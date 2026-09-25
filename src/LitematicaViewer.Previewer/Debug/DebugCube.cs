using System.Diagnostics;
using System.Numerics;

namespace LitematicaViewer.Previewer.Diagnostics;

// 命名空间不叫 Debug：那样会跟 System.Diagnostics.Debug 撞名，`Debug.Assert` 这种写法
// 在命名空间内部就会解析到作用域链上的另一头去。
//
// 这里是立方体的不变量检查。「看到立方体」这条验收项没法靠读日志成立，
// 所以把整张 framebuffer 读回来，按颜色反推画面上到底出现了什么。
internal static class DebugCube
{
    // 颜色匹配允许的每通道偏差。实测 unmatched=0：帧缓冲没有多重采样，边缘是硬切的，
    // 每个像素都精确落在某个面的颜色上。留这点余量是防御性的，换个后端或开了 MSAA 就会用到。
    private const int ChannelTolerance = 8;

    private const int SquaredTolerance = 3 * ChannelTolerance * ChannelTolerance;

    // 一个朝向相机的面，实际像素数允许比几何期望少到这个比例。
    //
    // 期望值只有一个数是算出来的（面心到相机的距离、面法线与视线的夹角），而一个面横跨
    // 一段深度、掠射时更明显，加上共边像素的归属不唯一（GL_LESS 严格小于，先画的留下），
    // 实际值在这个期望的上下浮动两三成是正常的。留到 0.35 是为了只抓「数量级不对」——
    // 渲染器没在用当前这个相机时，观测到的面要么整个消失（0 像素），要么是一整块别的面，
    // 与期望差的是数量级，不是两三成。
    private const float MinFacePixelRatio = 0.35f;

    // 期望像素低于这个数就不再断言。几个像素的差别里，光栅化的量化误差比信号还大：
    // 相机掠过某个面的平面时那个面本来就只有十几个像素。
    private const int RasterNoiseFloor = 8;

    public static void CheckGeometry(float[] vertices, int[] indices, int faceCount)
    {
        int vertexCount = faceCount * 4;
        Debug.Assert(
            vertices.Length == vertexCount * GlCubeRenderer.FloatsPerVertex,
            $"[PREVIEWER][gl.cube] 顶点数不对 floats={vertices.Length} expected={vertexCount * GlCubeRenderer.FloatsPerVertex}");
        Debug.Assert(
            indices.Length == faceCount * 6,
            $"[PREVIEWER][gl.cube] 索引数不对 indices={indices.Length} expected={faceCount * 6}");

        HashSet<Vector3> positions = [];
        HashSet<Vector3> normals = [];

        for (int face = 0; face < faceCount; face++)
        {
            int first = face * 4;
            Vector3 normal = ReadVector3(vertices, first, 3);

            Debug.Assert(
                MathF.Abs(normal.Length() - 1f) < 1e-6f,
                $"[PREVIEWER][gl.cube] 法线不是单位向量 face={face} normal={normal}");
            Debug.Assert(
                IsAxisAligned(normal),
                $"[PREVIEWER][gl.cube] 法线不是轴向 face={face} normal={normal}");
            normals.Add(normal);

            for (int corner = 0; corner < 4; corner++)
            {
                int vertex = first + corner;
                Vector3 position = ReadVector3(vertices, vertex, 0);
                Vector3 vertexNormal = ReadVector3(vertices, vertex, 3);

                // 同一面的四个顶点法线必须完全一致，否则面上会出现渐变，读回像素的期望值就不成立了。
                Debug.Assert(
                    vertexNormal == normal,
                    $"[PREVIEWER][gl.cube] 同面法线不一致 face={face} corner={corner} " +
                    $"normal={vertexNormal} expected={normal}");

                // 顶点在法线方向上的投影必须是 0.5：立方体中心在原点，半边长就是 0.5。
                Debug.Assert(
                    MathF.Abs(Vector3.Dot(position, normal) - 0.5f) < 1e-6f,
                    $"[PREVIEWER][gl.cube] 顶点不在该面的平面上 face={face} position={position} normal={normal}");

                // 两个切向上必须正好落在 ±0.5，否则画出来的不是立方体。
                for (int axis = 0; axis < 3; axis++)
                {
                    if (Component(normal, axis) != 0f)
                    {
                        continue;
                    }

                    Debug.Assert(
                        MathF.Abs(MathF.Abs(Component(position, axis)) - 0.5f) < 1e-6f,
                        $"[PREVIEWER][gl.cube] 切向分量不是 0.5 face={face} axis={axis} position={position}");
                }

                positions.Add(position);
            }

            // 两组三角形都只能引用本面的四个顶点。跨面引用不会报错，只会画出一堆穿插的三角形。
            for (int i = 0; i < 6; i++)
            {
                int index = indices[(face * 6) + i];
                Debug.Assert(
                    index >= first && index < first + 4,
                    $"[PREVIEWER][gl.cube] 索引跨面 face={face} index={index} expected=[{first},{first + 4})");
            }
        }

        Debug.Assert(normals.Count == faceCount, $"[PREVIEWER][gl.cube] 法线有重复 count={normals.Count}");
        Debug.Assert(positions.Count == 8, $"[PREVIEWER][gl.cube] 不同顶点数不是 8 count={positions.Count}");

        // 八个角到中心的距离相等才是个正立方体。
        float expectedRadius = MathF.Sqrt(3f) / 2f;
        foreach (Vector3 position in positions)
        {
            Debug.Assert(
                MathF.Abs(position.Length() - expectedRadius) < 1e-6f,
                $"[PREVIEWER][gl.cube] 顶点不在单位立方体上 position={position} radius={position.Length()}");
        }
    }

