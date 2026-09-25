using System.Collections.Immutable;
using System.Diagnostics;
using LitematicaViewer.Core.Model;
using LitematicaViewer.Core.Parsing;

namespace LitematicaViewer.Core.Tests;

// Core 的验收入口。只用 Debug.WriteLine 打桩、Debug.Assert 收口，
// 不引 xUnit：这些检查要的是"在真实文件上现场看一眼"，不是一个可重复的报告。
public static class CoreSmoke
{
    private static int _failures;
    private static int _checks;

    public static int Main(string[] args)
    {
        // 没有这一行时 Debug.WriteLine 只进调试器的输出窗口，从终端 dotnet run 什么都看不到，
        // 而验收标准恰恰是"看桩输出对不对"。Release 下这个 Main 根本不存在，不必条件化。
        using TextWriterTraceListener listener = new(Console.Out);
        Trace.Listeners.Add(listener);
        Trace.AutoFlush = true;

        Console.WriteLine($"CoreSmoke: {args.Length} 个真实文件参数");
        Debug.WriteLine($"[CORE][smoke.begin] args={args.Length}");

        CheckCodecBitWidths();
        CheckCodecRoundTrip();
        CheckPackedLongCountPins();
        CheckSingleStateRegion();
        CheckNegativeSizeRegion();
        CheckIndexRoundTrip();
        CheckErrorInputs();

        foreach (string path in args)
        {
            CheckRealFile(path);
        }

        Debug.WriteLine($"[CORE][smoke.end] checks={_checks} failures={_failures} expected=0");
        Console.WriteLine($"CoreSmoke: checks={_checks} failures={_failures}");

        // 收口断言放在最后而不是每个检查里：中途 Assert 会带走后面的检查，
        // 一次跑完拿到全部失败比第一个失败就停更有用。
        Debug.Assert(_failures == 0, $"CoreSmoke failures={_failures}");
        return _failures == 0 ? 0 : 1;
    }

    private static void CheckCodecBitWidths()
    {
        (int Palette, int Bits)[] cases =
        [
            (1, 0), (2, 2), (3, 2), (4, 2), (5, 3), (17, 5), (256, 8), (257, 9), (459, 9), (4096, 12),
        ];

        foreach ((int palette, int expected) in cases)
        {
            int actual = BlockStatesCodec.GetBitsPerBlock(palette);
            Check(actual == expected, $"bitsPerBlock palette={palette} actual={actual} expected={expected}");
        }
    }

    private static void CheckCodecRoundTrip()
    {
        // palette 8 是 3 位：22 个方块压成 66 位，必然跨 long 边界，
        // 而随机长度的用例几乎撞不到这条分支。
        (int Palette, int Count)[] cases = [(1, 16), (2, 9), (8, 22), (17, 100), (256, 1000), (4096, 100)];

        foreach ((int palette, int count) in cases)
        {
            ImmutableArray<int> source = [.. Enumerable.Range(0, count).Select(i => i % palette)];
            long[] packed = BlockStatesCodec.Pack(source.AsSpan(), palette);
            ImmutableArray<int> restored = BlockStatesCodec.Unpack(packed, count, palette);

            int expectedLongs = BlockStatesCodec.GetPackedLongCount(count, palette);
            Check(packed.Length == expectedLongs, $"packedLongs palette={palette} count={count} actual={packed.Length} expected={expectedLongs}");
            Check(restored.SequenceEqual(source), $"roundTrip palette={palette} count={count} mismatched={CountMismatches(source, restored)}");
        }
    }

    // 这两组数字来自真实文件的实际长度，用来钉死"紧凑位流"这个排法。
    // 换成每 long 塞 64/bits 个会分别变成 499844 与 10519。
    private static void CheckPackedLongCountPins()
    {
        Check(BlockStatesCodec.GetPackedLongCount(3498908, 459) == 492034,
            $"pin 全部的热气球 count=3498908 palette=459 actual={BlockStatesCodec.GetPackedLongCount(3498908, 459)} expected=492034");
        Check(BlockStatesCodec.GetPackedLongCount(126225, 31) == 9862,
            $"pin 水上树屋 count=126225 palette=31 actual={BlockStatesCodec.GetPackedLongCount(126225, 31)} expected=9862");
    }

