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

    // GlConsts 只收了 GL_NEAREST/GL_LINEAR 这组，缩小带 mip 的三档没有，数值来自 GL 规范。
    private const int GlNearestMipmapLinear = 0x2702;

    // 只上传部分 mip 链时必须声明链顶：MIN_FILTER 带 mip 的默认完整要求是链一路到 1x1，
    // 缺层 = 纹理不完整 = 采样全黑，不是退化到层 0。
    private const int GlTextureMaxLevel = 0x813D;

    public static GlTexture Create(GlInterface gl, byte[][] levels, int width, int height)
    {
        Debug.Assert(
            levels.Length > 0 && levels[0].Length == width * height * 4,
            $"[PREVIEWER][gl.texture] 层 0 数据对不上 bytes={levels[0].Length} " +
            $"expected={(long)width * height * 4} size={width}x{height}");
        Debug.Assert(
            MathF.Log2(width) % 1 == 0 && MathF.Log2(height) % 1 == 0,
            $"[PREVIEWER][gl.texture] 尺寸不是 2 的幂 width={width} height={height} " +
            "note=图集装箱侧负责对齐，这里不兜底");

        int handle = gl.GenTexture();
        gl.BindTexture(GlConsts.GL_TEXTURE_2D, handle);

        // 放大保持 NEAREST：MC 的像素风要的是棱角，双线性放大会糊。
        // 缩小走 mipmap：mip 链由图集侧按 sprite 独立生成（见 TextureAtlas.Build），
        // 这里逐层上传即可。绝不能 glGenerateMipmap 整图压缩：深层把相邻 sprite
        // 混进同一纹素，alpha 被稀释过 0.5 后 cutout discard 把整个面丢没。
        gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlConsts.GL_TEXTURE_MIN_FILTER, GlNearestMipmapLinear);
        gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlConsts.GL_TEXTURE_MAG_FILTER, GlConsts.GL_NEAREST);
        gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlTextureMaxLevel, levels.Length - 1);
        // 图集边缘的 sprite 越过边采样会包到对面去，夹住。
        gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlConsts.GL_TEXTURE_WRAP_S, GlConsts.GL_CLAMP_TO_EDGE);
        gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlConsts.GL_TEXTURE_WRAP_T, GlConsts.GL_CLAMP_TO_EDGE);

        for (int level = 0; level < levels.Length; level++)
        {
            int lw = Math.Max(1, width >> level);
            int lh = Math.Max(1, height >> level);
            byte[] data = levels[level];
            Debug.Assert(
                data.Length == lw * lh * 4,
                $"[PREVIEWER][gl.texture] mip 层 {level} 数据对不上 bytes={data.Length} " +
                $"expected={(long)lw * lh * 4} size={lw}x{lh}");

            IntPtr staging = Marshal.AllocHGlobal(data.Length);
            try
            {
                Marshal.Copy(data, 0, staging, data.Length);
                gl.TexImage2D(
                    GlConsts.GL_TEXTURE_2D,
                    level,
                    GlConsts.GL_RGBA8,
                    lw,
                    lh,
                    0,
                    GlConsts.GL_RGBA,
                    GlConsts.GL_UNSIGNED_BYTE,
                    staging);
            }
            finally
            {
                Marshal.FreeHGlobal(staging);
            }
        }

        gl.BindTexture(GlConsts.GL_TEXTURE_2D, 0);

        Debug.WriteLine($"[PREVIEWER][gl.texture] created size={width}x{height} levels={levels.Length}");
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
