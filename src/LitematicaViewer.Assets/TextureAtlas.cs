using System.Diagnostics;
using StbImageSharp;

namespace LitematicaViewer.Assets;

// 图集里一个 sprite 的落位。X/Y 是左上角像素，宽高是解码后的实际尺寸
// （动画贴图取首帧后是正方形，非动画的原样）。
public sealed record SpriteRect(string Sprite, int X, int Y, int Width, int Height);

public sealed record GeneratedSprite(string Sprite, byte[] Rgba, int Width, int Height);

// 自己拼的图集。不用游戏运行时的图集 dump：布局随版本变、还被拆成多页。
// 这里只收录调用方点名的 sprite，行式装箱，行内同高。
// 像素是 RGBA8、行主序、原点在左上（PNG 的约定）；v 翻转交给网格阶段。
public sealed class TextureAtlas
{
    // 每个 sprite 四周的留白像素数。开 mipmap 后缩小采样会越出 sprite 边界采到
    // 隔壁的颜色：留白 + 边缘外扩让 mip 链各层的越界采样仍然落在自己颜色的复制上。
    //
    // 8 不是拍脑袋：MIN_FILTER 用三线性（见 GlTexture）后层内要做双线性，
    // 每一层都要留出 ≥1 纹素的自家颜色给越界采样。16px sprite 的 cell=32 恰是 2 的幂，
    // 每层下采样后留白严格对齐（L1 剩 4、L2 剩 2、L3 剩 1、L4 整个 cell 都还是自家的），
    // 大尺寸 sprite 的 cell 不是 2 的幂会有一点深层落位漂移，但 8px 的余量吞得下。
    private const int Pad = 8;

    private const int MaxMipLevels = 4;
    private readonly Dictionary<string, SpriteRect> _rects;