    private static void CheckSingleStateRegion()
    {
        byte[] bytes = CoreFixtureBuilder.BuildLitematic(
            "single",
            new Vector3I(0, 0, 0),
            new Vector3I(2, 2, 2),
            ["minecraft:air"],
            [0, 0, 0, 0, 0, 0, 0, 0]);

        LoadResult result = LitematicLoader.TryLoad(bytes, "fixture://single-state");
        if (!RequireSuccess(result, "single-state region"))
        {
            return;
        }

        LitematicRegion region = result.Document!.Regions[0];
        Check(region.BlockIndices.Length == 8, $"single-state indices={region.BlockIndices.Length} expected=8");
        Check(region.BlockIndices.All(static i => i == 0), "single-state all indices should be palette[0]");
        Check(result.Document.TotalBlocks == 0, $"single-state totalBlocks={result.Document.TotalBlocks} expected=0");
    }

    // Region.Size 为负是实测存在的：Position(4,2,8) 配 Size(-5,-3,-9)。
    // 直接 Position + Size - 1 会得到 (8,4,16) 这种完全错位的角落。
    private static void CheckNegativeSizeRegion()
    {
        byte[] bytes = CoreFixtureBuilder.BuildLitematic(
            "negative",
            new Vector3I(4, 2, 8),
            new Vector3I(-5, -3, -9),
            ["minecraft:air", "minecraft:stone"],
            [.. Enumerable.Range(0, 135).Select(static i => i % 2)]);

        LoadResult result = LitematicLoader.TryLoad(bytes, "fixture://negative-size");
        if (!RequireSuccess(result, "negative-size region"))
        {
            return;
        }

        LitematicRegion region = result.Document!.Regions[0];
        Check(region.Bounds.Min == new Vector3I(-1, -1, -1), $"negative-size min={region.Bounds.Min} expected=(-1,-1,-1)");
        Check(region.Bounds.Max == new Vector3I(3, 1, 7), $"negative-size max={region.Bounds.Max} expected=(3,1,7)");
        Check(region.Bounds.Size == new Vector3I(5, 3, 9), $"negative-size size={region.Bounds.Size} expected=(5,3,9)");
        Check(region.Volume == 135, $"negative-size volume={region.Volume} expected=135");
        Check(region.BlockIndices.Length == 135, $"negative-size indices={region.BlockIndices.Length} expected=135");
    }

    private static void CheckIndexRoundTrip()
    {
        Vector3I size = new(7, 3, 5);
        byte[] bytes = CoreFixtureBuilder.BuildLitematic(
            "index",
            Vector3I.Zero,
            size,
            ["minecraft:air", "minecraft:stone", "minecraft:dirt"],
            [.. Enumerable.Repeat(0, 7 * 3 * 5)]);

        LoadResult result = LitematicLoader.TryLoad(bytes, "fixture://index-round-trip");
        if (!RequireSuccess(result, "index round trip"))
        {
            return;
        }

        LitematicRegion region = result.Document!.Regions[0];
        int volume = (int)region.Volume;
        int[] probes = [0, 1, size.X - 1, size.X, 1 + (2 * size.X), volume - 1];
        foreach (int index in probes)
        {
            Vector3I local = region.ToLocal(index);
            Check(region.ToIndex(local.X, local.Y, local.Z) == index,
                $"indexRoundTrip index={index} local={local} back={region.ToIndex(local.X, local.Y, local.Z)}");
        }

        Check(region.ToIndex(1, 0, 0) == 1, "index order: x fastest");
        Check(region.ToIndex(0, 0, 1) == size.X, "index order: then z");
        Check(region.ToIndex(0, 1, 0) == size.X * size.Z, "index order: then y");
    }

