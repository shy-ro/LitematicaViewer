using System.Diagnostics;
using System.Numerics;
using Avalonia.OpenGL;
using LitematicaViewer.Previewer.Diagnostics;
using LitematicaViewer.Previewer.Gpu;

namespace LitematicaViewer.Previewer;

// 原点上那个方向参照：沿 ±X / ±Y / ±Z 各一条线，X 黄、Y 绿、Z 蓝，半透明。
//
// 它存在的理由是「自由转头之后没有方向感」：立方体六个面按法线着色，转到某个角度时
// 相邻两面的颜色对比不足以说明「现在朝的是世界的哪一侧」，而上下都可以看到 89 度之后
// 连地平线都没有了。三条轴线是唯一不带歧义的世界坐标系参照。
//
// 走深度测试，所以落在立方体里的那一段被挡住，露在外面的是三根从方块里伸出来的轴。
// 不做「永远画在最前」的 gizmo：那样线会糊在方块上，画面校验里按面颜色反推的那套
// 会把线像素算成认不出的颜色，而它本来只是几何关系。
internal sealed class GlAxesRenderer : IDisposable
{
    // 位置 3 + 颜色 3。颜色跟着顶点走而不是 uniform：三根轴各一个颜色，
    // 用 uniform 就得画三遍，而三遍意味着三次绘制调用和三次状态切换。
    internal const int FloatsPerVertex = 6;

    // 半长。立方体半边长 0.5，取 2 相当于每根轴伸出方块外 1.5 个世界单位——
    // 相机默认在离原点约 4.7 的地方，这个长度在画面里明显但不会顶到边。
    internal const float AxisLength = 2f;

    // 半透明。着色器里那一行写的是同一个数，两边必须一致（同 GlCubeRenderer 与 DebugCube 的关系）：
    // 不一致时画面看起来只是「线淡了点」，而按颜色反推的画面校验会开始认不出像素。
    internal const float Alpha = 0.6f;

    // 顺序就是 xyz 的顺序，颜色也就按这个顺序：X 黄、Y 绿、Z 蓝。
    internal static readonly (Vector3 Color, Vector3 Direction)[] Axes =
    [
        (new Vector3(1f, 1f, 0f), Vector3.UnitX),
        (new Vector3(0f, 1f, 0f), Vector3.UnitY),
        (new Vector3(0f, 0f, 1f), Vector3.UnitZ),
    ];

    // GlConsts 里没有 GL_LINES：它按「后端自己用得上」来收图元类型。
    private const int GL_LINES = 0x0001;

    // 想要的线宽。GLES 3 的 core 只保证 1.0，更粗的是可选的实现细节。
    private const float PreferredLineWidth = 2f;

    private readonly GlInterface _gl;
    private readonly GlShader _shader;
    private readonly GlMesh _mesh;
    private bool _disposed;

    private GlAxesRenderer(GlInterface gl, GlShader shader, GlMesh mesh)
    {
        _gl = gl;
        _shader = shader;
        _mesh = mesh;
    }

    internal static float[] Vertices { get; } = BuildVertices();

    internal static int[] Indices { get; } = BuildIndices();

    public static GlAxesRenderer Create(GlInterface gl)
    {
        GlShader shader = GlShader.Create(gl, VertexShaderSource, FragmentShaderSource);
        GlMesh mesh = GlMesh.Create(
            gl,
            Vertices,
            Vertices.Length / FloatsPerVertex,
            Indices,
            [3, 3],
            GL_LINES);

        // 混合是渲染器自己的状态，和资源一样只在 Initialize 里设一次。
        // 不开混合的话 alpha 只是被写进帧缓冲的一个数，屏幕上看不出半透明——
        // 表现成「线比想要的实」，而那看起来像是颜色选错了。
        bool blended = GlRaw.EnableAlphaBlend(gl);

        // 入口找不到时它什么都不做，而 GL_BLEND 此时配的是默认的 (ONE, ZERO)，也就是原样覆盖——
        // 症状是三条线全是不透明的纯色。那个错法不报错、不崩，只在画面上「看着不太对」，
        // 所以在这里炸掉：宁可起不来，也不要一个颜色不对的参照物。
        Debug.Assert(
            blended,
            "[PREVIEWER][gl.axes] 这个上下文里没有 glBlendFuncSeparate，alpha 0.6 会被当成不透明画出去");

        float lineWidth = ApplyLineWidth(gl);

        DebugAxes.CheckGeometry(Vertices, Indices, Axes.Length);

        Debug.WriteLine(
            $"[PREVIEWER][gl.axes] axes={Axes.Length} vertices={Vertices.Length / FloatsPerVertex} " +
            $"indices={Indices.Length} length={AxisLength} alpha={Alpha} lineWidth={lineWidth} " +
            $"blend=srcAlpha/oneMinusSrcAlpha alphaChan=kept expected=线宽>=1");
        Debug.WriteLine(
            $"[PREVIEWER][gl.axes.color] x=黄(1,1,0) y=绿(0,1,0) z=蓝(0,0,1) " +
            $"depthTest=on note=落在立方体里的那一段被挡住");

        return new GlAxesRenderer(gl, shader, mesh);
    }

