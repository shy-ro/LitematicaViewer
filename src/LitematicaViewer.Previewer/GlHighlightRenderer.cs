using System.Diagnostics;
using System.Numerics;
using Avalonia.OpenGL;
using LitematicaViewer.Previewer.Gpu;

namespace LitematicaViewer.Previewer;

// 拾取命中的那个方块外面的白色线框盒。
//
// 它与网格的关系是「准星」，不是「选中」：指针指着哪儿它就在哪儿，PointerExited 或
// 射线落空时消失。画成线框而不是半透明罩，是因为罩会与模型自己的混合排序打架，
// 而线框只需要深度测试——被模型挡住的边看不见，露出来的边连成那个方块的轮廓。
//
// 顶点数据是「中心在原点、边长为 1、微微外扩」的那一份：外扩 0.002（半边 0.001×2）
// 是为了不与方块自己的面 z-fighting——方块的六个面就画在盒子的六个面上，共用同一平面。
// 实际位置与大小走矩阵（R5：换目标不重建任何 GPU 资源）。
internal sealed class GlHighlightRenderer : IDisposable
{
    internal const int FloatsPerVertex = 3;

    // GlConsts 没收 GL_LINES（「后端用不上就不收」），数值直接给：GL_LINES = 0x0001。
    internal const int GlLines = 0x0001;

    private const float HalfExpand = 0.501f;

    private const string VertexShaderSource =
        """
        #version 300 es
        precision highp float;

        layout(location = 0) in vec3 aPosition;

        uniform mat4 uViewProjection;

        void main()
        {
            gl_Position = uViewProjection * vec4(aPosition, 1.0);
        }
        """;

    private const string FragmentShaderSource =
        """
        #version 300 es
        precision highp float;

        out vec4 fragColor;

        void main()
        {
            fragColor = vec4(1.0, 1.0, 1.0, 1.0);
        }
        """;

    // 12 条边 × 2 端点。LINES 的两条端点顺序无所谓（无线宽方向）。
    internal static readonly float[] Vertices = BuildVertices();

    private static readonly int[] Indices = Enumerable.Range(0, 24).ToArray();

    private readonly GlInterface _gl;
    private readonly GlMesh _mesh;
    private readonly GlShader _shader;
    private bool _disposed;

    private GlHighlightRenderer(GlInterface gl, GlShader shader, GlMesh mesh)
    {
        _gl = gl;
        _shader = shader;
        _mesh = mesh;
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _mesh.Dispose();
        _shader.Dispose();
    }

    public static GlHighlightRenderer Create(GlInterface gl)
    {
        var shader = GlShader.Create(gl, VertexShaderSource, FragmentShaderSource);
        var mesh = GlMesh.Create(
            gl,
            Vertices,
            Vertices.Length / FloatsPerVertex,
            Indices,
            [3],
            GlLines);

        gl.Enable(GlConsts.GL_DEPTH_TEST);

        Debug.WriteLine($"[PREVIEWER][gl.highlight] vertices={Vertices.Length / FloatsPerVertex} lines=12 mode=LINES");

        return new GlHighlightRenderer(gl, shader, mesh);
    }

    // blockPosition 是方块的最小角（世界坐标），与体素遍历的整数格子语义一致：
    // 格子 (x,y,z) 占据的世界空间是 [x, x+1)³。
    public void Render(CameraState camera, int width, int height, Vector3 blockPosition)
    {
        var aspect = (float)width / height;
        var viewProjection = camera.GetViewMatrix() * camera.GetProjectionMatrix(aspect);

        var transform = Matrix4x4.CreateTranslation(blockPosition + new Vector3(0.5f));

        _shader.Use();
        _shader.SetMatrix4("uViewProjection", transform * viewProjection);
        _mesh.Draw();
    }

    public void Abandon()
    {
        if (_disposed) return;

        _disposed = true;
        _mesh.Abandon();
        _shader.Abandon();
    }

    private static float[] BuildVertices()
    {
        var a = -HalfExpand;
        var b = HalfExpand;

        // 8 个角。
        Vector3[] corner =
        [
            new(a, a, a), new(b, a, a), new(b, b, a), new(a, b, a),
            new(a, a, b), new(b, a, b), new(b, b, b), new(a, b, b)
        ];

        // 12 条边：底面 4、顶面 4、立柱 4。
        (int From, int To)[] edges =
        [
            (0, 1), (1, 2), (2, 3), (3, 0),
            (4, 5), (5, 6), (6, 7), (7, 4),
            (0, 4), (1, 5), (2, 6), (3, 7)
        ];

        var vertices = new float[edges.Length * 2 * FloatsPerVertex];
        var offset = 0;
        foreach (var (from, to) in edges)
        {
            vertices[offset++] = corner[from].X;
            vertices[offset++] = corner[from].Y;
            vertices[offset++] = corner[from].Z;
            vertices[offset++] = corner[to].X;
            vertices[offset++] = corner[to].Y;
            vertices[offset++] = corner[to].Z;
        }

        return vertices;
    }
}
