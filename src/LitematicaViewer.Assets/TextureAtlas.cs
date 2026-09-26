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

    private TextureAtlas(int width, int height, byte[] pixels, Dictionary<string, SpriteRect> rects, List<string> missingSprites)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
        _rects = rects;
        MissingCount = missingSprites.Count;
        MissingSprites = [.. missingSprites];
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }
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
        byte[] pixels = new byte[atlasWidth * atlasHeight * 4];
        foreach ((string sprite, byte[] rgba, int w, int h) in decoded)
        {
            SpriteRect rect = rects[sprite];
            for (int row = 0; row < h; row++)
            {
                int source = row * w * 4;
                int target = (((rect.Y + row) * atlasWidth) + rect.X) * 4;
                // 越界说明装箱的落位与数据形状对不上（解码尺寸或首帧裁剪出了错）；
                // BlockCopy 的报错不带 sprite 名，先在这里把它钉出来。
                Debug.Assert(
                    source + (w * 4) <= rgba.Length,
                    $"[ASSETS][atlas] 源数据不够 sprite={sprite} w={w} h={h} bytes={rgba.Length} " +
                    $"expected={(long)w * h * 4}");
                Debug.Assert(
                    rect.Y + row < atlasHeight && rect.X + w <= atlasWidth,
                    $"[ASSETS][atlas] 落位越界 sprite={sprite} rect=({rect.X},{rect.Y},{w},{h}) " +
                    $"atlas={atlasWidth}x{atlasHeight}");
                Buffer.BlockCopy(rgba, source, pixels, target, w * 4);
            }

            ExtrudeEdges(pixels, atlasWidth, atlasHeight, rect.X, rect.Y, w, h, Pad);
        }

        return new TextureAtlas(atlasWidth, atlasHeight, pixels, rects, missing);
    }

    // 把 sprite 内区的边缘像素向外复制 pad 圈（含四角）。mipmap 缩小采样时
    // 越出 sprite 的 UV 落在这些复制出来的像素上，采到的是自己的边缘色而不是邻居的。
    private static void ExtrudeEdges(byte[] pixels, int atlasWidth, int atlasHeight, int x, int y, int w, int h, int pad)
    {
        for (int dy = -pad; dy < h + pad; dy++)
        {
            int targetY = y + dy;
            if ((uint)targetY >= (uint)atlasHeight)
            {
                continue;
            }

            // 源行 clamp 进内区：留白圈之外的 dy 全部取内区最靠边的行。
            int sourceY = y + Math.Clamp(dy, 0, h - 1);
            for (int dx = -pad; dx < w + pad; dx++)
            {
                if (dx >= 0 && dx < w && dy >= 0 && dy < h)
                {
                    continue; // 内区本体不动
                }

                int targetX = x + dx;
                if ((uint)targetX >= (uint)atlasWidth)
                {
                    continue;
                }

                int sourceX = x + Math.Clamp(dx, 0, w - 1);
                int source = ((sourceY * atlasWidth) + sourceX) * 4;
                int target = ((targetY * atlasWidth) + targetX) * 4;
                pixels[target] = pixels[source];
                pixels[target + 1] = pixels[source + 1];
                pixels[target + 2] = pixels[source + 2];
                pixels[target + 3] = pixels[source + 3];
            }
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