    // 这套像素校验只在特定姿态下有定义，先把前提算出来。Phase F 起相机可以平移、可以无界缩放，
    // 会有相当一部分姿态本来就验不了——那些姿态下必须明说「跳过」，不能一声不吭地放过去。
    //
    // 两条前提：
    // 一、相机看向立方体中心（原点）。轮廓关于投影中心对称只在看向中心时成立，
    //     而那正是「质心落在画面中心」那条断言的全部依据。相机现在可以转头、可以平移，
    //     转头之后立方体本来就会离开画面中央，那不是 bug。用视线方向当判据不会把检查架空：
    //     ForwardOf 的三个轴向由 CameraState.VerifyConvention 单独钉着，这里不承担那个职责。
    // 二、立方体完整落在视口里，而且不能小到只剩几十个像素。判据用外接球在画面里的占比：
    //     「轮廓被视口切掉一部分」与「覆盖率落在 2%~50%」都是像素计数断言的隐含前提，
    //     凑到半屏、或者远到只剩几个像素时，那些断言就不再说明任何事。
    //     覆盖率那两条的上下界反推出距离窗口约 [1.9, 9.6] 个世界单位，这里取 [0.30, 0.85] 的占比，
    //     对应约 [2.4, 7.0]，两头都留了余量。
    //
    // 判据只用相机自己的参数（位置、朝向、fov、宽高比），不碰投影矩阵：
    // 投影矩阵漏了转置时，质心那条断言正是要红的——把投影算进前提里等于把那条检查自己关掉。
    internal static bool IsVerifiable(CameraState camera, int width, int height, out string reason)
    {
        // 边长 1 的立方体外接球半径 √3/2。立方体在原点，这是渲染器写死的。
        const float BoundingRadius = 0.866f;

        Vector3 toCenter = -camera.Position;
        float distance = toCenter.Length();

        if (!float.IsFinite(distance) || distance <= 0f)
        {
            reason = $"相机压在立方体中心上 distance={distance}";
            return false;
        }

        float alignment = Vector3.Dot(camera.Forward, toCenter / distance);
        if (alignment < 0.9999f)
        {
            reason = $"相机没看向立方体中心 alignment={alignment:F6}（自由转头之后属于预期）";
            return false;
        }

        float halfHeight = distance * MathF.Tan(float.DegreesToRadians(camera.Fov) / 2f);
        float aspect = height == 0 ? 1f : width / (float)height;

        // 取紧的那条轴：窗口比高还窄时，装不装得下由宽度说了算。
        float fraction = BoundingRadius / (halfHeight * MathF.Min(1f, aspect));
        if (fraction is > 0.85f or < 0.30f)
        {
            reason = $"立方体在画面里的占比不在可用区间 fraction={fraction:F3} range=[0.3,0.85] " +
                $"distance={distance:F3} fov={camera.Fov}";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    // 整张 framebuffer 读回来，按颜色反推画面上出现了什么。
    // 这一条比「中心像素不是背景色」强得多：它同时证明了几何没画错、
    // 深度测试生效、投影矩阵没转置反，以及——相机确实是从外面传进来的那个。
    //
    // 调用方必须先过 IsVerifiable：质心与覆盖率那两条断言只在看向中心、且完整可见的姿态下有定义。
    public static void CheckRendered(
        float[] vertices,
        byte[] rgba,
        int width,
        int height,
        CameraState camera,
        Vector3 clearColor,
        bool pedestalVisible)
    {
        int faceCount = vertices.Length / (GlCubeRenderer.FloatsPerVertex * 4);
        (float R, float G, float B)[] faceColors = new (float, float, float)[faceCount];
        Vector3[] faceNormals = new Vector3[faceCount];

        for (int face = 0; face < faceCount; face++)
        {
            faceNormals[face] = ReadVector3(vertices, face * 4, 3);
            faceColors[face] = ShaderColor(faceNormals[face]);
        }

        // 背景色不走着色器，是 clear 直接写进 framebuffer 的，所以不能套法线那一套变换。
        (float R, float G, float B) clear = (clearColor.X, clearColor.Y, clearColor.Z);

        int axisCount = GlAxesRenderer.Axes.Length;

        // 底色：0 是背景，往后依次是六个面。
        int baseCount = faceCount + 1;
        (float R, float G, float B)[] baseColors = new (float, float, float)[baseCount];
        baseColors[0] = clear;
        for (int face = 0; face < faceCount; face++)
        {
            baseColors[face + 1] = faceColors[face];
        }

        // 候选颜色一次备齐：七种底色，加上「每根轴压在每种底色上」的二十一种混合结果。
        //
        // 为什么不能分成两趟（先认底色、再看是不是某根轴压在上面）：混过的像素离底色已经很远，
        // 而它往往更接近另一个**面色**——黄色压在背景上得到的那个颜色，离「Y 面那个黄」比离背景近。
        // 于是第一步就把底色认错了，第二步在错的底色上怎么混都对不上。
        // 二十八种候选放在一起取最近的，就没有这个先后问题。
        //
        // 为什么非认出来不可：轴线是真的改了像素的，而认不出色的像素有一个 1% 的预算。
        // 几条线当然吃得下，但那样一来那条断言的余量就取决于线有多长，而不是取决于画得对不对——
        // 「判据的强度被一个无关参数悄悄带走」正是要避免的。
        // 展台光环也改像素，而且改的比轴线多两个数量级（它是一整圈半透明的带子），
        // 但它的 alpha 是连续插值出来的，进不了这张表——理由见下。
        // 展台光环不进这张表。它的 alpha 是顶点插值出来的**连续**值（内带 0.14→0.42、
        // 中带 0.42、外带 0.42→0.14），而枚举一个连续统需要无穷多个候选色。
        //
        // 曾经按「两档 alpha」列进表里，结果真机上一跑就红：unmatched=10751（1.18%）。
        // 根因是那张表的注释写错了——「相邻两圈同值于是带内是常量」只对中带成立，
        // 内外两个带的两端分别是 0.14 与 0.42，插值出来是一整段渐变，只有贴到端点的那几行像素才认得出。
        // 所以光环走另一条判据（见 TryPedestalAlpha）：像素落在「底色 → 不透明光环色」这条线段上，
        // 落点参数就是它的 alpha，再要求这个 alpha 落在声明的区间里。
        int candidateCount = baseCount + (baseCount * axisCount);
        (float R, float G, float B)[] candidates = new (float, float, float)[candidateCount];
        int[] candidateAxis = new int[candidateCount];

        for (int baseIndex = 0; baseIndex < baseCount; baseIndex++)
        {
            candidates[baseIndex] = baseColors[baseIndex];
            candidateAxis[baseIndex] = -1;
        }

        for (int baseIndex = 0; baseIndex < baseCount; baseIndex++)
        {
            for (int axis = 0; axis < axisCount; axis++)
            {
                Vector3 axisColor = GlAxesRenderer.Axes[axis].Color;
                int index = baseCount + (baseIndex * axisCount) + axis;
                candidates[index] = PixelBlend.Over(
                    (axisColor.X, axisColor.Y, axisColor.Z),
                    baseColors[baseIndex],
                    GlAxesRenderer.Alpha);
                candidateAxis[index] = axis;
            }
        }

        int[] facePixels = new int[faceCount];
        int[] axisPixels = new int[axisCount];
        int clearPixels = 0;
        int solidPixels = 0;
        int unmatched = 0;
        int pedestalPixels = 0;
        float minPedestalAlpha = float.MaxValue;
        float maxPedestalAlpha = float.MinValue;
        long centroidX = 0;
        long centroidY = 0;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = ((y * width) + x) * 4;
                byte r = rgba[offset];
                byte g = rgba[offset + 1];
                byte b = rgba[offset + 2];

                int best = -1;
                int bestDistance = int.MaxValue;

                for (int candidate = 0; candidate < candidateCount; candidate++)
                {
                    int distance = Distance(r, g, b, candidates[candidate]);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = candidate;
                    }
                }

                // 顺序要紧：先判「认不出来」，再判认出来的是谁。
                // 反过来的话，一个既不像底色也不像任何混合色的像素会被就近归给某根轴，
                // 于是「认不出的颜色」那条断言永远不会有东西可报。
                if (bestDistance > SquaredTolerance)
                {
                    // 底色与轴线都认不出来，才轮到光环。放这么后面是有意的：
                    // 光环那条判据是「离一条线段足够近」，比「离一个点足够近」宽松得多——
                    // 先让它试的话，正好落在那条线段附近的背景色或面色会被它抢走，
                    // 于是面像素少几块、光环像素多几块，而两条断言都看不出异常。
                    if (pedestalVisible
                        && TryPedestalAlpha(r, g, b, candidates, out float alpha))
                    {
                        // 光环像素不进 solidPixels，也不进质心：它落在底面上、围着方块，
                        // 按面积算比轮廓的一部分还大，混进去会让覆盖率和质心两条
                        // 一起变成「光环有多大」的函数。
                        pedestalPixels++;
                        minPedestalAlpha = MathF.Min(minPedestalAlpha, alpha);
                        maxPedestalAlpha = MathF.Max(maxPedestalAlpha, alpha);
                    }
                    else
                    {
                        unmatched++;
                    }
                }
                else if (candidateAxis[best] >= 0)
                {
                    // 轴线像素不进 solidPixels，也不进质心：那两条断言问的是立方体，
                    // 而线是画在方块前面（或者旁边）的东西，跟轮廓对称性没关系。
                    axisPixels[candidateAxis[best]]++;
                }
                else if (best == 0)
                {
                    clearPixels++;
                }
                else
                {
                    int face = best - 1;
                    facePixels[face]++;
                    solidPixels++;
                    centroidX += x;
                    centroidY += y;
                }
            }
        }

