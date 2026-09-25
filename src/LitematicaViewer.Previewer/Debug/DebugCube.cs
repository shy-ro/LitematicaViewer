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

    // 整张 framebuffer 读回来，按颜色反推画面上出现了什么。
    // 这一条比「中心像素不是背景色」强得多：它同时证明了几何没画错、
    // 深度测试生效、投影矩阵没转置反。
    public static void CheckRendered(
        float[] vertices,
        byte[] rgba,
        int width,
        int height,
        Vector3 eye,
        Vector3 clearColor)
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

        int[] facePixels = new int[faceCount];
        int clearPixels = 0;
        int solidPixels = 0;
        int unmatched = 0;
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

                int best = -2;
                int bestDistance = Distance(r, g, b, clear);
                if (bestDistance <= SquaredTolerance)
                {
                    best = -1;
                }

                for (int face = 0; face < faceCount; face++)
                {
                    int distance = Distance(r, g, b, faceColors[face]);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = face;
                    }
                }

                if (bestDistance > SquaredTolerance)
                {
                    unmatched++;
                }
                else if (best == -1)
                {
                    clearPixels++;
                }
                else
                {
                    facePixels[best]++;
                    solidPixels++;
                    centroidX += x;
                    centroidY += y;
                }
            }
        }

        int pixelCount = width * height;
        int visibleFaces = 0;
        int backFacePixels = 0;
        int smallestVisibleFace = int.MaxValue;
        int loggedBackFacePixels = 0;

        for (int face = 0; face < faceCount; face++)
        {
            if (Vector3.Dot(faceNormals[face], eye) > 0f)
            {
                visibleFaces++;
                smallestVisibleFace = Math.Min(smallestVisibleFace, facePixels[face]);
            }
            else
            {
                backFacePixels += facePixels[face];
                if (facePixels[face] > 0 && loggedBackFacePixels < 8)
                {
                    loggedBackFacePixels++;
                    Debug.WriteLine(
                        $"[PREVIEWER][gl.cube.back] face={face} normal={faceNormals[face]} pixels={facePixels[face]}");
                }
            }

            Debug.WriteLine(
                $"[PREVIEWER][gl.cube.face] face={face} normal={faceNormals[face]} " +
                $"color={Byte(faceColors[face].R)},{Byte(faceColors[face].G)},{Byte(faceColors[face].B)} " +
                $"pixels={facePixels[face]} towardCamera={Vector3.Dot(faceNormals[face], eye) > 0f}");
        }

        float centroidPixelX = solidPixels == 0 ? 0f : (float)centroidX / solidPixels;
        float centroidPixelY = solidPixels == 0 ? 0f : (float)centroidY / solidPixels;

        Debug.WriteLine(
            $"[PREVIEWER][gl.cube.render] solid={solidPixels} clear={clearPixels} unmatched={unmatched} " +
            $"coverage={(float)solidPixels / pixelCount:P1} visibleFaces={visibleFaces} expected=3");
        Debug.WriteLine(
            $"[PREVIEWER][gl.cube.render] centroid=({centroidPixelX:F1},{centroidPixelY:F1}) " +
            $"expected=({width / 2f:F1},{height / 2f:F1}) y 轴方向无关，立方体的轮廓关于中心对称");

        Debug.Assert(solidPixels > 0, "[PREVIEWER][gl.cube] 画面里没有立方体，全是背景色");
        Debug.Assert(visibleFaces == 3, $"[PREVIEWER][gl.cube] 朝向相机的面是 {visibleFaces} 个，期望 3 个");
        Debug.Assert(
            smallestVisibleFace > solidPixels / 20,
            $"[PREVIEWER][gl.cube] 有可见面几乎没占到像素 smallest={smallestVisibleFace} solid={solidPixels}");

        // 背向相机的面只允许漏出极少量像素。真正会出错的是「一个都不许漏」这条判据本身：
        // 相邻两面共边，落在边上的像素中心会被两个三角形同时覆盖、深度相等，
        // 而 GL_LESS 是严格小于，先画的留下。共边处先画的是背向面时，就漏出一个像素。
        // 实测恰好 1 个。深度测试真失效的样子是整个轮廓只剩一种颜色（几万个），量级差着五个数量级，
        // 所以判据取占比而不是取零。要做到一个不漏得上多边形偏移，那是后面的事。
        Debug.Assert(
            backFacePixels <= pixelCount / 10000,
            $"[PREVIEWER][gl.cube] 画面上出现了大量背向相机的面 pixels={backFacePixels} " +
            $"of {pixelCount}，深度测试没生效");

        // 认不出来的像素只应该出现在三角形边缘，超过 1% 就不像是抗锯齿了。
        Debug.Assert(
            unmatched < pixelCount / 100,
            $"[PREVIEWER][gl.cube] 认不出的颜色过多 unmatched={unmatched} of {pixelCount}");

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
