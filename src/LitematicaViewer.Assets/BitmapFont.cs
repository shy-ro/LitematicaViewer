using System.Text.Json;
using System.Text;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.Versioning;
using StbImageSharp;

namespace LitematicaViewer.Assets;

// Minimal resource-pack font pipeline for static sign text. It follows reference and bitmap
// providers from font/default.json; unsupported providers are skipped instead of replacing text.
public sealed class BitmapFont
{
    private readonly Dictionary<Rune, Glyph> _glyphs;

    private BitmapFont(Dictionary<Rune, Glyph> glyphs)
    {
        _glyphs = glyphs;
    }

    public static BitmapFont Load(PackStack packs, string fontId = "minecraft:default")
    {
        Dictionary<Rune, Glyph> glyphs = [];
        HashSet<string> visited = new(StringComparer.Ordinal);
        LoadFont(packs, NormalizeFontPath(fontId), glyphs, visited);
        return new BitmapFont(glyphs);
    }

    public GeneratedSprite Render(string spriteId, IReadOnlyList<string> lines, uint rgb, bool glowing = false)
    {
        // 告示牌文字会被贴图图集的 mip 链与远景重采样；原先 96x48 的点阵在标准视图中
        // 只剩几个像素宽，中文笔画会糊成一团。保持同一物理片面尺寸，用 2x 点阵烘焙提高临界清晰度。
        const int scale = 2;
        const int width = 96 * scale;
        const int height = 48 * scale;
        const int lineHeight = 10 * scale;
        var rgba = new byte[width * height * 4];
        for (var lineIndex = 0; lineIndex < Math.Min(4, lines.Count); lineIndex++)
        {
            var glyphs = lines[lineIndex].EnumerateRunes().Select(rune =>
                _glyphs.TryGetValue(rune, out var found) ? found : default).ToArray();
            var lineWidth = glyphs.Sum(static glyph => glyph.Advance) * scale;
            var cursor = Math.Max(0, (width - lineWidth) / 2);
            var baseline = 4 * scale + lineIndex * lineHeight;
            foreach (var glyph in glyphs)
            {
                if (glyph.Pixels is not null) BlitGlyph(rgba, width, height, cursor, baseline, glyph, rgb, scale, glowing);
                cursor += glyph.Advance * scale;
            }
        }

        if (OperatingSystem.IsWindows() && lines.Any(line => line.EnumerateRunes().Any(rune =>
                rune.Value != ' ' && !_glyphs.ContainsKey(rune))))
            OverlaySystemFont(rgba, width, height, lines, rgb, glowing);

        return new GeneratedSprite(spriteId, rgba, width, height);
    }

    [SupportedOSPlatform("windows")]
    private static void OverlaySystemFont(byte[] target, int width, int height, IReadOnlyList<string> lines, uint rgb,
        bool glowing)
    {
        using Bitmap bitmap = new(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var font = new Font("Microsoft YaHei UI", 16f, FontStyle.Regular, GraphicsUnit.Pixel))
        using (var brush = new SolidBrush(Color.FromArgb(255, (int)(rgb >> 16), (int)(rgb >> 8 & 0xFF), (int)(rgb & 0xFF))))
        using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            graphics.Clear(Color.Transparent);
            graphics.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
            for (var i = 0; i < Math.Min(4, lines.Count); i++)
            {
                if (!lines[i].EnumerateRunes().Any(rune => rune.Value != ' ')) continue;
                graphics.DrawString(lines[i], font, brush, new RectangleF(0, 4 + i * 20, width, 20), format);
            }
        }