    private TextureAtlas(int width, int height, byte[][] levels, Dictionary<string, SpriteRect> rects,
        List<string> missingSprites)
    {
        Width = width;
        Height = height;
        Levels = levels;
        Pixels = levels[0];
        _rects = rects;
        MissingCount = missingSprites.Count;
        MissingSprites = [.. missingSprites];
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    // mip 链：Levels[0] 是原图，之后每级尺寸减半，最多 MaxMipLevels+1 级。
    // 上传走逐层 TexImage2D，绝不用 glGenerateMipmap——它把整张图集一路压到 1x1，
    // 深层会把相邻 sprite 混进同一个纹素：alpha 被稀释过 0.5 就被 cutout discard
    // 丢光，整个面凭空消失（铁块面空就是这个）。每 sprite 独立缩、限层数是 MC 的做法。
    public byte[][] Levels { get; }
    public int MissingCount { get; }

    // 缺的是哪几张：包栈覆盖面有缺口时（原版没有、材质包也没补），名单是唯一的排查入口。
    public IReadOnlyList<string> MissingSprites { get; }

    public IReadOnlyCollection<SpriteRect> Rects => _rects.Values;

    public bool TryGetRect(string sprite, out SpriteRect rect)
    {
        return _rects.TryGetValue(sprite, out rect!);
    }

    // 逐个从包栈里读 PNG 并装箱。缺的贴图给品红/黑棋盘（MC missingno 的样式），
    // 网格照常生成，缺什么在画面上一眼能认出来——比静默用白块好查得多。
    public static TextureAtlas Build(PackStack packs, IEnumerable<string> sprites,
        IEnumerable<GeneratedSprite>? generatedSprites = null)
    {
        List<(string Sprite, byte[] Rgba, int W, int H)> decoded = [];
        List<string> missing = [];

        var generated = (generatedSprites ?? []).ToDictionary(static item => item.Sprite, StringComparer.Ordinal);

        foreach (var sprite in sprites.Distinct().OrderBy(s => s, StringComparer.Ordinal))
        {
            if (generated.TryGetValue(sprite, out var supplied))
            {
                decoded.Add((sprite, supplied.Rgba, supplied.Width, supplied.Height));
                continue;
            }

            var (ns, path) = SplitId(sprite);
            if (packs.TryRead($"assets/{ns}/textures/{path}.png", out var png))
            {
                var image = ImageResult.FromMemory(png, ColorComponents.RedGreenBlueAlpha);
                var (rgba, w, h) = image.Height > image.Width && image.Height % image.Width == 0
                    ? TakeFirstFrame(image)
                    : (image.Data, image.Width, image.Height);
                decoded.Add((sprite, rgba, w, h));
            }
            else
            {
                const int side = 16;
                var checker = new byte[side * side * 4];
                for (var y = 0; y < side; y++)
                for (var x = 0; x < side; x++)
                {
                    var offset = (y * side + x) * 4;
                    var magenta = (x / 8 + y / 8) % 2 == 0;
                    checker[offset] = magenta ? (byte)248 : (byte)0;
                    checker[offset + 1] = 0;
                    checker[offset + 2] = magenta ? (byte)248 : (byte)0;
                    checker[offset + 3] = 255;
                }

                decoded.Add((sprite, checker, side, side));
                missing.Add(sprite);
            }
        }

        foreach (var supplied in generated.Values)
            if (!decoded.Any(item => item.Sprite == supplied.Sprite))
                decoded.Add((supplied.Sprite, supplied.Rgba, supplied.Width, supplied.Height));

        // 行式装箱：按高降序排，一行放不下就换行。图集宽先定（容纳最宽的 sprite，
        // 抬到 2 的幂，GLES 3.0 对 NPOT 其实宽容，但 2 的幂将来开 mipmap 不用重排），
        // 高度随行数增长，最后同样抬到 2 的幂。
        // 装箱按「sprite + 四周留白」占格子，rect 记录的是内区：uv 永远落不进留白，
        // 留白只给 mipmap 的越界采样兜底。
        decoded.Sort((a, b) => b.H - a.H != 0 ? b.H - a.H : string.CompareOrdinal(a.Sprite, b.Sprite));

        // 宽度按总面积估一个接近正方的值（夹在 [最宽 sprite, 2048] 里）：
        // 只按最宽 sprite 定宽的话，几十张 16px 的小图会摞成 16x1024 的细高条，
        // 采样与将来开 mipmap 都难看。
        var totalArea = decoded.Sum(d => (long)(d.W + Pad * 2) * (d.H + Pad * 2));
        var targetSide = Pow2Ceiling((int)MathF.Sqrt(totalArea));
        var widest = Pow2Ceiling(decoded.Count == 0 ? 16 + Pad * 2 : decoded.Max(d => d.W) + Pad * 2);
        var atlasWidth = Math.Max(widest, Math.Min(targetSide, 2048));
        var cursorX = 0;
        var cursorY = 0;
        var rowHeight = 0;
        Dictionary<string, SpriteRect> rects = new(StringComparer.Ordinal);
        foreach (var (sprite, _, w, h) in decoded)
        {
            var cellWidth = w + Pad * 2;
            if (cursorX + cellWidth > atlasWidth && cursorX > 0)
            {
                cursorX = 0;
                cursorY += rowHeight;
                rowHeight = 0;
            }

            rects[sprite] = new SpriteRect(sprite, cursorX + Pad, cursorY + Pad, w, h);
            cursorX += cellWidth;
            rowHeight = Math.Max(rowHeight, h + Pad * 2);
        }

        var atlasHeight = Pow2Ceiling(cursorY + rowHeight);

        // 层数 = min(上限, 图集边长能减半的次数)。4 级是 MC 的默认档：
        // 16px sprite 到第 4 级正好 1x1，再深只会把整张图集搅成一锅。
        var levelCount = 0;
        while (levelCount < MaxMipLevels &&
               atlasWidth >> (levelCount + 1) >= 1 &&
               atlasHeight >> (levelCount + 1) >= 1)
            levelCount++;

        var levels = new byte[levelCount + 1][];
        levels[0] = new byte[atlasWidth * atlasHeight * 4];
        for (var level = 1; level <= levelCount; level++)
        {
            var lw = Math.Max(1, atlasWidth >> level);
            var lh = Math.Max(1, atlasHeight >> level);
            levels[level] = new byte[lw * lh * 4];
        }

        foreach (var (sprite, rgba, w, h) in decoded)
        {
            var rect = rects[sprite];
            Debug.Assert(
                rgba.Length == (long)w * h * 4,
                $"[ASSETS][atlas] 源数据不够 sprite={sprite} w={w} h={h} bytes={rgba.Length} " +
                $"expected={(long)w * h * 4}");

            // 先做带留白的独立副本（内区 + 边缘外扩），所有 mip 层都从它出：
            // 层 0 直接 blit 进图集；深层各自减半后 blit 到对应层的落位。
            // 之前把外扩直接写在图集上再 glGenerateMipmap 整图压缩，深层必然串到邻居。
            var padded = MakePadded(rgba, w, h, Pad);

            var chain = padded;
            var cw = w + Pad * 2;
            var ch = h + Pad * 2;
            for (var level = 0; level <= levelCount && cw > 0 && ch > 0; level++)
            {
                if (level > 0) chain = Downsample2x(chain, cw, ch, out cw, out ch);

                Blit(
                    chain, cw, ch,
                    levels[level],
                    Math.Max(1, atlasWidth >> level),
                    Math.Max(1, atlasHeight >> level),
                    (rect.X - Pad) >> level,
                    (rect.Y - Pad) >> level);
            }
        }

        return new TextureAtlas(atlasWidth, atlasHeight, levels, rects, missing);
    }

    // sprite 内区四周复制 pad 圈边缘像素（含四角），返回 (w+2pad)x(h+2pad) 的新图。
    private static byte[] MakePadded(byte[] rgba, int w, int h, int pad)
    {
        var pw = w + pad * 2;
        var ph = h + pad * 2;
        var padded = new byte[pw * ph * 4];
        for (var dy = -pad; dy < h + pad; dy++)
        {
            var sourceY = Math.Clamp(dy, 0, h - 1);
            for (var dx = -pad; dx < w + pad; dx++)
            {
                var sourceX = Math.Clamp(dx, 0, w - 1);
                var source = (sourceY * w + sourceX) * 4;
                var target = ((dy + pad) * pw + dx + pad) * 4;
                padded[target] = rgba[source];
                padded[target + 1] = rgba[source + 1];
                padded[target + 2] = rgba[source + 2];
                padded[target + 3] = rgba[source + 3];
            }
        }

        return padded;
    }

    // 2x2 盒式压缩。RGB 按 alpha 加权平均（孔洞不把颜色拖黑），alpha 取 2x2 的最大值；
    // 之后补一轮膨胀：透明纹素只要有不透明邻居就借邻居的颜色——
    // 没有这一步，树叶这类挖孔贴图的 alpha 逐层减半，缩到远处就被
    // cutout discard 丢光，整片树叶凭空消失。
    // alpha 不能取平均：铁栅栏/玻璃板这类稀疏挖孔贴图（覆盖 <50%）逐层平均
    // 会让深层纹素 alpha 齐刷刷掉到 discard 阈值之下，且均匀稀释时
    // 膨胀找不到不透明邻居，整片 sprite 远看直接消失。「足迹里有不透明就
    // 算不透明」正是「孔在远处收拢」的语义。
    private static byte[] Downsample2x(byte[] src, int w, int h, out int nw, out int nh)
    {
        nw = Math.Max(1, w >> 1);
        nh = Math.Max(1, h >> 1);
        var dst = new byte[nw * nh * 4];
        for (var y = 0; y < nh; y++)
        for (var x = 0; x < nw; x++)
        {
            int r = 0, g = 0, b = 0, a = 0, aMax = 0;
            for (var dy = 0; dy < 2; dy++)
            {
                var sy = Math.Min(y * 2 + dy, h - 1);
                for (var dx = 0; dx < 2; dx++)
                {
                    var sx = Math.Min(x * 2 + dx, w - 1);
                    var source = (sy * w + sx) * 4;
                    int sa = src[source + 3];
                    if (sa > 0)
                    {
                        r += src[source] * sa;
                        g += src[source + 1] * sa;
                        b += src[source + 2] * sa;
                    }

                    a += sa;
                    if (sa > aMax) aMax = sa;
                }
            }

            var target = (y * nw + x) * 4;
            if (a > 0)
            {
                dst[target] = (byte)(r / a);
                dst[target + 1] = (byte)(g / a);
                dst[target + 2] = (byte)(b / a);
            }

            dst[target + 3] = (byte)aMax;
        }

        DilateAlpha(dst, nw, nh);
        return dst;
    }

    // 一轮 4 邻域膨胀：alpha < 32 的纹素取不透明邻居（>=128）的平均颜色，
    // alpha 拉满。挖孔贴图的 mip 链靠它保持「孔在远处收拢而不是整片消失」。
    private static void DilateAlpha(byte[] img, int w, int h)
    {
        var snapshot = (byte[])img.Clone();
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var index = (y * w + x) * 4;
            if (snapshot[index + 3] >= 32) continue;

            int r = 0, g = 0, b = 0, count = 0;
            for (var dy = -1; dy <= 1; dy++)
            {
                var ny = y + dy;
                if ((uint)ny >= (uint)h) continue;

                for (var dx = -1; dx <= 1; dx++)
                {
                    var nx = x + dx;
                    if ((uint)nx >= (uint)w || (dx == 0 && dy == 0)) continue;

                    var source = (ny * w + nx) * 4;
                    if (snapshot[source + 3] < 128) continue;

                    r += snapshot[source];
                    g += snapshot[source + 1];
                    b += snapshot[source + 2];
                    count++;
                }
            }

            if (count > 0)
            {
                img[index] = (byte)(r / count);
                img[index + 1] = (byte)(g / count);
                img[index + 2] = (byte)(b / count);
                img[index + 3] = 255;
            }
        }
    }

