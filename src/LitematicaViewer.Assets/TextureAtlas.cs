using System.Diagnostics;
using StbImageSharp;

namespace LitematicaViewer.Assets;

// 图集里一个 sprite 的落位。X/Y 是左上角像素，宽高是解码后的实际尺寸
// （动画贴图取首帧后是正方形，非动画的原样）。
public sealed record SpriteRect(string Sprite, int X, int Y, int Width, int Height);

// 自己拼的图集。不用游戏运行时的图集 dump：布局随版本变、还被拆成多页。
// 这里只收录调用方点名的 sprite，行式装箱，行内同高。
// 像素是 RGBA8、行主序、原点在左上（PNG 的约定）；v 翻转交给网格阶段。
public sealed class TextureAtlas
{
    private readonly Dictionary<string, SpriteRect> _rects;

    private TextureAtlas(int width, int height, byte[][] levels, Dictionary<string, SpriteRect> rects, List<string> missingSprites)
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

    public bool TryGetRect(string sprite, out SpriteRect rect) => _rects.TryGetValue(sprite, out rect!);

    // 每个 sprite 四周的留白像素数。开 mipmap 后缩小采样会越出 sprite 边界采到
    // 隔壁的颜色：留白 + 边缘外扩让 mip 链前几级的越界采样仍然落在自己颜色的复制上。
    // 2px 养到 mip level 2（4 合 1）不串味，更深的级别影响的是缩到极小时的画面，
    // 那时贴图本身已不到 2px，串味不可见。
    private const int Pad = 2;