    public void Render(CameraState camera, int width, int height)
    {
        float aspect = (float)width / height;
        Matrix4x4 viewProjection = camera.GetViewMatrix() * camera.GetProjectionMatrix(aspect);

        _shader.Use();
        _shader.SetMatrix4("uViewProjection", viewProjection);
        _mesh.Draw();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _mesh.Dispose();
        _shader.Dispose();
    }

    // 上下文丢失时用：GPU 侧的对象已经不在了，只能丢引用，不能发 Delete*。
    public void Abandon()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _mesh.Abandon();
        _shader.Abandon();
    }

    // 线宽是可选能力，而且被拒绝的方式是往错误状态里丢一个 GL_INVALID_VALUE——不读就不知道。
    // 所以这里不假设：先把攒着的错误读干净（那可能是别的绘制留下的，拿它当答案会误判），
    // 设一次宽的，再读一次；被拒就退回 1.0。日志里那一行是「这几条线到底多粗」的唯一答案。
    private static float ApplyLineWidth(GlInterface gl)
    {
        int drained = DrainErrors(gl);

        if (!GlRaw.LineWidth(gl, PreferredLineWidth))
        {
            Debug.WriteLine(
                "[PREVIEWER][gl.axes] 这个上下文里没有 glLineWidth，线宽保持默认 1");
            return 1f;
        }

        int error = GlRaw.GetError(gl);

        if (error == GlRaw.NoError)
        {
            return PreferredLineWidth;
        }

        GlRaw.LineWidth(gl, 1f);
        Debug.WriteLine(
            $"[PREVIEWER][gl.axes] 驱动不接受 {PreferredLineWidth} 的线宽，退回 1 error=0x{error:X} " +
            $"drained={drained} expected=某些后端只保证 1.0");
        return 1f;
    }

    // 读干净攒下的错误。加一个上限是防「一直有错」时在这里转圈：那说明前面有什么一直在出错，
    // 而那件事该由绘制路径上的探针报，不该在这里把初始化卡住。
    private static int DrainErrors(GlInterface gl)
    {
        int drained = 0;
        while (drained < 16 && GlRaw.GetError(gl) != GlRaw.NoError)
        {
            drained++;
        }

        return drained;
    }

    private static float[] BuildVertices()
    {
        float[] vertices = new float[Axes.Length * 2 * FloatsPerVertex];
        int cursor = 0;

        foreach ((Vector3 color, Vector3 direction) in Axes)
        {
            Write(vertices, ref cursor, direction * -AxisLength, color);
            Write(vertices, ref cursor, direction * AxisLength, color);
        }

        return vertices;
    }

    private static int[] BuildIndices()
    {
        int[] indices = new int[Axes.Length * 2];
        for (int i = 0; i < indices.Length; i++)
        {
            indices[i] = i;
        }

        return indices;
    }

    private static void Write(float[] vertices, ref int cursor, Vector3 position, Vector3 color)
    {
        vertices[cursor++] = position.X;
        vertices[cursor++] = position.Y;
        vertices[cursor++] = position.Z;
        vertices[cursor++] = color.X;
        vertices[cursor++] = color.Y;
        vertices[cursor++] = color.Z;
    }

    private const string VertexShaderSource = """
        #version 300 es
        precision highp float;

        layout(location = 0) in vec3 aPosition;
        layout(location = 1) in vec3 aColor;

        uniform mat4 uViewProjection;

        out vec3 vColor;

        void main()
        {
            vColor = aColor;
            gl_Position = uViewProjection * vec4(aPosition, 1.0);
        }
        """;

    // 这里的 0.6 就是 GlAxesRenderer.Alpha，改一个必须改另一个。
    private const string FragmentShaderSource = """
        #version 300 es
        precision highp float;

        in vec3 vColor;

        out vec4 fragColor;

        void main()
        {
            fragColor = vec4(vColor, 0.6);
        }
        """;
}