        var area = new Rectangle(0, 0, width, height);
        var data = bitmap.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[Math.Abs(data.Stride)];
            for (var y = 0; y < height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (var x = 0; x < width; x++)
                {
                    var source = x * 4;
                    var alpha = row[source + 3];
                    if (alpha == 0) continue;
                    var destination = (y * width + x) * 4;
                    target[destination] = row[source + 2];
                    target[destination + 1] = row[source + 1];
                    target[destination + 2] = row[source];
                    target[destination + 3] = alpha;
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static void LoadFont(PackStack packs, string path, Dictionary<Rune, Glyph> glyphs,
        HashSet<string> visited)
    {
        if (!visited.Add(path) || !packs.TryRead(path, out var json)) return;
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("providers", out var providers)) return;
        foreach (var provider in providers.EnumerateArray())
        {
            var type = provider.GetProperty("type").GetString();
            if (type == "reference" && provider.TryGetProperty("id", out var reference))
            {
                LoadFont(packs, NormalizeFontPath(reference.GetString()!), glyphs, visited);
                continue;
            }

            if (type != "bitmap" || !provider.TryGetProperty("file", out var file) ||
                !provider.TryGetProperty("chars", out var rows))
            {
                if (type == "legacy_unicode") LoadLegacyUnicode(packs, provider, glyphs);
                continue;
            }
            var textureId = file.GetString()!;
            var (ns, texturePath) = SplitId(textureId);
            if (!packs.TryRead($"assets/{ns}/textures/{texturePath}.png", out var png)) continue;
            var image = ImageResult.FromMemory(png, ColorComponents.RedGreenBlueAlpha);
            var rowStrings = rows.EnumerateArray().Select(static row => row.GetString() ?? string.Empty).ToArray();
            if (rowStrings.Length == 0) continue;
            var columns = rowStrings.Max(static row => row.EnumerateRunes().Count());
            if (columns == 0) continue;
            var cellWidth = image.Width / columns;
            var cellHeight = image.Height / rowStrings.Length;
            for (var rowIndex = 0; rowIndex < rowStrings.Length; rowIndex++)
            {
                var column = 0;
                foreach (var rune in rowStrings[rowIndex].EnumerateRunes())
                {
                    if (rune.Value != 0)
                        glyphs.TryAdd(rune, CropGlyph(image, column * cellWidth, rowIndex * cellHeight,
                            cellWidth, cellHeight));
                    column++;
                }
            }
        }
    }

    private static void LoadLegacyUnicode(PackStack packs, JsonElement provider, Dictionary<Rune, Glyph> glyphs)
    {
        if (!provider.TryGetProperty("sizes", out var sizesElement) ||
            !provider.TryGetProperty("template", out var templateElement)) return;
        var (sizesNs, sizesPath) = SplitId(sizesElement.GetString()!);
        if (!packs.TryRead($"assets/{sizesNs}/{sizesPath}", out var sizes) &&
            !packs.TryRead($"assets/{sizesNs}/textures/{sizesPath}", out sizes)) return;
        var (pageNs, pageTemplate) = SplitId(templateElement.GetString()!);
        Dictionary<int, ImageResult?> pages = [];
        for (var codepoint = 0; codepoint < Math.Min(65536, sizes.Length); codepoint++)
        {
            var bounds = sizes[codepoint];
            if (bounds == 0) continue;
            var pageIndex = codepoint >> 8;
            if (!pages.TryGetValue(pageIndex, out var page))
            {
                var path = pageTemplate.Replace("%s", pageIndex.ToString("x2"), StringComparison.Ordinal);
                page = packs.TryRead($"assets/{pageNs}/textures/{path}.png", out var png)
                    ? ImageResult.FromMemory(png, ColorComponents.RedGreenBlueAlpha)
                    : null;
                pages[pageIndex] = page;
            }
            if (page is null) continue;
            var left = bounds >> 4;
            var right = bounds & 0x0F;
            if (right < left) continue;
            var cell = codepoint & 0xFF;
            glyphs.TryAdd(new Rune(codepoint), CropLegacyGlyph(page, (cell & 15) * 16, (cell >> 4) * 16,
                left, right));
        }
    }

    private static Glyph CropLegacyGlyph(ImageResult image, int cellX, int cellY, int left, int right)
    {
        var sourceWidth = right - left + 1;
        var width = Math.Max(1, (sourceWidth + 1) / 2);
        const int height = 8;
        var pixels = new byte[width * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            byte alpha = 0;
            for (var dy = 0; dy < 2; dy++)
            for (var dx = 0; dx < 2; dx++)
            {
                var sx = cellX + left + Math.Min(sourceWidth - 1, x * 2 + dx);
                var sy = cellY + y * 2 + dy;
                alpha = Math.Max(alpha, image.Data[(sy * image.Width + sx) * 4 + 3]);
            }
            pixels[y * width + x] = alpha;
        }
        return new Glyph(pixels, width, height, 0, 0, width + 1);
    }

    private static Glyph CropGlyph(ImageResult image, int x, int y, int width, int height)
    {
        var left = width;
        var right = -1;
        for (var py = 0; py < height; py++)
        for (var px = 0; px < width; px++)
            if (image.Data[((y + py) * image.Width + x + px) * 4 + 3] != 0)
            {
                left = Math.Min(left, px);
                right = Math.Max(right, px);
            }
        if (right < left) return new Glyph(null, 0, 0, 0, 0, 4);
        var glyphWidth = right - left + 1;
        var pixels = new byte[glyphWidth * height];
        for (var py = 0; py < height; py++)
        for (var px = 0; px < glyphWidth; px++)
            pixels[py * glyphWidth + px] = image.Data[((y + py) * image.Width + x + left + px) * 4 + 3];
        return new Glyph(pixels, glyphWidth, height, left, 0, Math.Min(9, glyphWidth + 1));
    }

    private static void BlitGlyph(byte[] target, int targetWidth, int targetHeight, int x, int y, Glyph glyph,
        uint rgb, int scale, bool glowing)
    {
        for (var py = 0; py < glyph.Height; py++)
        for (var px = 0; px < glyph.Width; px++)
        {
            var alpha = glyph.Pixels![py * glyph.Width + px];
            if (alpha == 0) continue;
            for (var sy = 0; sy < scale; sy++)
            for (var sx = 0; sx < scale; sx++)
            {
                var tx = x + px * scale + sx;
                var ty = y + py * scale + sy;
                if ((uint)tx >= (uint)targetWidth || (uint)ty >= (uint)targetHeight) continue;
                var targetIndex = (ty * targetWidth + tx) * 4;
                target[targetIndex] = (byte)(rgb >> 16);
                target[targetIndex + 1] = (byte)(rgb >> 8);
                target[targetIndex + 2] = (byte)rgb;
                target[targetIndex + 3] = glowing ? (byte)Math.Max((int)alpha, 224) : alpha;
            }
        }
    }

    private static string NormalizeFontPath(string id)
    {
        var (ns, path) = SplitId(id);
        return $"assets/{ns}/font/{path}.json";
    }

    private static (string Ns, string Path) SplitId(string id)
    {
        var colon = id.IndexOf(':');
        return colon < 0 ? ("minecraft", id) : (id[..colon], id[(colon + 1)..]);
    }

    private readonly record struct Glyph(byte[]? Pixels, int Width, int Height, int Left, int Top, int Advance);
}