        int pixelCount = width * height;
        int visibleFaces = 0;
        int backFacePixels = 0;
        int loggedBackFacePixels = 0;

        // 背向相机的面最多允许漏出多少像素。这一条判的是「深度测试还在不在」，
        // 而漏出来的机理是共边处两个三角形深度相等、GL_LESS 让先画的留下，
        // 所以它与轮廓的周长同量级，与面积无关。实测正常时恰好 1 个；
        // 给个绝对下限是因为小窗口下 pixelCount/10000 会掉到个位数，那时它比周长还小。
        int backFaceAllowance = Math.Max(16, pixelCount / 10000);

        // 投影的尺度。半高等于 depth * tan(fov/2)，半宽再乘宽高比；
        // 于是距离 depth 处的可见世界面积是 4 * halfHeight * halfWidth。
        float tanHalfFov = MathF.Tan(float.DegreesToRadians(camera.Fov) / 2f);
        float aspect = height == 0 ? 1f : width / (float)height;

        for (int face = 0; face < faceCount; face++)
        {
            Vector3 normal = faceNormals[face];

            // 一个面能不能被看到，取决于相机在不在它所在平面的外侧。判据是
            // dot(normal, camera - faceCenter)，不是 dot(normal, camera)：
            // 后者漏掉了面心到原点的 0.5，相机贴近时会把已经侧转过去的面也算成可见。
            Vector3 toFace = camera.Position - (normal * 0.5f);
            float incidence = Vector3.Dot(normal, toFace);
            bool expectedVisible = incidence > 0f;

            // 这一面在画面上该有多少像素：单位立方体的面面积是 1，投影后的面积是
            // incidence / |toFace|（入射角的余弦），除以该深度处可见的世界面积再乘总像素数。
            //
            // 为什么不能用一个写死的门槛（原来写的是「至少占画面万分之一」，1024x768 下是 78 个像素）：
            // 相机掠过某个面的平面时，那个面与视线的夹角趋近 90 度，投影面积连续地趋近 0——
            // 它确实朝向相机（上面的判据成立），但只剩几十个像素是几何的必然，不是画错了。
            // Phase F 之前相机只能沿一条固定的体对角线推拉，永远碰不到掠射姿态，所以那个门槛
            // 一直没暴露；相机能自由转之后，任意一张面扫过镜头都会触发它，而 Debug.Assert
            // 失败是直接终止进程——表现成「Debug 下转着转着就崩了」。
            float expectedPixels = 0f;
            if (expectedVisible)
            {
                float depth = toFace.Length();
                float halfHeight = depth * tanHalfFov;
                float halfWidth = halfHeight * aspect;
                expectedPixels = pixelCount * (incidence / depth) / (4f * halfHeight * halfWidth);
            }

            Debug.WriteLine(
                $"[PREVIEWER][gl.cube.face] face={face} normal={normal} " +
                $"color={Byte(faceColors[face].R)},{Byte(faceColors[face].G)},{Byte(faceColors[face].B)} " +
                $"pixels={facePixels[face]} expected={expectedPixels:F0} expectedVisible={expectedVisible}");

            // 观测到的可见面必须与相机推出来的集合完全一致。这条是「SetCamera 真的驱动了渲染」的证据：
            // 渲染器若还在用某个写死的相机，观测集合就会和当前相机算出来的对不上。
            if (expectedVisible)
            {
                visibleFaces++;

                if (expectedPixels >= RasterNoiseFloor)
                {
                    Debug.Assert(
                        facePixels[face] >= expectedPixels * MinFacePixelRatio,
                        $"[PREVIEWER][gl.cube] 朝向相机的面画得比几何期望少太多 face={face} " +
                        $"normal={normal} pixels={facePixels[face]} expected={expectedPixels:F1} " +
                        $"ratio={MinFacePixelRatio} note=渲染器可能没在用当前这个相机");
                }
            }
            else
            {
                backFacePixels += facePixels[face];
                Debug.Assert(
                    facePixels[face] <= backFaceAllowance,
                    $"[PREVIEWER][gl.cube] 背向相机的面画出来了 face={face} normal={normal} " +
                    $"pixels={facePixels[face]} allowed={backFaceAllowance}，深度测试或者相机没接上");

                if (facePixels[face] > 0 && loggedBackFacePixels < 8)
                {
                    loggedBackFacePixels++;
                    Debug.WriteLine(
                        $"[PREVIEWER][gl.cube.back] face={face} normal={normal} pixels={facePixels[face]} " +
                        $"note=共边像素的归属，GL_LESS 严格小于的语义让先画的留下");
                }
            }
        }

