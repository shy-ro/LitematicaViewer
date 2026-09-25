using System.Diagnostics;
using System.Numerics;
using Avalonia.OpenGL;
using LitematicaViewer.Previewer.Diagnostics;
using LitematicaViewer.Previewer.Gpu;

namespace LitematicaViewer.Previewer;

// 展台底部那个半透明蓝色光环。
//
// 它存在的理由是「展示时下面要有个托底的东西」：模型悬在空空的背景里时，画面没有尺度参照，
// 也不知道自己站在哪儿。一个落在底面平面上的圆环同时给出这两件事，而不引入地面、网格、阴影——
// 那些都会和「按面颜色反推像素」的画面校验打架，而收益只是好看。
//
// 半径与底面高度都是入参（每个展示目标不同），但顶点数据是死的：变换走矩阵，
// 于是换目标时不重建任何 GPU 资源（R5 要求 GPU 资源只在 Initialize / Dispose 里创建销毁）。
//
// alpha 跟着顶点走，而且只有两档：内带与外带各做一次渐变（0.14 ↔ 0.42），中带是常量 0.42。
// 软边就是这两段渐变画出来的——整圈同值是一条硬边的带子，和「光环」不是一回事。
//
// 注意「两档」说的是**顶点上**只有两个取值，不是说画出来只有两种颜色：带内的 alpha 是插出来的，
// 所以一个带里是一整段渐变。画面校验那边正是按这一点量的（像素落在「底色 → 不透明光环色」
// 这条线段上，落点参数就是 alpha），所以这里改成三档、或者把两端写成同一个值，
// 那边那条「两端都要够到」的断言就会红。
internal sealed class GlPedestalRenderer : IDisposable
{
    // 位置 3 + 颜色 4。第四个分量是 alpha，所以顶点格式和轴线那里不一样。
    internal const int FloatsPerVertex = 7;

    // 径向分成四圈顶点、三个带。数值是「目标水平半对角线」的倍数：
    // 1 就是刚好贴住目标的外接圆。内缘必须大于 1（见 DebugPedestal），否则光环压在目标底下被挡掉一段。
    internal static readonly float[] RingRadii = [1.08f, 1.26f, 1.44f, 1.62f];

    // 每个圈顶点的 alpha。内带与外带各是一次 0.14 → 0.42 的渐变（软边），中带是平的。
    internal static readonly float[] RingAlphas = [0.14f, 0.42f, 0.42f, 0.14f];

    // 蓝。取自「半透明蓝色光环」这个要求本身，DebugPedestal 会断言它确实是蓝的。
    internal static readonly Vector3 Color = new(0.25f, 0.65f, 1f);

    // 一圈的段数。96 段下半径 1.6 的圆每段弦长 0.1 个世界单位，
    // 在相机的默认距离下不到一个像素的误差——再多只是白费顶点。
    internal const int Segments = 96;

    private readonly GlInterface _gl;
    private readonly GlShader _shader;
    private readonly GlMesh _mesh;
    private bool _disposed;

    private GlPedestalRenderer(GlInterface gl, GlShader shader, GlMesh mesh)
    {
        _gl = gl;
        _shader = shader;
        _mesh = mesh;
    }

    // 顶点与索引是纯数据，静态算一次。画面校验要按它算期望，所以是 internal 而不是私有。
    internal static float[] Vertices { get; } = BuildVertices();

    internal static int[] Indices { get; } = BuildIndices();

    public static GlPedestalRenderer Create(GlInterface gl)
    {
        GlShader shader = GlShader.Create(gl, VertexShaderSource, FragmentShaderSource);
        GlMesh mesh = GlMesh.Create(
            gl,
            Vertices,
            Vertices.Length / FloatsPerVertex,
            Indices,
            [3, 4]);

        // 深度测试要显式开一次：光环是「落在模型底面上」的东西，被模型挡住的那一段不该画出来。
        // 立方体那个渲染器已经开过，但那是它的状态——顺序一变就轮到「光环整个盖在模型上」，
        // 而那看起来像模型没了。开两次是幂等的。
        gl.Enable(GlConsts.GL_DEPTH_TEST);

        bool blended = GlRaw.EnableAlphaBlend(gl);
        Debug.Assert(
            blended,
            "[PREVIEWER][gl.pedestal] 这个上下文里没有 glBlendFuncSeparate，alpha 会被当成不透明画出去");

        DebugPedestal.CheckGeometry(Vertices, Indices);

        Debug.WriteLine(
            $"[PREVIEWER][gl.pedestal] vertices={Vertices.Length / FloatsPerVertex} indices={Indices.Length} " +
            $"segments={Segments} radii=[{string.Join(", ", RingRadii)}] alphas=[{string.Join(", ", RingAlphas)}] " +
            $"color={Byte(Color.X)},{Byte(Color.Y)},{Byte(Color.Z)} blend=srcAlpha/oneMinusSrcAlpha depthTest=on " +
            "note=半径是目标水平半对角线的倍数，实际半径与底面高度由宿主每帧给");

        return new GlPedestalRenderer(gl, shader, mesh);
    }

