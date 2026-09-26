using System.Diagnostics;
using System.Text;

namespace LitematicaViewer.Previewer.Sample;

// --shot 的落盘端。PPM(P6)：无压缩、无依赖、十几行写完，PIL / IrfanView / GIMP 都认。
// glReadPixels 的行序是自下而上（GL 的原点在左下），PPM 的原点在左上，写的时候翻过来。
internal static class ShotWriter
{
    internal static void Write(string path, byte[] rgba, int width, int height)
    {
        if (rgba.Length < width * height * 4)
        {
            Debug.WriteLine(
                $"[SAMPLE][shot] 像素数据不够 bytes={rgba.Length} expected={(long)width * height * 4} " +
                $"size={width}x{height} note=读回失败，不落盘");
            return;
        }

        using FileStream file = new(path, FileMode.Create, FileAccess.Write);
        using BinaryWriter writer = new(file);
        writer.Write("P6\n"u8);
        writer.Write(Encoding.ASCII.GetBytes($"{width} {height}\n255\n"));
        var row = new byte[width * 3];
        for (var y = height - 1; y >= 0; y--)
        {
            var source = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                row[x * 3 + 0] = rgba[source + x * 4 + 0];
                row[x * 3 + 1] = rgba[source + x * 4 + 1];
                row[x * 3 + 2] = rgba[source + x * 4 + 2];
            }

            writer.Write(row);
        }
    }

    // alpha 通道单独落盘（P5 灰度）：RGB 正确不代表 alpha 正确，
    // 而片元着色器按 alpha<0.5 discard——alpha 坏了的表现就是「面凭空消失」。
    internal static void WriteAtlasAlpha(string path, byte[] rgbaLevel0, int width, int height)
    {
        using FileStream file = new(path, FileMode.Create, FileAccess.Write);
        using BinaryWriter writer = new(file);
        writer.Write("P5\n"u8);
        writer.Write(Encoding.ASCII.GetBytes($"{width} {height}\n255\n"));
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            writer.Write(rgbaLevel0[(y * width + x) * 4 + 3]);
    }

    // 把上传给 GPU 的图集层 0 dump 成 PPM（RGBA 丢 alpha，原点已在左上不用翻）。
    // 用途：GPU 画面花 vs 软件渲染正常时，先确认 GPU 收到的纹素本身是对的——
    // 上传链（Copy/staging/TexImage2D）与 CPU 侧的图集是两份数据，都得验。
    internal static void WriteAtlas(string path, byte[] rgbaLevel0, int width, int height)
    {
        using FileStream file = new(path, FileMode.Create, FileAccess.Write);
        using BinaryWriter writer = new(file);
        writer.Write("P6\n"u8);
        writer.Write(Encoding.ASCII.GetBytes($"{width} {height}\n255\n"));
        for (var y = 0; y < height; y++)
        {
            var source = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                writer.Write(rgbaLevel0[source + x * 4 + 0]);
                writer.Write(rgbaLevel0[source + x * 4 + 1]);
                writer.Write(rgbaLevel0[source + x * 4 + 2]);
            }
        }
    }
}
