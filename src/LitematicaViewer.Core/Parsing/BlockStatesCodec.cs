using System.Collections.Immutable;
using System.Diagnostics;

namespace LitematicaViewer.Core.Parsing;

public static class BlockStatesCodec
{
    // 位宽下限是 2 而不是 1：paletteSize == 2 时 Minecraft 仍然用 2 位/方块。
    // paletteSize <= 1 则完全不写 BlockStates，0 位。
    public static int GetBitsPerBlock(int paletteSize)
    {
        if (paletteSize <= 1)
        {
            return 0;
        }

        int bits = 1;
        while ((1 << bits) < paletteSize)
        {
            bits++;
        }

        return Math.Max(2, bits);
    }

    // 一个序列占多少个 long。用的是紧凑位流：第 i 个方块占第 i*bits 到 i*bits+bits-1 位，
    // 允许跨 long 边界。
    //
    // 不要改成"每个 long 塞 64/bits 个"那种排法。两种排法在 bits 整除 64 时结果完全一样，
    // 所以只在 palette 稍大时才露出差别，而且不报错、只是颜色错位：
    // 实测 459 色（9 位）那类文件用紧凑位流算出 492034 个 long，与文件里实际的长度吻合；
    // 按每 long 7 个算会得到 499844。
    public static int GetPackedLongCount(int count, int paletteSize)
    {
        int bits = GetBitsPerBlock(paletteSize);
        return bits == 0 ? 0 : (int)((((long)count * bits) + 63) >> 6);
    }

    public static ImmutableArray<int> Unpack(ReadOnlySpan<long> data, int count, int paletteSize)
    {
        int[] indices = new int[count];
        int bits = GetBitsPerBlock(paletteSize);
        if (bits == 0)
        {
            // 单状态区域：BlockStates 为空，全部落在 palette[0]。
            return [.. indices];
        }

        int mask = (1 << bits) - 1;
        int straddles = 0;
        int truncated = 0;
        for (int i = 0; i < count; i++)
        {
            long bitIndex = (long)i * bits;
            int startLong = (int)(bitIndex >> 6);
            int startBit = (int)(bitIndex & 63);
            if (startLong >= data.Length)
            {
                // 截断：剩下的全部留在 0。调用方比对声明长度后自行决定是跳过还是告警，
                // 这里不抛异常，因为真实文件里确实出现过长度不足的区域。
                truncated = count - i;
                break;
            }

            ulong low = (ulong)data[startLong] >> startBit;
            if (startBit + bits <= 64 || startLong + 1 >= data.Length)
            {
                indices[i] = (int)(low & (ulong)mask);
                continue;
            }

            // 只有 startBit + bits > 64 才走到这里，因此 startBit >= 1，
            // 下面的左移量最大 63。写成 (64 - startBit) 是安全的；若 startBit 可能为 0，
            // C# 的移位只取低 6 位，移 64 会静默变成移 0，得到错误的值而不是报错。
            straddles++;
            indices[i] = (int)((low | ((ulong)data[startLong + 1] << (64 - startBit))) & (ulong)mask);
        }

        Debug.WriteLine(
            $"[CORE][codec.unpack] count={count} paletteSize={paletteSize} bits={bits} " +
            $"longs={data.Length} straddled={straddles} truncatedTail={truncated} expected=0");

        return [.. indices];
    }

    public static long[] Pack(ReadOnlySpan<int> indices, int paletteSize)
    {
        int bits = GetBitsPerBlock(paletteSize);
        if (bits == 0)
        {
            return [];
        }

        long[] data = new long[GetPackedLongCount(indices.Length, paletteSize)];
        int mask = (1 << bits) - 1;
        for (int i = 0; i < indices.Length; i++)
        {
            ulong value = (ulong)(indices[i] & mask);
            long bitIndex = (long)i * bits;
            int startLong = (int)(bitIndex >> 6);
            int startBit = (int)(bitIndex & 63);
            data[startLong] |= (long)(value << startBit);
            if (startBit + bits > 64)
            {
                data[startLong + 1] |= (long)(value >> (64 - startBit));
            }
        }

        return data;
    }
}