    // 半径与底面高度由调用方给：控件不知道当前展示的是什么，那是宿主的事。
    public void Render(CameraState camera, int width, int height, float radius, float baseY)
    {
        float aspect = (float)width / height;
        Matrix4x4 viewProjection = camera.GetViewMatrix() * camera.GetProjectionMatrix(aspect);

        // 顶点是「半径为 1、落在 y=0 平面上」的那一份，缩放与抬升折进矩阵。
        // 折进矩阵而不是重建网格：重建意味着换目标就删一次 VBO 再建一次，而 R5 把
        // GPU 资源的创建销毁只留给 Initialize / Dispose。
        Matrix4x4 transform =
            Matrix4x4.CreateScale(radius, 1f, radius) *
            Matrix4x4.CreateTranslation(0f, baseY, 0f);

        _shader.Use();
        _shader.SetMatrix4("uViewProjection", transform * viewProjection);
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

    private static byte Byte(float value) => (byte)Math.Clamp((int)Math.Round(value * 255f), 0, 255);

    private static float[] BuildVertices()
    {
        float[] vertices = new float[RingRadii.Length * Segments * FloatsPerVertex];
        int cursor = 0;

        for (int ring = 0; ring < RingRadii.Length; ring++)
        {
            float radius = RingRadii[ring];
            float alpha = RingAlphas[ring];

            for (int segment = 0; segment < Segments; segment++)
            {
                float angle = MathF.Tau * segment / Segments;
                vertices[cursor++] = MathF.Cos(angle) * radius;
                vertices[cursor++] = 0f;
                vertices[cursor++] = MathF.Sin(angle) * radius;
                vertices[cursor++] = Color.X;
                vertices[cursor++] = Color.Y;
                vertices[cursor++] = Color.Z;
                vertices[cursor++] = alpha;
            }
        }

        return vertices;
    }

    // 相邻两圈之间连成一条三角带。段号取模而不是多生成一列重合顶点：
    // 接缝处两个顶点位置相同而法线/颜色相同，重复一列只是多一段退化的三角形。
    private static int[] BuildIndices()
    {
        int[] indices = new int[(RingRadii.Length - 1) * Segments * 6];
        int cursor = 0;

        for (int ring = 0; ring < RingRadii.Length - 1; ring++)
        {
            for (int segment = 0; segment < Segments; segment++)
            {
                int next = (segment + 1) % Segments;
                int inner = (ring * Segments) + segment;
                int innerNext = (ring * Segments) + next;
                int outer = ((ring + 1) * Segments) + segment;
                int outerNext = ((ring + 1) * Segments) + next;

                indices[cursor++] = inner;
                indices[cursor++] = innerNext;
                indices[cursor++] = outerNext;
                indices[cursor++] = inner;
                indices[cursor++] = outerNext;
                indices[cursor++] = outer;
            }
        }

        return indices;
    }

    private const string VertexShaderSource = """
        #version 300 es
        precision highp float;

        layout(location = 0) in vec3 aPosition;
        layout(location = 1) in vec4 aColor;

        uniform mat4 uViewProjection;

        out vec4 vColor;

        void main()
        {
            vColor = aColor;
            gl_Position = uViewProjection * vec4(aPosition, 1.0);
        }
        """;

    // alpha 完全跟着顶点走。写成常量会丢掉那圈软边，而软边正是「光环」和「一个圆盘」的区别。
    // 透明通道由混合函数里的 (ZERO, ONE) 原样留下，这里的 alpha 只作用于颜色。
    private const string FragmentShaderSource = """
        #version 300 es
        precision highp float;

        in vec4 vColor;

        out vec4 fragColor;

        void main()
        {
            fragColor = vColor;
        }
        """;
}
