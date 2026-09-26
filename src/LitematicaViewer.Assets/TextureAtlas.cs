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

    private TextureAtlas(int width, int height, byte[] pixels, Dictionary<string, SpriteRect> rects, int missingCount)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
        _rects = rects;
        MissingCount = missingCount;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }
    public int MissingCount { get; }

    public IReadOnlyCollection<SpriteRect> Rects => _rects.Values;

    public bool TryGetRect(string sprite, out SpriteRect rect) => _rects.TryGetValue(sprite, out rect!);

    // 逐个从包栈里读 PNG 并装箱。缺的贴图给品红/黑棋盘（MC missingno 的样式），
    // 网格照常生成，缺什么在画面上一眼能认出来——比静默用白块好查得多。
    public static TextureAtlas Build(PackStack packs, IEnumerable<string> sprites)
    {
        List<(string Sprite, byte[] Rgba, int W, int H)> decoded = [];
        int missing = 0;

        foreach (string sprite in sprites.Distinct().OrderBy(s => s, StringComparer.Ordinal))
        {
            (string ns, string path) = SplitId(sprite);
            if (packs.TryRead($"assets/{ns}/textures/{path}.png", out byte[] png))
            {
                ImageResult image = ImageResult.FromMemory(png, ColorComponents.RedGreenBlueAlpha);
                (byte[] rgba, int h) = image.Width == image.Height
                    ? (image.Data, image.Height)
                    : TakeFirstFrame(image);
                decoded.Add((sprite, rgba, image.Width, h));
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
                missing++;
            }
        }

        // 行式装箱：按高降序排，一行放不下就换行。图集宽先定（容纳最宽的 sprite，
        // 抬到 2 的幂，GLES 3.0 对 NPOT 其实宽容，但 2 的幂将来开 mipmap 不用重排），
        // 高度随行数增长，最后同样抬到 2 的幂。
        decoded.Sort((a, b) => (b.H - a.H) != 0 ? b.H - a.H : string.CompareOrdinal(a.Sprite, b.Sprite));

        // 宽度按总面积估一个接近正方的值（夹在 [最宽 sprite, 2048] 里）：
        // 只按最宽 sprite 定宽的话，几十张 16px 的小图会摞成 16x1024 的细高条，
        // 采样与将来开 mipmap 都难看。
        long totalArea = decoded.Sum(d => (long)d.W * d.H);
        int targetSide = Pow2Ceiling((int)MathF.Sqrt(totalArea));
        int widest = Pow2Ceiling(decoded.Count == 0 ? 16 : decoded.Max(d => d.W));
        int atlasWidth = Math.Max(widest, Math.Min(targetSide, 2048));
        int cursorX = 0;
        int cursorY = 0;
        int rowHeight = 0;
        Dictionary<string, SpriteRect> rects = new(StringComparer.Ordinal);
        foreach ((string sprite, _, int w, int h) in decoded)
        {
            if (cursorX + w > atlasWidth && cursorX > 0)
            {
                cursorX = 0;
                cursorY += rowHeight;
                rowHeight = 0;
            }

            rects[sprite] = new SpriteRect(sprite, cursorX, cursorY, w, h);
            cursorX += w;
            rowHeight = Math.Max(rowHeight, h);
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
                Buffer.BlockCopy(rgba, source, pixels, target, w * 4);
            }
        }

        return new TextureAtlas(atlasWidth, atlasHeight, pixels, rects, missing);
    }

    // 动画贴图（水、熔岩、火）是竖排的多帧 + 同名 .mcmeta。首版只要静止画面：
    // 取最顶上的正方形首帧，其余帧直接丢。帧时序将来做「活水」时再说。
    private static (byte[] Rgba, int Height) TakeFirstFrame(ImageResult image)
    {
        int frame = image.Width * 4;
        byte[] first = new byte[frame];
        Buffer.BlockCopy(image.Data, 0, first, 0, frame);
        return (first, image.Width);
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
