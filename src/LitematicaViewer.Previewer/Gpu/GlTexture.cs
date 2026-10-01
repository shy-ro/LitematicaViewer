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
        // 各向异性过滤默认关闭（理由见 ApplyAnisotropicFiltering）。LV_AF=8 可临时打开做对照：
        // 它是「预览比单方块出图糊」的真正元凶，只有拿同一次构建的开关对照才看得出来——
        // filter 的改动会被它整个盖住（MAG/MIN/MAX_LEVEL 怎么改，画面都逐像素不变）。
        if (int.TryParse(Environment.GetEnvironmentVariable("LV_AF"), out var afLevel) && afLevel > 1)
            ApplyAnisotropicFiltering(gl, afLevel);

        // 诊断开关 LV_NOMIP=1：强制只用层 0、且不用 mip。回答的是「采样到底落在哪一层」——
        // 画面若因此变锐利，说明平时采的是更粗的 mip 层，即纹理整体处于缩小状态。
        var noMip = Environment.GetEnvironmentVariable("LV_NOMIP") == "1";
        if (noMip)
        {
            gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlConsts.GL_TEXTURE_MIN_FILTER, GlConsts.GL_NEAREST);
            gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlTextureMaxLevel, 0);
        }

        // 探针：设完立刻回读。「预览比单方块出图糊」的第一入口就是这里——MAG 只要不是
        // NEAREST，放大就是双线性，像素风的棱角全没；而常量值本身也要确认（GlConsts 的
        // GL_TEXTURE_MIN/MAG_FILTER 若被写反，设置会落到对方身上，看代码完全看不出来）。
        var readMag = GlRaw.GetTexParameteriv(gl, GlConsts.GL_TEXTURE_2D, GlConsts.GL_TEXTURE_MAG_FILTER);
        var readMin = GlRaw.GetTexParameteriv(gl, GlConsts.GL_TEXTURE_2D, GlConsts.GL_TEXTURE_MIN_FILTER);
        Debug.WriteLine(
            $"[PREVIEWER][gl.texture.filters] target GL_TEXTURE_2D=0x{GlConsts.GL_TEXTURE_2D:X4} " +
            $"GL_TEXTURE0=0x{GlConsts.GL_TEXTURE0:X4} (spec 0x0DE1 / 0x84C0) | " +
            $"pname MAG=0x{GlConsts.GL_TEXTURE_MAG_FILTER:X4} " +
            $"MIN=0x{GlConsts.GL_TEXTURE_MIN_FILTER:X4} | set MAG=0x{GlConsts.GL_NEAREST:X4} " +
            $"MIN=0x{GlLinearMipmapLinear:X4} | actual " +
            $"MAG={(readMag is { } m1 ? $"0x{m1:X4}" : "null")} " +
            $"MIN={(readMin is { } m2 ? $"0x{m2:X4}" : "null")} " +
            "| spec MAG=0x2800 MIN=0x2801 NEAREST=0x2600 LINEAR_MIPMAP_LINEAR=0x2703");

        for (var level = 0; level < levels.Length; level++)
        {
            var lw = Math.Max(1, width >> level);
            var lh = Math.Max(1, height >> level);
            var data = levels[level];
            // 临时探针：LV_REDATLAS=1 时把层 0 染成纯红。用来回答一个只有它能回答的问题——
            // 「画面上的贴图到底是不是这张纹理采出来的」。filter 改了画面却逐像素不变时，
            // 第一个要排除的就是「渲染读的是另一张纹理」。
            if (level == 0 && Environment.GetEnvironmentVariable("LV_REDATLAS") == "1")
            {
                data = (byte[])data.Clone();
                for (var i = 0; i < data.Length; i += 4)
                {
                    data[i] = 255;
                    data[i + 1] = 0;
                    data[i + 2] = 0;
                    data[i + 3] = 255;
                }
            }

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

        Debug.WriteLine(
            $"[PREVIEWER][gl.texture] created handle={handle} size={width}x{height} levels={levels.Length}");
        return new GlTexture(gl, handle);
    }

    // 绑到指定纹理单元。uniform 的采样器序号由着色器侧保证是同一个单元。
    public void Bind(int unit)
    {
        _gl.ActiveTexture(GlConsts.GL_TEXTURE0 + unit);
        _gl.BindTexture(GlConsts.GL_TEXTURE_2D, _handle);
    }

    // 各向异性过滤。**默认关闭**：本项目的贴图是 16px 像素画，AF 在斜视时沿长边多次
    // 线性取样，把纹素块平均成连续渐变——放大倍率下「糊」得肉眼可见。实测同一帧：
    // AF=8 时箱子面上的水平 run-length 恒为 1（相邻像素全不同，纯渐变），关掉后立刻
    // 出现 3~6px 的纹素平台。更坑的是它把 filter 的改动整个盖住：MAG/MIN/MAX_LEVEL
    // 随便改，画面却逐像素不变，看起来像「filter 根本没生效」，实际是 AF 在管采样。
    // 它原本的收益是「平掠面不闪」，而那一层已由 2x 超采样 + 三线性 mip 覆盖。
    // 所以不做成用户开关，只在 LV_AF=<level> 时打开做对照。扩展或入口缺席静默跳过。
    private static void ApplyAnisotropicFiltering(GlInterface gl, float level)
    {
        var extensions = GlRaw.GetString(gl, GlExtensions);
        if (extensions is null || !extensions.Contains("EXT_texture_filter_anisotropic", StringComparison.Ordinal))
        {
            Debug.WriteLine("[PREVIEWER][gl.texture] 无 EXT_texture_filter_anisotropic，跳过 AF");
            return;
        }

        var max = GlRaw.GetFloat(gl, GlMaxTextureMaxAnisotropy);
        if (max is not float maxAnisotropy || maxAnisotropy < 1f) return;

        var requested = MathF.Min(level, maxAnisotropy);
        if (GlRaw.TexParameterf(gl, GlConsts.GL_TEXTURE_2D, GlTextureMaxAnisotropy, requested))
            Debug.WriteLine($"[PREVIEWER][gl.texture] AF={requested} (max={maxAnisotropy})");
    }

    // 上下文丢失时用：GPU 侧的对象已经不在了，只能丢引用，不能发 Delete*。
    public void Abandon()
    {
        _disposed = true;
    }
}
