using System.Diagnostics;
using System.IO.Compression;

namespace LitematicaViewer.Core.Io;

public enum NbtCompression
{
    None,
    GZip,
    ZLib
}

public readonly record struct NbtContainer(byte[] Payload, NbtCompression Compression);

// Poly.NBT 完全不碰压缩：gzip / zlib / Deflate 在它的程序集里零出现。
// .litematic 与 .schem 都是 gzip 包裹的，解压必须自己做。
public static class NbtContainerReader
{
    public static NbtContainer Read(ReadOnlySpan<byte> bytes)
    {
        var compression = Sniff(bytes);
        var payload = compression switch
        {
            NbtCompression.GZip => Decompress(bytes, static s => new GZipStream(s, CompressionMode.Decompress)),
            NbtCompression.ZLib => Decompress(bytes, static s => new ZLibStream(s, CompressionMode.Decompress)),
            _ => bytes.ToArray()
        };

        Debug.WriteLine(
            $"[CORE][container] head={Convert.ToHexString(bytes[..Math.Min(16, bytes.Length)])} " +
            $"compression={compression} rawBytes={bytes.Length} payloadBytes={payload.Length}");

        return new NbtContainer(payload, compression);
    }

    private static NbtCompression Sniff(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B) return NbtCompression.GZip;

        // zlib：首字节低半字节是压缩方法（8 = deflate），前两字节大端组成 16 位值必须是 31 的倍数。
        // 不硬编码 78 01 / 78 9C / 78 DA，因为窗口大小与预设等级的组合不止那三种。
        if (bytes.Length >= 2 && (bytes[0] & 0x0F) == 0x08 && ((bytes[0] << 8) | bytes[1]) % 31 == 0)
            return NbtCompression.ZLib;

        return NbtCompression.None;
    }

    private static byte[] Decompress(ReadOnlySpan<byte> bytes, Func<Stream, Stream> wrap)
    {
        using MemoryStream input = new(bytes.ToArray(), false);
        using var decompressor = wrap(input);
        using MemoryStream output = new();
        decompressor.CopyTo(output);
        return output.ToArray();
    }
}