        float centroidPixelX = solidPixels == 0 ? 0f : (float)centroidX / solidPixels;
        float centroidPixelY = solidPixels == 0 ? 0f : (float)centroidY / solidPixels;

        int axisTotal = 0;
        foreach (int count in axisPixels)
        {
            axisTotal += count;
        }

        Debug.WriteLine(
            $"[PREVIEWER][gl.cube.render] solid={solidPixels} clear={clearPixels} unmatched={unmatched} " +
            $"coverage={(float)solidPixels / pixelCount:P1} visibleFaces={visibleFaces} " +
            $"backFacePixels={backFacePixels} axisPixels={axisTotal} pedestalPixels={pedestalPixels} " +
            $"pos=({camera.Position}) yaw={camera.Yaw:F2} pitch={camera.Pitch:F2}");

        if (pedestalVisible)
        {
            // 认出来的 alpha 区间是这条判据的核心证据：像素的颜色决定了它落在
            // 「底色 → 不透明光环色」那条线上的哪个位置，而那个位置就是这一点上的 alpha。
            Debug.WriteLine(
                $"[PREVIEWER][gl.pedestal.band] pixels={pedestalPixels} " +
                $"alphaObserved=[{Bound(minPedestalAlpha)},{Bound(maxPedestalAlpha)}] " +
                $"alphaDeclared=[{Bound(PedestalAlphaMin)},{Bound(PedestalAlphaMax)}] " +
                $"color={Byte(GlPedestalRenderer.Color.X)},{Byte(GlPedestalRenderer.Color.Y)}," +
                $"{Byte(GlPedestalRenderer.Color.Z)}");
        }