    // 把独立 sprite 图拷进某一 mip 层的图集，越层边界裁掉。
    // 落位坐标 >> level 后可能不对齐 1px，clamp 保证不越界即可：
    // 深层的落位误差影响的是收拢期的 1px 边缘，肉眼不可见。
    private static void Blit(byte[] src, int w, int h, byte[] dst, int dstW, int dstH, int ox, int oy)
    {
        for (var row = 0; row < h; row++)
        {
            var ty = oy + row;
            if ((uint)ty >= (uint)dstH) continue;

            var columns = Math.Min(w, dstW - ox);
            if (columns <= 0) continue;

            var source = row * w * 4;
            var target = (ty * dstW + ox) * 4;
            Buffer.BlockCopy(src, source, dst, target, columns * 4);
        }
    }

    // 只有「高是宽的整数倍」才是竖排动画（水、熔岩、火），取最顶上的首帧。
    // 64x32 的告示牌/旧皮肤等横向实体展开图必须完整保留，不能裁成正方形。
    private static (byte[] Rgba, int Width, int Height) TakeFirstFrame(ImageResult image)
    {
        var side = Math.Min(image.Width, image.Height);
        var crop = new byte[side * side * 4];
        for (var row = 0; row < side; row++)
        {
            var source = row * image.Width * 4;
            Buffer.BlockCopy(image.Data, source, crop, row * side * 4, side * 4);
        }

        return (crop, side, side);
    }

    private static int Pow2Ceiling(int value)
    {
        var result = 1;
        while (result < value) result <<= 1;

        return result;
    }

    private static (string Ns, string Path) SplitId(string raw)
    {
        var colon = raw.IndexOf(':');
        return colon < 0 ? ("minecraft", raw) : (raw[..colon], raw[(colon + 1)..]);
    }
}
