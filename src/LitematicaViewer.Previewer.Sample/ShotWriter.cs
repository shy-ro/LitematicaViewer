using System.Diagnostics;

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
        writer.Write(System.Text.Encoding.ASCII.GetBytes($"{width} {height}\n255\n"));
        byte[] row = new byte[width * 3];
        for (int y = height - 1; y >= 0; y--)
        {
            int source = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                row[x * 3 + 0] = rgba[source + x * 4 + 0];
                row[x * 3 + 1] = rgba[source + x * 4 + 1];
                row[x * 3 + 2] = rgba[source + x * 4 + 2];
            }

            writer.Write(row);
        }
    }
}
