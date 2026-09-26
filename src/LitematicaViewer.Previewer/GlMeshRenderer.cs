using System.Diagnostics;
using System.Numerics;
using Avalonia.OpenGL;
using LitematicaViewer.Previewer.Gpu;

namespace LitematicaViewer.Previewer;

// 有贴图的任意网格：Meshing 中间层的产物（pos3 + normal3 + uv2 交错，索引 uint）交给这里画。
// Previewer 不知道 Meshing 存在：装填只认数组，布局由本类的常量约定（与 MeshData 一一对应）。
//
// 数据到达与 GL 可用是两个互不相干的时间点（文件在后台解析时上下文早就开着；上下文丢失
// 恢复时数据还在手边），所以 GPU 侧的缓冲不跟着 Load 走、也不只在 Initialize 里建，
// 而是惰性：Render 发现有没建的东西就建。GL 调用仍然全部落在渲染回调里，R4 不破；
// 缓冲的销毁仍只在 Dispose / Abandon，R5 的「建」放宽到渲染回调是本类存在的理由。
internal sealed class GlMeshRenderer : IDisposable
{
    // pos3 + normal3 + uv2 + tint1。与 Meshing 的 MeshData.FloatsPerVertex 相同，
    // 但这里不给引用——两个工程零引用，布局改了会在下面的断言处炸出来。
    internal const int FloatsPerVertex = 9;

    private GlShader _shader;

    // 托管副本。调用方可能装完就改数组，渲染层拿到的必须是自己那一份。
    private float[]? _vertices;
    private int[]? _indices;
    private byte[][]? _atlasLevels;
    private int _atlasWidth;
    private int _atlasHeight;

    private GlMesh? _mesh;
    private GlTexture? _atlas;
    private bool _gpuDirty = true;
    private bool _shaderAbandoned;
    private bool _disposed;

    private GlMeshRenderer(GlShader shader)
    {
        _shader = shader;
    }

    // 上下文在的这段时间里有没有东西可画。装填过且没被清空。
    public bool HasMesh => _vertices is not null && _indices is not null;

    public static GlMeshRenderer Create(GlInterface gl)
    {
        GlShader shader = GlShader.Create(gl, VertexShaderSource, FragmentShaderSource);
        return new GlMeshRenderer(shader);
    }

    // 一次装齐网格与图集：uv 是按这张图集算出来的，分开装就会出现
    // 「顶点已经是新 uv、纹理还是上一张」的那一帧，画面上表现为颜色错乱一闪。
    // 全部 null 是清空（卸掉模型，回到演示立方体）；有网格就必须有图集。
    //
    // 这里只做拷贝，一个 GL 调用都没有：调用方可能在任何时刻调它（后台解析完通过
    // Dispatcher 推过来），而真正的上传等下一次 Render。
    public void Load(float[]? vertices, int[]? indices, byte[][]? atlasLevels, int atlasWidth, int atlasHeight)
    {
        Debug.Assert(
            (vertices is null) == (indices is null),
            $"[PREVIEWER][gl.mesh.load] 网格与索引必须同时给或同时不给 " +
            $"vertices={vertices?.Length.ToString() ?? "null"} indices={indices?.Length.ToString() ?? "null"}");
        Debug.Assert(
            vertices is null || (atlasLevels is not null && atlasLevels.Length > 0 && atlasWidth > 0 && atlasHeight > 0),
            "[PREVIEWER][gl.mesh.load] 有网格必须有图集：uv 采样没有别的来源");
        Debug.Assert(
            vertices is null || vertices.Length % FloatsPerVertex == 0,
            $"[PREVIEWER][gl.mesh.load] 顶点数据不是 {FloatsPerVertex} 的整数倍 " +
            $"len={vertices?.Length}");
        Debug.Assert(
            atlasLevels is null || (long)atlasLevels[0].Length == (long)atlasWidth * atlasHeight * 4,
            $"[PREVIEWER][gl.mesh.load] 图集层 0 数据长度对不上 bytes={atlasLevels?[0].Length} " +
            $"expected={(long)atlasWidth * atlasHeight * 4}");

        if (vertices is null)
        {
            bool had = HasMesh;
            _vertices = null;
            _indices = null;
            _atlasLevels = null;
            _gpuDirty = true;
            if (had)
            {
                Debug.WriteLine("[PREVIEWER][gl.mesh.load] cleared expected=卸载模型");
            }
            return;
        }

        // 非空由上面的 Debug.Assert 保证；编译器推不出复合条件里的非空，这里显式认定。
        // 拷贝是因为调用方可能装完就改数组：渲染层拿到的必须是自己那一份。
        _vertices = (float[])vertices!.Clone();
        _indices = (int[])indices!.Clone();
        _atlasLevels = atlasLevels!.Select(l => (byte[])l.Clone()).ToArray();
        _atlasWidth = atlasWidth;
        _atlasHeight = atlasHeight;
        _gpuDirty = true;

        Debug.WriteLine(
            $"[PREVIEWER][gl.mesh.load] vertices={_vertices.Length / FloatsPerVertex} " +
            $"indices={_indices.Length} atlas={atlasWidth}x{atlasHeight}");
    }

