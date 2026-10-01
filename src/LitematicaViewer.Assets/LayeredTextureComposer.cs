using StbImageSharp;

namespace LitematicaViewer.Assets;

public static class LayeredTextureComposer
{
    public static GeneratedSprite RenderTintedLayers(PackStack packs, string spriteId,
        IReadOnlyList<(string Sprite, uint Rgb)> layers, int width = 64, int height = 64)
    {
        var result = new byte[width * height * 4];
        foreach (var (sprite, rgb) in layers)
        {
            var (ns, path) = SplitId(sprite);
            if (!packs.TryRead($"assets/{ns}/textures/{path}.png", out var png)) continue;
            var image = ImageResult.FromMemory(png, ColorComponents.RedGreenBlueAlpha);
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var sx = x * image.Width / width;
                var sy = y * image.Height / height;
                var source = (sy * image.Width + sx) * 4;
                var alpha = image.Data[source + 3] / 255f;
                if (alpha <= 0f) continue;
                var target = (y * width + x) * 4;
                var oldAlpha = result[target + 3] / 255f;
                var combined = alpha + oldAlpha * (1f - alpha);
                if (combined <= 0f) continue;
                BlendChannel(result, target, (byte)(rgb >> 16), alpha, oldAlpha, combined);
                BlendChannel(result, target + 1, (byte)(rgb >> 8), alpha, oldAlpha, combined);
                BlendChannel(result, target + 2, (byte)rgb, alpha, oldAlpha, combined);
                result[target + 3] = (byte)Math.Clamp(combined * 255f, 0, 255);
            }
        }

        return new GeneratedSprite(spriteId, result, width, height);
    }

    private static void BlendChannel(byte[] target, int index, byte source, float alpha, float oldAlpha,
        float combined)
    {
        target[index] = (byte)Math.Clamp((source * alpha + target[index] * oldAlpha * (1f - alpha)) / combined,
            0, 255);
    }

    private static (string Ns, string Path) SplitId(string id)
    {
        var colon = id.IndexOf(':');
        return colon < 0 ? ("minecraft", id) : (id[..colon], id[(colon + 1)..]);
    }
}
