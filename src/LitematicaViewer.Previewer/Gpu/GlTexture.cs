using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.OpenGL;

namespace LitematicaViewer.Previewer.Gpu;

// R1：GL 对象由创建者持有，生命周期跟着创建它的上下文走。
// 一张 RGBA8 二维纹理，图集上传专用。
internal sealed class GlTexture : IDisposable
{
    private readonly GlInterface _gl;
    private readonly int _handle;
    private bool _disposed;

    private GlTexture(GlInterface gl, int handle)
    {
        _gl = gl;
        _handle = handle;
    }

    public static GlTexture Create(GlInterface gl, byte[] rgba, int width, int height)
    {
        Debug.Assert(
            (long)rgba.Length == (long)width * height * 4,
            $"[PREVIEWER][gl.texture] 数据长度对不上 bytes={rgba.Length} " +
            $"expected={(long)width * height * 4} size={width}x{height}");
        Debug.Assert(
            MathF.Log2(width) % 1 == 0 && MathF.Log2(height) % 1 == 0,
            $"[PREVIEWER][gl.texture] 尺寸不是 2 的幂 width={width} height={height} " +
            "note=图集装箱侧负责对齐，这里不兜底");

        int handle = gl.GenTexture();
        gl.BindTexture(GlConsts.GL_TEXTURE_2D, handle);

        // 图集里相邻 sprite 的边缘互相挨着，双线性过滤会在接缝处采到隔壁 sprite 的颜色；
        // NEAREST 没有这个问题，代价是缩小到很小时会闪烁——那要等 sprite padding 方案
        // 一起做，不是单独换过滤就能好的。
        gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlConsts.GL_TEXTURE_MIN_FILTER, GlConsts.GL_NEAREST);
        gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlConsts.GL_TEXTURE_MAG_FILTER, GlConsts.GL_NEAREST);
        // 图集边缘的 sprite 越过边采样会包到对面去，夹住。
        gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlConsts.GL_TEXTURE_WRAP_S, GlConsts.GL_CLAMP_TO_EDGE);
        gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlConsts.GL_TEXTURE_WRAP_T, GlConsts.GL_CLAMP_TO_EDGE);

        IntPtr staging = Marshal.AllocHGlobal(rgba.Length);
        try
        {
            Marshal.Copy(rgba, 0, staging, rgba.Length);
            gl.TexImage2D(
                GlConsts.GL_TEXTURE_2D,
                0,
                GlConsts.GL_RGBA8,
                width,
                height,
                0,
                GlConsts.GL_RGBA,
                GlConsts.GL_UNSIGNED_BYTE,
                staging);
        }
        finally
        {
            Marshal.FreeHGlobal(staging);
        }

        gl.BindTexture(GlConsts.GL_TEXTURE_2D, 0);

        Debug.WriteLine($"[PREVIEWER][gl.texture] created size={width}x{height} bytes={rgba.Length}");
        return new GlTexture(gl, handle);
    }

    // 绑到指定纹理单元。uniform 的采样器序号由着色器侧保证是同一个单元。
    public void Bind(int unit)
    {
        _gl.ActiveTexture(GlConsts.GL_TEXTURE0 + unit);
        _gl.BindTexture(GlConsts.GL_TEXTURE_2D, _handle);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gl.DeleteTexture(_handle);
    }

    // 上下文丢失时用：GPU 侧的对象已经不在了，只能丢引用，不能发 Delete*。
    public void Abandon() => _disposed = true;
}