    public void Render(GlInterface gl, CameraState camera, int width, int height)
    {
        if (!HasMesh)
        {
            return;
        }

        // 惰性建（或上下文恢复后重建）。_gpuDirty 在 Abandon / Load / 清空后都为真，
        // 而 Abandon 之后旧引用已失效，必须全部重新建。
        if (_gpuDirty)
        {
            RebuildGpu(gl);
        }

        float aspect = (float)width / height;
        Matrix4x4 viewProjection = camera.GetViewMatrix() * camera.GetProjectionMatrix(aspect);

        _atlas!.Bind(0);
        _shader.Use();
        _shader.SetMatrix4("uViewProjection", viewProjection);

        // 两 pass：vanilla 的 opaque + translucent 同构。不透明先画并写深度，
        // 半透明后画、LEQUAL、不写深度——水的半透明才不会把后画的不透明方块
        // 挡成「隔着水看到背景洞」，共面的宿主面与水壁也稳定成「湿面」而不是
        // z-fight。glClear 的深度清空受 depth mask 影响，第二个 pass 结束必须还原。
        _shader.SetFloat("uOpaquePass", 1f);
        _mesh!.Draw();

        _shader.SetFloat("uOpaquePass", 0f);
        gl.DepthMask(0);
        _mesh.Draw();
        gl.DepthMask(1);
    }

    private void RebuildGpu(GlInterface gl)
    {
        // 上下文恢复（Abandon 之后的第一帧）时着色器也在丢弃之列：program 句柄已失效，
        // 拿去 Use 不会报错、只是什么都不画。源码是常量，重建不依赖任何外部状态。
        if (_shaderAbandoned)
        {
            _shader = GlShader.Create(gl, VertexShaderSource, FragmentShaderSource);
            _shaderAbandoned = false;
        }

        _mesh?.Dispose();
        _mesh = null;
        _atlas?.Dispose();
        _atlas = null;

        _atlas = GlTexture.Create(gl, _atlasLevels!, _atlasWidth, _atlasHeight);
        _mesh = GlMesh.Create(gl, _vertices!, _vertices!.Length / FloatsPerVertex, _indices!, [3, 3, 2, 1]);

        // 混合与深度语义是本渲染器的前置条件，自己设一遍（幂等），不赌别的渲染器
        // 的 Initialize 恰好先跑：alpha 输出靠 (SRC_ALPHA, ONE_MINUS_SRC_ALPHA) 混合
        // 才上屏，fb 的 alpha 通道由 (ZERO, ONE) 保护；半透明 pass 的稳定叠色要
        // LEQUAL（LESS 会让共面的宿主面与水壁落到插值噪声上互相抖）。
        bool blended = GlRaw.EnableAlphaBlend(gl);
        Debug.Assert(
            blended,
            "[PREVIEWER][gl.mesh] 这个上下文里没有 glBlendFuncSeparate，水的半透明会被画成不透明色块");
        gl.DepthFunc(GlRaw.GL_LEQUAL);

        _gpuDirty = false;

        Debug.WriteLine(
            $"[PREVIEWER][gl.mesh.gpu] rebuilt vertices={_vertices.Length / FloatsPerVertex} " +
            $"indices={_indices!.Length} atlas={_atlasWidth}x{_atlasHeight} note=惰性建缓冲");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _mesh?.Dispose();
        _atlas?.Dispose();
        _shader.Dispose();
        _vertices = null;
        _indices = null;
        _atlasLevels = null;
    }

