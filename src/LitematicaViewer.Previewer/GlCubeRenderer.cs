using System.Diagnostics;
using System.Numerics;
using Avalonia.OpenGL;
using LitematicaViewer.Previewer.Diagnostics;
using LitematicaViewer.Previewer.Gpu;

namespace LitematicaViewer.Previewer;

// Phase C 的硬编码立方体。相机是固定的——CameraState 与 SetCamera 属于 Phase D，
// 这里先写死三个数，等 Phase D 有了状态权威再换成从外面传进来。
internal sealed class GlCubeRenderer : IDisposable
{
    internal static readonly Vector3 CameraEye = new(2.6f, 2.0f, 3.4f);

    private static readonly Vector3 CameraTarget = Vector3.Zero;
    private static readonly Vector3 CameraUp = Vector3.UnitY;
    private const float FieldOfViewDegrees = 45f;
    private const float NearPlane = 0.1f;
    private const float FarPlane = 100f;

    internal const int FloatsPerVertex = 6;
    private const int PositionFloats = 3;

    // 每面给一组 (法线, 切向 U, 切向 V)，且保证 U × V = 法线。
    // 这样按 (-1,-1) (1,-1) (1,1) (-1,1) 的顺序取四个角，绕序从外面看就是逆时针，
    // 将来开背面剔除不用回头改数据。这条约束由 DebugCube 断言守着。
    private static readonly (Vector3 Normal, Vector3 U, Vector3 V)[] Faces =
    [
        (new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)),
        (new(-1, 0, 0), new(0, 0, 1), new(0, 1, 0)),
        (new(0, 1, 0), new(0, 0, 1), new(1, 0, 0)),
        (new(0, -1, 0), new(1, 0, 0), new(0, 0, 1)),
        (new(0, 0, 1), new(1, 0, 0), new(0, 1, 0)),
        (new(0, 0, -1), new(0, 1, 0), new(1, 0, 0)),
    ];

    private readonly GlInterface _gl;
    private readonly GlShader _shader;
    private readonly GlMesh _mesh;
    private bool _disposed;

    private GlCubeRenderer(GlInterface gl, GlShader shader, GlMesh mesh)
    {
        _gl = gl;
        _shader = shader;
        _mesh = mesh;
    }

    internal static float[] Vertices { get; } = BuildVertices();

    internal static int[] Indices { get; } = BuildIndices();

    public static GlCubeRenderer Create(GlInterface gl)
    {
        GlShader shader = GlShader.Create(gl, VertexShaderSource, FragmentShaderSource);
        GlMesh mesh = GlMesh.Create(gl, Vertices, Vertices.Length / FloatsPerVertex, Indices, [3, 3]);

        // 深度测试属于渲染器自己的状态，和资源一样只在 Initialize 里设一次。
        // 不设的话六个面按提交顺序互相覆盖，看到的是一个纯色轮廓而不是立方体。
        gl.Enable(GlConsts.GL_DEPTH_TEST);
        gl.DepthFunc(GlConsts.GL_LESS);
        gl.DepthMask(1);

        DebugCube.CheckGeometry(Vertices, Indices, Faces.Length);

        Debug.WriteLine(
            $"[PREVIEWER][gl.cube] vertices={Vertices.Length / FloatsPerVertex} indices={Indices.Length} " +
            $"eye={CameraEye} fov={FieldOfViewDegrees} expected=24/36");

        return new GlCubeRenderer(gl, shader, mesh);
    }

    public void Render(int width, int height)
    {
        // Phase C 用固定相机：CameraState 与 SetCamera 属于 Phase D，
        // 这里先写死三个数，等 Phase D 有了状态权威再换成从外面传进来。
        float aspect = (float)width / height;
        Matrix4x4 view = Matrix4x4.CreateLookAt(CameraEye, CameraTarget, CameraUp);
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(
            float.DegreesToRadians(FieldOfViewDegrees), aspect, NearPlane, FarPlane);

        _shader.Use();
        _shader.SetMatrix4("uViewProjection", view * projection);
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

    private static float[] BuildVertices()
    {
        float[] vertices = new float[Faces.Length * 4 * FloatsPerVertex];
        int cursor = 0;

        foreach ((Vector3 normal, Vector3 u, Vector3 v) in Faces)
        {
            Vector3 center = normal * 0.5f;
            foreach ((float s, float t) in Corners)
            {
                Vector3 position = center + (u * (0.5f * s)) + (v * (0.5f * t));
                vertices[cursor++] = position.X;
                vertices[cursor++] = position.Y;
                vertices[cursor++] = position.Z;
                vertices[cursor++] = normal.X;
                vertices[cursor++] = normal.Y;
                vertices[cursor++] = normal.Z;
            }
        }

        return vertices;
    }

    private static int[] BuildIndices()
    {
        int[] indices = new int[Faces.Length * 6];
        int cursor = 0;

        for (int face = 0; face < Faces.Length; face++)
        {
            int first = face * 4;
            indices[cursor++] = first;
            indices[cursor++] = first + 1;
            indices[cursor++] = first + 2;
            indices[cursor++] = first;
            indices[cursor++] = first + 2;
            indices[cursor++] = first + 3;
        }

        return indices;
    }

    private static (float S, float T)[] Corners => [(-1f, -1f), (1f, -1f), (1f, 1f), (-1f, 1f)];

    // 上下文是 ANGLE 给的 GLES 3.0，所以指令是 `#version 300 es` 且必须带精度限定符，
    // 桌面 GL 那套 `#version 330 core` 在这里编译不过。
    private const string VertexShaderSource = """
        #version 300 es
        precision highp float;

        layout(location = 0) in vec3 aPosition;
        layout(location = 1) in vec3 aNormal;

        uniform mat4 uViewProjection;

        out vec3 vNormal;

        void main()
        {
            vNormal = aNormal;
            gl_Position = uViewProjection * vec4(aPosition, 1.0);
        }
        """;

    private const string FragmentShaderSource = """
        #version 300 es
        precision highp float;

        in vec3 vNormal;

        out vec4 fragColor;

        void main()
        {
            // 直接拿法线当颜色：六个面互不相同，一眼能看出朝向。
            // 这也让读回像素的校验有确定的期望值——每个面就是一个固定的字节三元组。
            fragColor = vec4(vNormal * 0.5 + 0.5, 1.0);
        }
        """;
}