        if (pedestalVisible)
        {
            // 这条只在展台模式下判：自由视角下光环根本不画，认不出它是正常的。
            // 门槛取「大于零」而不是估出来的面积：光环投影出来的面积随俯仰角从几万像素掉到几千，
            // 拿一个数去卡它等于把断言绑在相机的姿态上；而「画没画」是个是非题。
            Debug.Assert(
                pedestalPixels > 0,
                $"[PREVIEWER][gl.cube] 展台光环一个像素都没认出来 pedestalVisible=True " +
                $"pos=({camera.Position}) pitch={camera.Pitch:F2} " +
                $"note=要么没画，要么被整个盖住了，要么颜色与声明的 alpha 区间对不上");

            // 两端都要够到。这一条判的是「软边还在不在」：顶点 alpha 写成常量（整圈一样亮）
            // 或者把内外两圈写反的时候，像素数照样是几万，「画出来了」那条全绿，
            // 只有观测到的区间会缩到一点上。
            Debug.Assert(
                minPedestalAlpha <= PedestalAlphaMin + AlphaReachSlack
                && maxPedestalAlpha >= PedestalAlphaMax - AlphaReachSlack,
                $"[PREVIEWER][gl.cube] 光环的 alpha 没铺满声明的区间 " +
                $"observed=[{Bound(minPedestalAlpha)},{Bound(maxPedestalAlpha)}] " +
                $"declared=[{Bound(PedestalAlphaMin)},{Bound(PedestalAlphaMax)}] " +
                $"note=内缘与外缘那两个 0.14 的两端都要有像素；只观测到一个值说明软边没了");
        }