    // 逐个从包栈里读 PNG 并装箱。缺的贴图给品红/黑棋盘（MC missingno 的样式），
    // 网格照常生成，缺什么在画面上一眼能认出来——比静默用白块好查得多。
    public static TextureAtlas Build(PackStack packs, IEnumerable<string> sprites)
    {
        List<(string Sprite, byte[] Rgba, int W, int H)> decoded = [];
        List<string> missing = [];

        foreach (string sprite in sprites.Distinct().OrderBy(s => s, StringComparer.Ordinal))
        {
            (string ns, string path) = SplitId(sprite);
            if (packs.TryRead($"assets/{ns}/textures/{path}.png", out byte[] png))
            {
                ImageResult image = ImageResult.FromMemory(png, ColorComponents.RedGreenBlueAlpha);
                (byte[] rgba, int w, int h) = image.Width == image.Height
                    ? (image.Data, image.Width, image.Height)
                    : TakeFirstFrame(image);
                decoded.Add((sprite, rgba, w, h));
            }
            else
            {
                const int side = 16;
                byte[] checker = new byte[side * side * 4];
                for (int y = 0; y < side; y++)
                {
                    for (int x = 0; x < side; x++)
                    {
                        int offset = ((y * side) + x) * 4;
                        bool magenta = ((x / 8) + (y / 8)) % 2 == 0;
                        checker[offset] = magenta ? (byte)248 : (byte)0;
                        checker[offset + 1] = 0;
                        checker[offset + 2] = magenta ? (byte)248 : (byte)0;
                        checker[offset + 3] = 255;
                    }
                }

                decoded.Add((sprite, checker, side, side));
                missing.Add(sprite);
            }
        }

        // 行式装箱：按高降序排，一行放不下就换行。图集宽先定（容纳最宽的 sprite，
        // 抬到 2 的幂，GLES 3.0 对 NPOT 其实宽容，但 2 的幂将来开 mipmap 不用重排），
        // 高度随行数增长，最后同样抬到 2 的幂。
        // 装箱按「sprite + 四周留白」占格子，rect 记录的是内区：uv 永远落不进留白，
        // 留白只给 mipmap 的越界采样兜底。
        decoded.Sort((a, b) => (b.H - a.H) != 0 ? b.H - a.H : string.CompareOrdinal(a.Sprite, b.Sprite));

        // 宽度按总面积估一个接近正方的值（夹在 [最宽 sprite, 2048] 里）：
        // 只按最宽 sprite 定宽的话，几十张 16px 的小图会摞成 16x1024 的细高条，
        // 采样与将来开 mipmap 都难看。
        long totalArea = decoded.Sum(d => (long)(d.W + Pad * 2) * (d.H + Pad * 2));
        int targetSide = Pow2Ceiling((int)MathF.Sqrt(totalArea));
        int widest = Pow2Ceiling(decoded.Count == 0 ? 16 + Pad * 2 : decoded.Max(d => d.W) + Pad * 2);
        int atlasWidth = Math.Max(widest, Math.Min(targetSide, 2048));
        int cursorX = 0;
        int cursorY = 0;
        int rowHeight = 0;
        Dictionary<string, SpriteRect> rects = new(StringComparer.Ordinal);
        foreach ((string sprite, _, int w, int h) in decoded)
        {
            int cellWidth = w + Pad * 2;
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

        int atlasHeight = Pow2Ceiling(cursorY + rowHeight);

        // 层数 = min(上限, 图集边长能减半的次数)。4 级是 MC 的默认档：
        // 16px sprite 到第 4 级正好 1x1，再深只会把整张图集搅成一锅。
        int levelCount = 0;
        while (levelCount < MaxMipLevels &&
               (atlasWidth >> (levelCount + 1)) >= 1 &&
               (atlasHeight >> (levelCount + 1)) >= 1)
        {
            levelCount++;
        }

        byte[][] levels = new byte[levelCount + 1][];
        levels[0] = new byte[atlasWidth * atlasHeight * 4];
        for (int level = 1; level <= levelCount; level++)
        {
            int lw = Math.Max(1, atlasWidth >> level);
            int lh = Math.Max(1, atlasHeight >> level);
            levels[level] = new byte[lw * lh * 4];
        }

        foreach ((string sprite, byte[] rgba, int w, int h) in decoded)
        {
            SpriteRect rect = rects[sprite];
            Debug.Assert(
                (long)rgba.Length == (long)w * h * 4,
                $"[ASSETS][atlas] 源数据不够 sprite={sprite} w={w} h={h} bytes={rgba.Length} " +
                $"expected={(long)w * h * 4}");

            // 先做带留白的独立副本（内区 + 边缘外扩），所有 mip 层都从它出：
            // 层 0 直接 blit 进图集；深层各自减半后 blit 到对应层的落位。
            // 之前把外扩直接写在图集上再 glGenerateMipmap 整图压缩，深层必然串到邻居。
            byte[] padded = MakePadded(rgba, w, h, Pad);

            byte[] chain = padded;
            int cw = w + Pad * 2;
            int ch = h + Pad * 2;
            for (int level = 0; level <= levelCount && cw > 0 && ch > 0; level++)
            {
                if (level > 0)
                {
                    chain = Downsample2x(chain, cw, ch, out cw, out ch);
                }

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

    private const int MaxMipLevels = 4;

    // sprite 内区四周复制 pad 圈边缘像素（含四角），返回 (w+2pad)x(h+2pad) 的新图。
    private static byte[] MakePadded(byte[] rgba, int w, int h, int pad)
    {
        int pw = w + pad * 2;
        int ph = h + pad * 2;
        byte[] padded = new byte[pw * ph * 4];
        for (int dy = -pad; dy < h + pad; dy++)
        {
            int sourceY = Math.Clamp(dy, 0, h - 1);
            for (int dx = -pad; dx < w + pad; dx++)
            {
                int sourceX = Math.Clamp(dx, 0, w - 1);
                int source = ((sourceY * w) + sourceX) * 4;
                int target = (((dy + pad) * pw) + (dx + pad)) * 4;
                padded[target] = rgba[source];
                padded[target + 1] = rgba[source + 1];
                padded[target + 2] = rgba[source + 2];
                padded[target + 3] = rgba[source + 3];
            }
        }

        return padded;
    }

    // 2x2 盒式压缩。RGB 按 alpha 加权平均（孔洞不把颜色拖黑），alpha 取平均；
    // 之后补一轮膨胀：透明纹素只要有不透明邻居就借邻居的颜色——
    // 没有这一步，树叶这类挖孔贴图的 alpha 逐层减半，缩到远处就被
    // cutout discard 丢光，整片树叶凭空消失。
    private static byte[] Downsample2x(byte[] src, int w, int h, out int nw, out int nh)
    {
        nw = Math.Max(1, w >> 1);
        nh = Math.Max(1, h >> 1);
        byte[] dst = new byte[nw * nh * 4];
        for (int y = 0; y < nh; y++)
        {
            for (int x = 0; x < nw; x++)
            {
                int r = 0, g = 0, b = 0, a = 0, weight = 0;
                for (int dy = 0; dy < 2; dy++)
                {
                    int sy = Math.Min((y * 2) + dy, h - 1);
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int sx = Math.Min((x * 2) + dx, w - 1);
                        int source = ((sy * w) + sx) * 4;
                        int sa = src[source + 3];
                        if (sa > 0)
                        {
                            r += src[source] * sa;
                            g += src[source + 1] * sa;
                            b += src[source + 2] * sa;
                        }

                        a += sa;
                        weight++;
                    }
                }

                int target = ((y * nw) + x) * 4;
                if (a > 0)
                {
                    dst[target] = (byte)(r / a);
                    dst[target + 1] = (byte)(g / a);
                    dst[target + 2] = (byte)(b / a);
                }

                dst[target + 3] = (byte)(a / weight);
            }
        }

        DilateAlpha(dst, nw, nh);
        return dst;
    }

    // 一轮 4 邻域膨胀：alpha < 32 的纹素取不透明邻居（>=128）的平均颜色，
    // alpha 拉满。挖孔贴图的 mip 链靠它保持「孔在远处收拢而不是整片消失」。
    private static void DilateAlpha(byte[] img, int w, int h)
    {
        byte[] snapshot = (byte[])img.Clone();
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int index = ((y * w) + x) * 4;
                if (snapshot[index + 3] >= 32)
                {
                    continue;
                }

                int r = 0, g = 0, b = 0, count = 0;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int ny = y + dy;
                    if ((uint)ny >= (uint)h)
                    {
                        continue;
                    }

                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx;
                        if ((uint)nx >= (uint)w || (dx == 0 && dy == 0))
                        {
                            continue;
                        }

                        int source = ((ny * w) + nx) * 4;
                        if (snapshot[source + 3] < 128)
                        {
                            continue;
                        }

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
    }

    // 把独立 sprite 图拷进某一 mip 层的图集，越层边界裁掉。
    // 落位坐标 >> level 后可能不对齐 1px，clamp 保证不越界即可：
    // 深层的落位误差影响的是收拢期的 1px 边缘，肉眼不可见。
    private static void Blit(byte[] src, int w, int h, byte[] dst, int dstW, int dstH, int ox, int oy)
    {
        for (int row = 0; row < h; row++)
        {
            int ty = oy + row;
            if ((uint)ty >= (uint)dstH)
            {
                continue;
            }

            int columns = Math.Min(w, dstW - ox);
            if (columns <= 0)
            {
                continue;
            }

            int source = row * w * 4;
            int target = ((ty * dstW) + ox) * 4;
            Buffer.BlockCopy(src, source, dst, target, columns * 4);
        }
    }

    // 非正方形贴图两种形状：竖排动画（水、熔岩、火，h 是 w 的整数倍）取最顶上
    // 的 w×w 首帧；横排实体皮肤图集（64x32 之类，头颅贴图就是这种）没有「帧」的
    // 概念，取左上角 min(w,h)² 的正方——头颅都画在皮肤图集的左上角，正好落在里面。
    // 之前只认竖排：横排贴图按 w×w 去拷直接把 BlockCopy 越界炸掉。
    private static (byte[] Rgba, int Width, int Height) TakeFirstFrame(ImageResult image)
    {
        int side = Math.Min(image.Width, image.Height);
        byte[] crop = new byte[side * side * 4];
        for (int row = 0; row < side; row++)
        {
            int source = row * image.Width * 4;
            Buffer.BlockCopy(image.Data, source, crop, row * side * 4, side * 4);
        }

        return (crop, side, side);
    }

    private static int Pow2Ceiling(int value)
    {
        int result = 1;
        while (result < value)
        {
            result <<= 1;
        }

        return result;
    }

    private static (string Ns, string Path) SplitId(string raw)
    {
        int colon = raw.IndexOf(':');
        return colon < 0 ? ("minecraft", raw) : (raw[..colon], raw[(colon + 1)..]);
    }
}