    // 上下文丢失。与另外三个渲染器不同，这里只把 GPU 侧丢掉，托管数据留住：
    // 上下文恢复后不需要宿主重新装填，下一次 Render 自己把缓冲建回来。
    // 这也是本类不置 null 的原因——宿主那侧对 mesh 渲染器只调 Abandon，不引用置空。
    public void Abandon()
    {
        if (_disposed)
        {
            return;
        }

        _mesh?.Abandon();
        _mesh = null;
        _atlas?.Abandon();
        _atlas = null;
        _shader.Abandon();
        _shaderAbandoned = true;
        _gpuDirty = true;
    }

    private const string VertexShaderSource = """
        #version 300 es
        precision highp float;

        layout(location = 0) in vec3 aPosition;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec2 aUv;
        layout(location = 3) in float aTint;

        uniform mat4 uViewProjection;

        out vec3 vNormal;
        out vec2 vUv;
        out float vTint;

        void main()
        {
            vNormal = aNormal;
            vUv = aUv;
            vTint = aTint;
            gl_Position = uViewProjection * vec4(aPosition, 1.0);
        }
        """;

    private const string FragmentShaderSource = """
        #version 300 es
        precision highp float;
        precision highp sampler2D;

        in vec3 vNormal;
        in vec2 vUv;
        in float vTint;

        uniform sampler2D uAtlas;
        uniform float uOpaquePass;

        out vec4 fragColor;

        // 半透明与不透明的分界：水贴图 alpha=180（≈0.71），染色玻璃 ~0.75，
        // 挖孔贴图（树叶/玻璃板）是 0/255 二值。0.99 以上按不透明处理。
        const float TranslucentThreshold = 0.99;

        void main()
        {
            // 光照只用来给面分出朝向：纯贴图的六个面在截图里分不清谁是谁，
            // 一点方向光让顶面亮、底面暗，立体感来自明暗差而不是颜色差。
            // 光的方向固定在世界空间，相机怎么转明暗关系都不变。
            vec3 light = normalize(vec3(0.35, 0.9, 0.2));
            float diffuse = max(dot(normalize(vNormal), light), 0.0);
            float shade = 0.62 + 0.38 * diffuse;

            vec4 texel = texture(uAtlas, vUv);

            // alpha cutout：玻璃、树叶这类挖孔贴图，孔洞的 alpha 是 0，直接丢片元。
            // 阈值取 0.5 与 MC 一致。
            if (texel.a < 0.5)
            {
                discard;
            }

            // 两个 pass 分流（vanilla 的 opaque + translucent 同构）：
            // 不透明 pass 丢掉半透明片元并写深度；半透明 pass 只画水这类片元、
            // 不写深度。混在一个 pass 里的话，半透明水写进的深度会把之后画的
            // 不透明方块挡掉——隔着水看到背景洞，谁挡谁全看图元顺序。
            bool translucent = texel.a < TranslucentThreshold;
            if (uOpaquePass > 0.5 && translucent)
            {
                discard;
            }

            if (uOpaquePass < 0.5 && !translucent)
            {
                discard;
            }

            // 固定色板，槽号由网格侧按方块 id 归类写进顶点（0 不染 / 1 草 / 2 叶 / 3 水）。
            // 颜色取 plains 群系：草 #91BD59、叶 #77AB2F、水 #3F76E4。
            // 树叶/草的贴图本身是灰度图，不染就是用户看到的灰白。
            vec3 tint = vec3(1.0);
            if (vTint > 0.5 && vTint < 1.5)
            {
                tint = vec3(0.569, 0.741, 0.349);
            }
            else if (vTint > 1.5 && vTint < 2.5)
            {
                tint = vec3(0.467, 0.671, 0.184);
            }
            else if (vTint > 2.5)
            {
                tint = vec3(0.247, 0.463, 0.894);
            }

            // alpha 原样输出：贴图里的半透明（水 180≈0.71）是画面的真实信息，
            // 写死 1.0 会把水变成不透明色块吞掉带水宿主。帧缓冲的 alpha 通道由
            // glBlendFuncSeparate 的 (ZERO, ONE) 保护，不会漏进窗口合成。
            fragColor = vec4(texel.rgb * tint * shade, texel.a);
        }
        """;
}
