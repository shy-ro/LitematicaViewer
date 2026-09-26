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
    // pos3 + normal3 + uv2。与 Meshing 的 MeshData.FloatsPerVertex 相同，
    // 但这里不给引用——两个工程零引用，布局改了会在下面的断言处炸出来。
    internal const int FloatsPerVertex = 8;

    private GlShader _shader;

    // 托管副本。调用方可能装完就改数组，渲染层拿到的必须是自己那一份。
    private float[]? _vertices;
    private int[]? _indices;
    private byte[]? _atlasRgba;
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
    public void Load(float[]? vertices, int[]? indices, byte[]? atlasRgba, int atlasWidth, int atlasHeight)
    {
        Debug.Assert(
            (vertices is null) == (indices is null),
            $"[PREVIEWER][gl.mesh.load] 网格与索引必须同时给或同时不给 " +
            $"vertices={vertices?.Length.ToString() ?? "null"} indices={indices?.Length.ToString() ?? "null"}");
        Debug.Assert(
            vertices is null || (atlasRgba is not null && atlasWidth > 0 && atlasHeight > 0),
            "[PREVIEWER][gl.mesh.load] 有网格必须有图集：uv 采样没有别的来源");
        Debug.Assert(
            vertices is null || vertices.Length % FloatsPerVertex == 0,
            $"[PREVIEWER][gl.mesh.load] 顶点数据不是 {FloatsPerVertex} 的整数倍 " +
            $"len={vertices?.Length}");
        Debug.Assert(
            atlasRgba is null || (long)atlasRgba.Length == (long)atlasWidth * atlasHeight * 4,
            $"[PREVIEWER][gl.mesh.load] 图集数据长度对不上 bytes={atlasRgba?.Length} " +
            $"expected={(long)atlasWidth * atlasHeight * 4}");

        if (vertices is null)
        {
            bool had = HasMesh;
            _vertices = null;
            _indices = null;
            _atlasRgba = null;
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
        _atlasRgba = (byte[])atlasRgba!.Clone();
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
        _mesh!.Draw();
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

        _atlas = GlTexture.Create(gl, _atlasRgba!, _atlasWidth, _atlasHeight);
        _mesh = GlMesh.Create(gl, _vertices!, _vertices!.Length / FloatsPerVertex, _indices!, [3, 3, 2]);
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
        _atlasRgba = null;
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

        uniform mat4 uViewProjection;

        out vec3 vNormal;
        out vec2 vUv;

        void main()
        {
            vNormal = aNormal;
            vUv = aUv;
            gl_Position = uViewProjection * vec4(aPosition, 1.0);
        }
        """;

    private const string FragmentShaderSource = """
        #version 300 es
        precision highp float;
        precision highp sampler2D;

        in vec3 vNormal;
        in vec2 vUv;

        uniform sampler2D uAtlas;

        out vec4 fragColor;

        void main()
        {
            // 光照只用来给面分出朝向：纯贴图的六个面在截图里分不清谁是谁，
            // 一点方向光让顶面亮、底面暗，立体感来自明暗差而不是颜色差。
            // 光的方向固定在世界空间，相机怎么转明暗关系都不变。
            vec3 light = normalize(vec3(0.35, 0.9, 0.2));
            float diffuse = max(dot(normalize(vNormal), light), 0.0);
            float shade = 0.62 + 0.38 * diffuse;

            vec4 texel = texture(uAtlas, vUv);
            fragColor = vec4(texel.rgb * shade, 1.0);
        }
        """;
}
