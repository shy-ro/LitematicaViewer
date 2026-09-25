using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.OpenGL;

namespace LitematicaViewer.Previewer.Gpu;

// R1：GL 对象由创建者持有，生命周期跟着创建它的上下文走。
internal sealed class GlMesh : IDisposable
{
    // Avalonia 的 GlConsts 里没有 GL_UNSIGNED_INT。索引宽度又只有它够用：
    // 真实的投影区域顶点数会超过 ushort 的 65535。
    private const int UnsignedInt = 0x1405;

    private readonly GlInterface _gl;
    private readonly int _vertexArray;
    private readonly int _vertexBuffer;
    private readonly int _indexBuffer;
    private readonly int _primitiveMode;
    private bool _disposed;

    public int IndexCount { get; }

    private GlMesh(
        GlInterface gl,
        int vertexArray,
        int vertexBuffer,
        int indexBuffer,
        int indexCount,
        int primitiveMode)
    {
        _gl = gl;
        _vertexArray = vertexArray;
        _vertexBuffer = vertexBuffer;
        _indexBuffer = indexBuffer;
        _primitiveMode = primitiveMode;
        IndexCount = indexCount;
    }

    // 交错顶点缓冲 + 独立索引缓冲。属性尺寸按顺序给出，偏移量从头累加。
    //
    // 图元类型是入参而默认三角形：GL 的绘制模式属于网格本身（索引的含义由它决定），
    // 放在 Create 里一次定下来，绘制处就不必每次再想「这一把该传什么」。
    // GlConsts 里没有 GL_LINES（同样是「后端用不上就不收」），所以由调用方给数值。
    public static GlMesh Create(
        GlInterface gl,
        float[] interleaved,
        int vertexCount,
        int[] indices,
        ReadOnlySpan<int> attributeSizes,
        int primitiveMode = GlConsts.GL_TRIANGLES)
    {
        int floatsPerVertex = 0;
        foreach (int size in attributeSizes)
        {
            floatsPerVertex += size;
        }

        Debug.Assert(
            floatsPerVertex * vertexCount == interleaved.Length,
            $"[PREVIEWER][gl.mesh] 顶点数据长度对不上 floats={interleaved.Length} " +
            $"expected={floatsPerVertex * vertexCount} vertexCount={vertexCount}");
        Debug.Assert(indices.Length > 0, "[PREVIEWER][gl.mesh] 索引为空");

        int vertexArray = gl.GenVertexArray();
        gl.BindVertexArray(vertexArray);

        int vertexBuffer = gl.GenBuffer();
        gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, vertexBuffer);
        UploadFloats(gl, GlConsts.GL_ARRAY_BUFFER, interleaved, GlConsts.GL_STATIC_DRAW);

        int offset = 0;
        for (int attribute = 0; attribute < attributeSizes.Length; attribute++)
        {
            gl.EnableVertexAttribArray(attribute);
            gl.VertexAttribPointer(
                attribute,
                attributeSizes[attribute],
                GlConsts.GL_FLOAT,
                0,
                floatsPerVertex * sizeof(float),
                (IntPtr)(offset * sizeof(float)));
            offset += attributeSizes[attribute];
        }

        // 索引缓冲必须在 VAO 还绑着的时候挂上去：ELEMENT_ARRAY_BUFFER 的绑定是 VAO 状态的一部分，
        // 顺序反了的话绘制时用的就是别的 VAO 里的索引缓冲。
        int indexBuffer = gl.GenBuffer();
        gl.BindBuffer(GlConsts.GL_ELEMENT_ARRAY_BUFFER, indexBuffer);
        UploadInts(gl, GlConsts.GL_ELEMENT_ARRAY_BUFFER, indices, GlConsts.GL_STATIC_DRAW);

        gl.BindVertexArray(0);
        return new GlMesh(gl, vertexArray, vertexBuffer, indexBuffer, indices.Length, primitiveMode);
    }

    public void Draw()
    {
        _gl.BindVertexArray(_vertexArray);
        _gl.DrawElements(_primitiveMode, IndexCount, UnsignedInt, IntPtr.Zero);

        // 画完解绑，别把 VAO 留给 Avalonia 自己的绘制流程。
        _gl.BindVertexArray(0);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gl.DeleteVertexArray(_vertexArray);
        _gl.DeleteBuffer(_vertexBuffer);
        _gl.DeleteBuffer(_indexBuffer);
    }

    // 上下文丢失时用：GPU 侧的对象已经不在了，只能丢引用，不能发 Delete*。
    public void Abandon() => _disposed = true;

    // 上传走一段非托管中转内存：GlInterface 的 BufferData 只收指针，没有 Span 重载，
    // 而为此开 unsafe 去 fixed 整个数组不值当。
    private static void UploadFloats(GlInterface gl, int target, float[] data, int usage)
    {
        IntPtr staging = Marshal.AllocHGlobal(data.Length * sizeof(float));
        try
        {
            Marshal.Copy(data, 0, staging, data.Length);
            gl.BufferData(target, (IntPtr)(data.Length * sizeof(float)), staging, usage);
        }
        finally
        {
            // BufferData 返回时数据已经拷进 GL 的缓冲，中转内存可以立刻放掉。
            Marshal.FreeHGlobal(staging);
        }
    }

    private static void UploadInts(GlInterface gl, int target, int[] data, int usage)
    {
        IntPtr staging = Marshal.AllocHGlobal(data.Length * sizeof(int));
        try
        {
            Marshal.Copy(data, 0, staging, data.Length);
            gl.BufferData(target, (IntPtr)(data.Length * sizeof(int)), staging, usage);
        }
        finally
        {
            Marshal.FreeHGlobal(staging);
        }
    }
}