        // 逐轴打一遍：三条线的像素数差着量级是正常的（正对着镜头的那根投影成一段，
        // 与视线垂直的那根投影成一个点），所以这里只记不判——
        // 「轴线到底画出来没有」在屏幕上是一眼的事，而数据对不对由 DebugAxes 在初始化时守着。
        for (int axis = 0; axis < axisCount; axis++)
        {
            Vector3 axisColor = GlAxesRenderer.Axes[axis].Color;
            Debug.WriteLine(
                $"[PREVIEWER][gl.axes.axis] axis={axis} color=" +
                $"{Byte(axisColor.X)},{Byte(axisColor.Y)},{Byte(axisColor.Z)} pixels={axisPixels[axis]}");
        }

        Debug.WriteLine(
            $"[PREVIEWER][gl.cube.render] centroid=({centroidPixelX:F1},{centroidPixelY:F1}) " +
            $"expected=({width / 2f:F1},{height / 2f:F1}) 前提=相机看向立方体中心，轮廓关于中心对称");

        Debug.Assert(solidPixels > 0, "[PREVIEWER][gl.cube] 画面里没有立方体，全是背景色");
        Debug.Assert(visibleFaces > 0, "[PREVIEWER][gl.cube] 一个朝向相机的面都没有");

        // 这里原本还有一条「最小的可见面至少占固体的 5%」。它和上面那条写死的门槛是同一个前提
        // 的两种写法：都在假设「几个可见面的面积是同一个量级」——那只在相机沿固定体对角线看时成立。
        // 相机能自由转之后，一个掠射面的合法面积可以比旁边正对着的面小三个数量级，
        // 这条断言就会把正确的画面判成错的。逐面的几何期望已经覆盖了它想抓的东西。