    private static void CheckErrorInputs()
    {
        byte[] good = CoreFixtureBuilder.BuildLitematic(
            "ok",
            Vector3I.Zero,
            new Vector3I(2, 2, 2),
            ["minecraft:stone"],
            [0, 0, 0, 0, 0, 0, 0, 0]);

        ExpectFailure("empty input", [], LoadErrorKind.Truncated);
        ExpectFailure("garbage bytes", [0x01, 0x02, 0x03], LoadErrorKind.Truncated);
        ExpectFailure("plain text gzip", CoreFixtureBuilder.BuildPlainTextGzipped(), LoadErrorKind.Malformed);
        ExpectFailure("nbt without regions", CoreFixtureBuilder.BuildNbtWithoutRegions(), LoadErrorKind.Malformed);

        // 截断的 gzip 从 GZipStream 里冒出来的是 EndOfStreamException 而不是 InvalidDataException，
        // 所以归到 Truncated。这条是实测结论，不是推测。
        ExpectFailure("truncated gzip", CoreFixtureBuilder.Truncate(good, good.Length / 2), LoadErrorKind.Truncated);
        ExpectFailure("missing file", null, LoadErrorKind.FileNotFound);
    }

    private static void CheckRealFile(string path)
    {
        LoadResult result = LitematicLoader.TryLoadFile(path);
        if (!RequireSuccess(result, $"real file '{path}'"))
        {
            return;
        }

        LitematicDocument document = result.Document!;
        LitematicMetadata metadata = document.Metadata;

        if (metadata.RegionCount != 0)
        {
            Check(document.Regions.Length == metadata.RegionCount,
                $"regions={document.Regions.Length} declared={metadata.RegionCount} expected=equal");
        }

        if (metadata.EnclosingSize != Vector3I.Zero)
        {
            Check(document.Bounds.Size == metadata.EnclosingSize,
                $"bounds={document.Bounds.Size} declaredEnclosing={metadata.EnclosingSize} expected=equal");
        }

        if (metadata.TotalVolume != 0)
        {
            Check(document.TotalVolume == metadata.TotalVolume,
                $"volume={document.TotalVolume} declaredVolume={metadata.TotalVolume} expected=equal");
        }

        Check(document.TotalBlocks <= document.TotalVolume,
            $"totalBlocks={document.TotalBlocks} volume={document.TotalVolume} expected=<=volume");

        // 文件头的 TotalBlocks 与重新数出来的值不总相等，所以只报不判。
        Console.WriteLine(
            $"  {Path.GetFileName(path)}: regions={document.Regions.Length} " +
            $"volume={document.TotalVolume} blocks={document.TotalBlocks} declared={metadata.TotalBlocks} " +
            $"issues={result.Issues.Length}");
        Debug.WriteLine(
            $"[CORE][smoke.file] path='{path}' regions={document.Regions.Length} volume={document.TotalVolume} " +
            $"blocks={document.TotalBlocks} declaredBlocks={metadata.TotalBlocks} " +
            $"declaredBytesMismatch={(document.TotalBlocks != metadata.TotalBlocks)}");

        foreach (string issue in result.Issues)
        {
            Console.WriteLine($"    issue: {issue}");
            Debug.WriteLine($"[CORE][smoke.issue] {issue}");
        }
    }

    private static void ExpectFailure(string what, byte[]? bytes, LoadErrorKind expected)
    {
        LoadResult result = bytes is null
            ? LitematicLoader.TryLoadFile(Path.Combine(Path.GetTempPath(), "litematica-viewer-does-not-exist.litematic"))
            : LitematicLoader.TryLoad(bytes, $"fixture://{what}");

        Check(!result.Success, $"errorInput '{what}' success={result.Success} expected=False");
        Check(result.Error == expected, $"errorInput '{what}' kind={result.Error} expected={expected} message={result.Message}");
        Check(result.Document is null, $"errorInput '{what}' document is null expected=True");
    }

    private static bool RequireSuccess(LoadResult result, string what)
    {
        if (result.Success)
        {
            return true;
        }

        Check(false, $"{what}: load failed kind={result.Error} message={result.Message}");
        return false;
    }

    private static int CountMismatches(ImmutableArray<int> expected, ImmutableArray<int> actual)
    {
        int count = 0;
        for (int i = 0; i < expected.Length && i < actual.Length; i++)
        {
            if (expected[i] != actual[i])
            {
                count++;
            }
        }

        return count;
    }

    private static void Check(bool condition, string what)
    {
        _checks++;
        if (condition)
        {
            Debug.WriteLine($"[CORE][check.ok] {what}");
            return;
        }

        _failures++;
        Console.WriteLine($"FAIL {what}");
        Debug.WriteLine($"[CORE][check.FAIL] {what}");
    }
}
