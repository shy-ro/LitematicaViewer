using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.OpenGL;

namespace LitematicaViewer.Previewer.Gpu;

// R1：GL 对象由创建者持有，生命周期跟着创建它的上下文走。
// 一张 RGBA8 二维纹理，图集上传专用。
internal sealed class GlTexture : IDisposable
{
    // GlConsts 只收了 GL_NEAREST/GL_LINEAR 这组，缩小带 mip 的三档没有，数值来自 GL 规范。
    private const int GlLinearMipmapLinear = 0x2703;

    // 只上传部分 mip 链时必须声明链顶：MIN_FILTER 带 mip 的默认完整要求是链一路到 1x1，
    // 缺层 = 纹理不完整 = 采样全黑，不是退化到层 0。
    private const int GlTextureMaxLevel = 0x813D;

    // EXT_texture_filter_anisotropic：扩展名、每方向取样上限、本纹理档位。
    private const int GlTextureMaxAnisotropy = 0x84FE;
    private const int GlMaxTextureMaxAnisotropy = 0x84FF;
    private const int GlExtensions = 0x1F03;
    private readonly GlInterface _gl;
    private readonly int _handle;
    private bool _disposed;

    private GlTexture(GlInterface gl, int handle)
    {
        _gl = gl;
        _handle = handle;
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _gl.DeleteTexture(_handle);
    }

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

        var handle = gl.GenTexture();
        gl.BindTexture(GlConsts.GL_TEXTURE_2D, handle);

        // 放大保持 NEAREST：MC 的像素风要的是棱角，双线性放大会糊。
        // 缩小走三线性 mipmap：层间插值消掉「相邻像素跳 mip 层」的噪点；
        // 层内双线性需要的越界留白由图集侧 Pad=8 兜住（见 TextureAtlas.Build）。
        // 绝不能 glGenerateMipmap 整图压缩：深层把相邻 sprite 混进同一纹素，
        // alpha 被稀释过 0.5 后 cutout discard 把整个面丢没。
        gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlConsts.GL_TEXTURE_MIN_FILTER, GlLinearMipmapLinear);
        gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlConsts.GL_TEXTURE_MAG_FILTER, GlConsts.GL_NEAREST);
        gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlTextureMaxLevel, levels.Length - 1);
        // 图集边缘的 sprite 越过边采样会包到对面去，夹住。
        gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlConsts.GL_TEXTURE_WRAP_S, GlConsts.GL_CLAMP_TO_EDGE);
        gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlConsts.GL_TEXTURE_WRAP_T, GlConsts.GL_CLAMP_TO_EDGE);
        ApplyAnisotropicFiltering(gl);

        for (var level = 0; level < levels.Length; level++)
        {
            var lw = Math.Max(1, width >> level);
            var lh = Math.Max(1, height >> level);
            var data = levels[level];
            Debug.Assert(
                data.Length == lw * lh * 4,
                $"[PREVIEWER][gl.texture] mip 层 {level} 数据对不上 bytes={data.Length} " +
                $"expected={(long)lw * lh * 4} size={lw}x{lh}");

            var staging = Marshal.AllocHGlobal(data.Length);
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

    // 各向异性过滤：平掠角（近乎平行于屏幕的地面/墙面）的 Footprint 是长条，
    // 普通 mipmap 按「最大边」选层会把整层糊掉，AF 沿长边多次取样保住细节。
    // 档位封顶 8：再高与 8 的肉眼差别趋零，但带宽按比例烧。
    // 扩展或入口缺席就静默跳过（三线性本身已经把噪点大头消掉），不做成开关——
    // 没有它画面只是「斜看更闪」，不影响正确性。
    private static void ApplyAnisotropicFiltering(GlInterface gl)
    {
        var extensions = GlRaw.GetString(gl, GlExtensions);
        if (extensions is null || !extensions.Contains("EXT_texture_filter_anisotropic", StringComparison.Ordinal))
        {
            Debug.WriteLine("[PREVIEWER][gl.texture] 无 EXT_texture_filter_anisotropic，跳过 AF");
            return;
        }

        var max = GlRaw.GetFloat(gl, GlMaxTextureMaxAnisotropy);
        if (max is not float maxAnisotropy || maxAnisotropy < 1f) return;

        var requested = MathF.Min(8f, maxAnisotropy);
        if (GlRaw.TexParameterf(gl, GlConsts.GL_TEXTURE_2D, GlTextureMaxAnisotropy, requested))
            Debug.WriteLine($"[PREVIEWER][gl.texture] AF={requested} (max={maxAnisotropy})");
    }

    // 上下文丢失时用：GPU 侧的对象已经不在了，只能丢引用，不能发 Delete*。
    public void Abandon()
    {
        _disposed = true;
    }
}