        // 背向相机的面只允许漏出极少量像素（逐面判据在上面，这里只兜总数）。
        // 真正会出错的是「一个都不许漏」这条判据本身：相邻两面共边，落在边上的像素中心
        // 会被两个三角形同时覆盖、深度相等，而 GL_LESS 是严格小于，先画的留下；
        // 共边处先画的是背向面时，就漏出一个像素。实测恰好 1 个。深度测试真失效的样子是
        // 整个轮廓只剩一种颜色（几万个），量级差着五个数量级。要做到一个不漏得上多边形偏移。
        Debug.Assert(
            backFacePixels <= backFaceAllowance,
            $"[PREVIEWER][gl.cube] 背向相机的面漏出过多 pixels={backFacePixels} allowed={backFaceAllowance}");

        // 认不出来的像素只应该出现在三角形边缘（轴线像素上面单独认走了，不算在内），
        // 超过 1% 就不像是抗锯齿了。
        // 余量留在 1% 而不是贴着零：边是硬切的，但两个三角形共边那一行像素的归属
        // 会在 GL_LESS 的严格小于语义下随机落到哪一边，而那种像素的颜色是混合出来的。
        Debug.Assert(
            unmatched < pixelCount / 100,
            $"[PREVIEWER][gl.cube] 认不出的颜色过多 unmatched={unmatched} of {pixelCount} " +
            $"note=按颜色反推的这套只认识背景、六个面和轴线");

        // 立方体关于中心对称，轮廓的质心必然落在投影中心上。
        // 投影矩阵漏了转置、或者宽高比算错，这条就会偏出去。
        float allowedDrift = width * 0.03f;
        Debug.Assert(
            MathF.Abs(centroidPixelX - (width / 2f)) <= allowedDrift &&
            MathF.Abs(centroidPixelY - (height / 2f)) <= allowedDrift,
            $"[PREVIEWER][gl.cube] 轮廓质心偏离画面中心 centroid=({centroidPixelX:F1},{centroidPixelY:F1}) " +
            $"center=({width / 2f:F1},{height / 2f:F1}) allowed={allowedDrift:F1}");

