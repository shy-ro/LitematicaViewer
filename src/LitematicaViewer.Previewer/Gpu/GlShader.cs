using System.Diagnostics;
using System.Numerics;
using Avalonia.OpenGL;

namespace LitematicaViewer.Previewer.Gpu;

// R1：GL 对象由创建者持有，生命周期跟着创建它的上下文走。
internal sealed class GlShader : IDisposable
{
    private readonly GlInterface _gl;
    private readonly int _program;
    private bool _disposed;

    private GlShader(GlInterface gl, int program)
    {
        _gl = gl;
        _program = program;
    }

    public static GlShader Create(GlInterface gl, string vertexSource, string fragmentSource)
    {
        int vertex = Compile(gl, GlConsts.GL_VERTEX_SHADER, vertexSource);
        int fragment = Compile(gl, GlConsts.GL_FRAGMENT_SHADER, fragmentSource);
        int program = gl.CreateProgram();

        gl.AttachShader(program, vertex);
        gl.AttachShader(program, fragment);

        // 先 LinkProgram 再单独取日志：LinkProgramAndGetError 是否顺带做链接没有文档保证。
        // 万一它只取日志，程序对象照样能拿到手，用起来也不出声，只是什么都不画。
        gl.LinkProgram(program);
        string? error = gl.LinkProgramAndGetError(program);
        Debug.Assert(string.IsNullOrEmpty(error), $"[PREVIEWER][gl.shader] 链接失败: {error}");

        // 链接之后着色器对象就没用了，程序对象已经持有编译结果。
        gl.DeleteShader(vertex);
        gl.DeleteShader(fragment);

        return new GlShader(gl, program);
    }

    public void Use() => _gl.UseProgram(_program);

    public void SetFloat(string name, float value)
    {
        int location = _gl.GetUniformLocationString(_program, name);
        Debug.Assert(location >= 0, $"[PREVIEWER][gl.shader] uniform '{name}' 没找到");
        GlRaw.Uniform1f(_gl, location, value);
    }

    public unsafe void SetMatrix4(string name, Matrix4x4 matrix)
    {
        int location = _gl.GetUniformLocationString(_program, name);
        Debug.Assert(location >= 0, $"[PREVIEWER][gl.shader] uniform '{name}' 没找到");

        // GL 的 uniform 数组是列主序，System.Numerics 是行主序配行向量约定，两者差一次下标互换。
        // 这里直接按下标互换来写，而不是调 Matrix4x4.Transpose 再顺序拷：
        // 调了 Transpose 之后再按行读，会把这次互换正好抵消掉，传过去的还是原矩阵。
        float* values = stackalloc float[16];
        for (int column = 0; column < 4; column++)
        {
            for (int row = 0; row < 4; row++)
            {
                values[(column * 4) + row] = matrix[column, row];
            }
        }

        _gl.UniformMatrix4fv(location, 1, false, values);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gl.DeleteProgram(_program);
    }

    // 上下文丢失时用：GPU 侧的对象已经不在了，只能丢引用，不能发 Delete*。
    public void Abandon() => _disposed = true;

    private static int Compile(GlInterface gl, int stage, string source)
    {
        int shader = gl.CreateShader(stage);
        string? error = gl.CompileShaderAndGetError(shader, source);
        Debug.Assert(string.IsNullOrEmpty(error), $"[PREVIEWER][gl.shader] 编译失败 stage={stage}: {error}");
        return shader;
    }
}