        // 覆盖率为零是没画，接近全屏是投影参数错了。
        float coverage = (float)solidPixels / pixelCount;
        Debug.Assert(
            coverage is > 0.02f and < 0.5f,
            $"[PREVIEWER][gl.cube] 覆盖率不在合理范围内 coverage={coverage:P1}");
    }

    // 着色器里那一行 `vNormal * 0.5 + 0.5` 的 CPU 版本，两边必须一致。
    private static (float R, float G, float B) ShaderColor(Vector3 normal) =>
        ((normal.X * 0.5f) + 0.5f, (normal.Y * 0.5f) + 0.5f, (normal.Z * 0.5f) + 0.5f);

    private static int Distance(byte r, byte g, byte b, (float R, float G, float B) expected)
    {
        int dr = r - Byte(expected.R);
        int dg = g - Byte(expected.G);
        int db = b - Byte(expected.B);
        return (dr * dr) + (dg * dg) + (db * db);
    }

    private static byte Byte(float value) => (byte)Math.Clamp(MathF.Round(value * 255f), 0f, 255f);

    private static string Bound(float value) =>
        value == float.MaxValue || value == float.MinValue ? "无" : value.ToString("F3");

    // 声明的 alpha 区间：光环顶点上那组 alpha 的最小与最大。
    private static float PedestalAlphaMin => MinAlpha();

    private static float PedestalAlphaMax => MaxAlpha();

    // 把「离一个点足够近」换成「离一条线段足够近」时要放宽的那一点余量，两处各用一份：
    //
    // 一是判据本身：alpha 是顶点插值出来的，混完再取整成 8 位，所以反推回来的那个参数
    // 会带上千分之几的抖动，落在声明的区间外面一点点是正常的。
    //
    // 二是「两端都要够到」那条：贴到内缘的那一行像素，alpha 比 0.14 大一点点（渐变是从这里起步的），
    // 所以断言不能要求恰好等于 0.14。
    private const float AlphaSlack = 0.05f;

    private const float AlphaReachSlack = 0.06f;

    private static float MinAlpha()
    {
        float min = float.MaxValue;
        foreach (float alpha in GlPedestalRenderer.RingAlphas)
        {
            min = MathF.Min(min, alpha);
        }

        return min;
    }

    private static float MaxAlpha()
    {
        float max = float.MinValue;
        foreach (float alpha in GlPedestalRenderer.RingAlphas)
        {
            max = MathF.Max(max, alpha);
        }

        return max;
    }

    // 这个像素是不是光环画出来的，如果是，它的 alpha 是多少。
    //
    // 光环的顶点 alpha 从 0.14 渐变到 0.42，混出来的颜色因此是一条**线段**上的点：
    // 从「这个像素底下的那个颜色」出发，指向不透明光环色。落在线上哪个位置，就是那一点的 alpha
    // （Over 那条式子在 alpha 上是线性的）。所以判据是：
    // 到这条线段足够近，而且落点参数落在声明的 alpha 区间里。
    //
    // 用参数而不是枚举颜色：枚举是一张有限表，而渐变是连续统——表上差一点就是「认不出来」，
    // 而那几万个像素会把「认不出的颜色」的预算整个吃掉。参数化之后判据是精确的，
    // 而且顺带把 alpha 本身也量出来了（画成不透明时落点是 1，立刻出界）。
    //
    // 根（线段的起点）就是底色那张表：背景、六个面、以及轴线压在它们上面的那二十一种结果。
    // 光环压在轴线上时底色正是后者，所以这一份表天然覆盖了「光环压着轴线」的那些像素。
    private static bool TryPedestalAlpha(
        byte r,
        byte g,
        byte b,
        (float R, float G, float B)[] roots,
        out float alpha)
    {
        float px = r;
        float py = g;
        float pz = b;

        // 线段的终点：alpha = 1，也就是光环色本身。
        float endR = Byte(GlPedestalRenderer.Color.X);
        float endG = Byte(GlPedestalRenderer.Color.Y);
        float endB = Byte(GlPedestalRenderer.Color.Z);

        float minAlpha = PedestalAlphaMin - AlphaSlack;
        float maxAlpha = PedestalAlphaMax + AlphaSlack;

        int bestError = SquaredTolerance + 1;
        alpha = 0f;

        foreach ((float R, float G, float B) root in roots)
        {
            float startR = Byte(root.R);
            float startG = Byte(root.G);
            float startB = Byte(root.B);

            float dx = endR - startR;
            float dy = endG - startG;
            float dz = endB - startB;

            float lengthSquared = (dx * dx) + (dy * dy) + (dz * dz);

            // 底色和不透明光环色几乎重合时这条线段退化了：投影出来除不了一个有效的长度，
            // 而且这种底色本来也认不出「上面是不是压着光环」——跳过它。
            if (lengthSquared < 1f)
            {
                continue;
            }

            float t = ((((px - startR) * dx) + ((py - startG) * dy) + ((pz - startB) * dz)) / lengthSquared);

            if (t < minAlpha || t > maxAlpha)
            {
                continue;
            }

            float cr = startR + (t * dx);
            float cg = startG + (t * dy);
            float cb = startB + (t * dz);

            float dr = px - cr;
            float dg = py - cg;
            float db = pz - cb;
            int error = (int)((dr * dr) + (dg * dg) + (db * db));

            if (error < bestError)
            {
                bestError = error;
                alpha = t;
            }
        }

        return bestError <= SquaredTolerance;
    }

    private static bool IsAxisAligned(Vector3 normal)
    {
        int nonZero = 0;
        for (int axis = 0; axis < 3; axis++)
        {
            float component = Component(normal, axis);
            if (component == 0f)
            {
                continue;
            }

            if (MathF.Abs(MathF.Abs(component) - 1f) > 1e-6f)
            {
                return false;
            }

            nonZero++;
        }

        return nonZero == 1;
    }

    private static Vector3 ReadVector3(float[] data, int vertex, int offset)
    {
        int start = (vertex * GlCubeRenderer.FloatsPerVertex) + offset;
        return new Vector3(data[start], data[start + 1], data[start + 2]);
    }

    private static float Component(Vector3 v, int axis) => axis switch
    {
        0 => v.X,
        1 => v.Y,
        _ => v.Z,
    };
}
