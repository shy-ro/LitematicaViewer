using System.Collections.Immutable;
using System.Diagnostics;
using System.Numerics;
using LitematicaViewer.Core.Model;
using LitematicaViewer.Core.Parsing;
using LitematicaViewer.Core.Picking;

namespace LitematicaViewer.Core.Tests;

// Core 的验收入口。只用 Debug.WriteLine 打桩、Debug.Assert 收口，
// 不引 xUnit：这些检查要的是"在真实文件上现场看一眼"，不是一个可重复的报告。
public static class CoreSmoke
{
    private const string AirName = "minecraft:air";
    private const string StoneName = "minecraft:stone";
    private const string DirtName = "minecraft:dirt";
    private static int _failures;
    private static int _checks;

    public static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--scan-entities")
            return ScanEntities(args[1..]);
        if (args.Length >= 3 && args[0] == "--dump-entity")
        {
            var nonEmpty = args.Contains("--nonempty", StringComparer.Ordinal);
            return DumpEntity(args[1], args[2..].Where(static arg => arg != "--nonempty").ToArray(), nonEmpty);
        }

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
        CheckEntityData();
        CheckErrorInputs();
        CheckPicking();
        CheckPickingNearestRegion();
        CheckPickingAgainstReference();

        foreach (var path in args) CheckRealFile(path);

        Debug.WriteLine($"[CORE][smoke.end] checks={_checks} failures={_failures} expected=0");
        Console.WriteLine($"CoreSmoke: checks={_checks} failures={_failures}");

        // 收口断言放在最后而不是每个检查里：中途 Assert 会带走后面的检查，
        // 一次跑完拿到全部失败比第一个失败就停更有用。
        Debug.Assert(_failures == 0, $"CoreSmoke failures={_failures}");
        return _failures == 0 ? 0 : 1;
    }

    private static void CheckEntityData()
    {
        var result = LitematicLoader.TryLoad(CoreFixtureBuilder.BuildEntityLitematic(), "fixture://entities");
        Check(result.Success, "entity fixture parses");
        if (!result.Success) return;
        var region = result.Document!.Regions[0];
        Check(region.BlockEntities.Count == 1, $"block entities={region.BlockEntities.Count} expected=1");
        Check(region.BlockEntities.TryGetValue(new Vector3I(1, 2, 3), out var sign),
            "block entity indexed by local position");
        Check(sign?.Data.GetMap("front_text")?.GetString("color") == "red", "nested block entity NBT preserved");
        Check(region.Entities.Length == 1, $"entities={region.Entities.Length} expected=1");
        Check(region.Entities[0].Position == new Vector3(1.5f, 2.5f, 3.5f),
            $"entity position={region.Entities[0].Position} expected=<1.5,2.5,3.5>");
        Check(region.Entities[0].Data.GetMap("Item")?.GetString("id") == "minecraft:stone",
            "nested entity item NBT preserved");
    }

    private static int ScanEntities(string[] roots)
    {
        Dictionary<string, int> blockEntityIds = new(StringComparer.Ordinal);
        Dictionary<string, int> entityIds = new(StringComparer.Ordinal);
        Dictionary<string, int> blockEntityKeys = new(StringComparer.Ordinal);
        Dictionary<string, int> entityKeys = new(StringComparer.Ordinal);
        var files = 0;
        var failures = 0;
        foreach (var path in roots.SelectMany(EnumerateLitematics))
        {
            files++;
            var result = LitematicLoader.TryLoadFile(path);
            if (!result.Success)
            {
                failures++;
                Console.WriteLine($"PARSE FAIL {path}: {result.Error}");
                continue;
            }

            foreach (var region in result.Document!.Regions)
            {
                foreach (var blockEntity in region.BlockEntities.Values)
                {
                    Increment(blockEntityIds, blockEntity.Id);
                    foreach (var key in blockEntity.Data.Values.Keys) Increment(blockEntityKeys, key);
                }

                foreach (var entity in region.Entities)
                {
                    Increment(entityIds, entity.Id);
                    foreach (var key in entity.Data.Values.Keys) Increment(entityKeys, key);
                }
            }
        }

        Console.WriteLine($"files={files} failures={failures}");
        PrintHistogram("blockEntityIds", blockEntityIds);
        PrintHistogram("entityIds", entityIds);
        PrintHistogram("blockEntityKeys", blockEntityKeys);
        PrintHistogram("entityKeys", entityKeys);
        return failures == 0 ? 0 : 1;
    }

    private static int DumpEntity(string requestedId, string[] roots, bool nonEmpty)
    {
        foreach (var path in roots.SelectMany(EnumerateLitematics))
        {
            var result = LitematicLoader.TryLoadFile(path);
            if (!result.Success) continue;
            foreach (var region in result.Document!.Regions)
            {
                var blockEntity = region.BlockEntities.Values.FirstOrDefault(value => value.Id == requestedId &&
                    (!nonEmpty || HasVisibleData(value.Data)));
                if (blockEntity is not null)
                {
                    Console.WriteLine($"file={path}\nregion={region.Name}\nposition={blockEntity.Position}\n{FormatNbt(blockEntity.Data)}");
                    return 0;
                }

                var entity = region.Entities.FirstOrDefault(value => value.Id == requestedId);
                if (entity is not null)
                {
                    Console.WriteLine($"file={path}\nregion={region.Name}\nposition={entity.Position}\n{FormatNbt(entity.Data)}");
                    return 0;
                }
            }
        }

        Console.WriteLine($"not found: {requestedId}");
        return 1;
    }

    private static bool HasVisibleData(NbtMap data)
    {
        if (data.GetMap("front_text")?.GetSequence("messages") is { } messages &&
            messages.Values.Any(static value => value.AsString() is { Length: > 2 } text && text != "{\"text\":\"\"}"))
            return true;
        if (data.GetSequence("Patterns") is { Values.Length: > 0 }) return true;
        return data.GetMap("Item")?.GetString("id") is not null;
    }

    private static string FormatNbt(NbtData value) => value switch
    {
        NbtInteger integer => integer.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        NbtFloating floating => floating.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        NbtText text => System.Text.Json.JsonSerializer.Serialize(text.Value),
        NbtBytes bytes => $"bytes[{bytes.Values.Length}]",
        NbtInts ints => "[" + string.Join(',', ints.Values) + "]",
        NbtLongs longs => "[" + string.Join(',', longs.Values) + "]",
        NbtSequence sequence => "[" + string.Join(',', sequence.Values.Select(FormatNbt)) + "]",
        NbtMap map => "{" + string.Join(',', map.Values.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => System.Text.Json.JsonSerializer.Serialize(pair.Key) + ":" + FormatNbt(pair.Value))) + "}",
        _ => value.ToString() ?? string.Empty
    };

    private static IEnumerable<string> EnumerateLitematics(string path)
    {
        if (File.Exists(path)) return [path];
        return Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*.litematic", SearchOption.AllDirectories)
            : [];
    }

    private static void Increment(Dictionary<string, int> histogram, string key)
    {
        histogram.TryGetValue(key, out var count);
        histogram[key] = count + 1;
    }

    private static void PrintHistogram(string label, Dictionary<string, int> histogram)
    {
        Console.WriteLine(label + "=" + string.Join(", ", histogram
            .OrderByDescending(static pair => pair.Value)
            .ThenBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => $"{pair.Key}:{pair.Value}")));
    }

    private static void CheckCodecBitWidths()
    {
        (int Palette, int Bits)[] cases =
        [
            (1, 0), (2, 2), (3, 2), (4, 2), (5, 3), (17, 5), (256, 8), (257, 9), (459, 9), (4096, 12)
        ];

        foreach (var (palette, expected) in cases)
        {
            var actual = BlockStatesCodec.GetBitsPerBlock(palette);
            Check(actual == expected, $"bitsPerBlock palette={palette} actual={actual} expected={expected}");
        }
    }

    private static void CheckCodecRoundTrip()
    {
        // palette 8 是 3 位：22 个方块压成 66 位，必然跨 long 边界，
        // 而随机长度的用例几乎撞不到这条分支。
        (int Palette, int Count)[] cases = [(1, 16), (2, 9), (8, 22), (17, 100), (256, 1000), (4096, 100)];

        foreach (var (palette, count) in cases)
        {
            ImmutableArray<int> source = [.. Enumerable.Range(0, count).Select(i => i % palette)];
            var packed = BlockStatesCodec.Pack(source.AsSpan(), palette);
            var restored = BlockStatesCodec.Unpack(packed, count, palette);

            var expectedLongs = BlockStatesCodec.GetPackedLongCount(count, palette);
            Check(packed.Length == expectedLongs,
                $"packedLongs palette={palette} count={count} actual={packed.Length} expected={expectedLongs}");
            Check(restored.SequenceEqual(source),
                $"roundTrip palette={palette} count={count} mismatched={CountMismatches(source, restored)}");
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
        var bytes = CoreFixtureBuilder.BuildLitematic(
            "single",
            new Vector3I(0, 0, 0),
            new Vector3I(2, 2, 2),
            ["minecraft:air"],
            [0, 0, 0, 0, 0, 0, 0, 0]);

        var result = LitematicLoader.TryLoad(bytes, "fixture://single-state");
        if (!RequireSuccess(result, "single-state region")) return;

        var region = result.Document!.Regions[0];
        Check(region.BlockIndices.Length == 8, $"single-state indices={region.BlockIndices.Length} expected=8");
        Check(region.BlockIndices.All(static i => i == 0), "single-state all indices should be palette[0]");
        Check(result.Document.TotalBlocks == 0, $"single-state totalBlocks={result.Document.TotalBlocks} expected=0");
    }

    // Region.Size 为负是实测存在的：Position(4,2,8) 配 Size(-5,-3,-9)。
    // 直接 Position + Size - 1 会得到 (8,4,16) 这种完全错位的角落。
    private static void CheckNegativeSizeRegion()
    {
        var bytes = CoreFixtureBuilder.BuildLitematic(
            "negative",
            new Vector3I(4, 2, 8),
            new Vector3I(-5, -3, -9),
            ["minecraft:air", "minecraft:stone"],
            [.. Enumerable.Range(0, 135).Select(static i => i % 2)]);

        var result = LitematicLoader.TryLoad(bytes, "fixture://negative-size");
        if (!RequireSuccess(result, "negative-size region")) return;

        var region = result.Document!.Regions[0];
        Check(region.Bounds.Min == new Vector3I(-1, -1, -1),
            $"negative-size min={region.Bounds.Min} expected=(-1,-1,-1)");
        Check(region.Bounds.Max == new Vector3I(3, 1, 7), $"negative-size max={region.Bounds.Max} expected=(3,1,7)");
        Check(region.Bounds.Size == new Vector3I(5, 3, 9), $"negative-size size={region.Bounds.Size} expected=(5,3,9)");
        Check(region.Volume == 135, $"negative-size volume={region.Volume} expected=135");
        Check(region.BlockIndices.Length == 135, $"negative-size indices={region.BlockIndices.Length} expected=135");
    }

    private static void CheckIndexRoundTrip()
    {
        Vector3I size = new(7, 3, 5);
        var bytes = CoreFixtureBuilder.BuildLitematic(
            "index",
            Vector3I.Zero,
            size,
            ["minecraft:air", "minecraft:stone", "minecraft:dirt"],
            [.. Enumerable.Repeat(0, 7 * 3 * 5)]);

        var result = LitematicLoader.TryLoad(bytes, "fixture://index-round-trip");
        if (!RequireSuccess(result, "index round trip")) return;

        var region = result.Document!.Regions[0];
        var volume = (int)region.Volume;
        int[] probes = [0, 1, size.X - 1, size.X, 1 + 2 * size.X, volume - 1];
        foreach (var index in probes)
        {
            var local = region.ToLocal(index);
            Check(region.ToIndex(local.X, local.Y, local.Z) == index,
                $"indexRoundTrip index={index} local={local} back={region.ToIndex(local.X, local.Y, local.Z)}");
        }

        Check(region.ToIndex(1, 0, 0) == 1, "index order: x fastest");
        Check(region.ToIndex(0, 0, 1) == size.X, "index order: then z");
        Check(region.ToIndex(0, 1, 0) == size.X * size.Z, "index order: then y");
    }

    private static void CheckErrorInputs()
    {
        var good = CoreFixtureBuilder.BuildLitematic(
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

    // 用"单块实心 + 六个方向"来钉：命中哪一格、从哪个面进去、距离多少，三件事分开断言。
    // 放在随机图案里这三样会互相掩盖，一个错了往往表现为另两个也不对。
    private static void CheckPicking()
    {
        // 5×5×5 里只放一个石头，局部 (2,2,2)，下标 = (2*5+2)*5+2 = 62。
        var indices = new int[125];
        indices[62] = 1;

        var loaded = LitematicLoader.TryLoad(
            CoreFixtureBuilder.BuildLitematic("pick", Vector3I.Zero, new Vector3I(5, 5, 5), [AirName, StoneName],
                [.. indices]),
            "fixture://pick-single-block");
        if (!RequireSuccess(loaded, "pick single block")) return;

        var document = loaded.Document!;

        // 方块占 [2,3)³，所以从负方向打进来距离是 12（到 x=2），从正方向是 9（到 x=3）。
        (Vector3 Origin, Vector3 Direction, VoxelFace Face, float Distance)[] axes =
        [
            (new Vector3(-10f, 2.5f, 2.5f), new Vector3(1f, 0f, 0f), VoxelFace.West, 12f),
            (new Vector3(12f, 2.5f, 2.5f), new Vector3(-1f, 0f, 0f), VoxelFace.East, 9f),
            (new Vector3(2.5f, -10f, 2.5f), new Vector3(0f, 1f, 0f), VoxelFace.Down, 12f),
            (new Vector3(2.5f, 12f, 2.5f), new Vector3(0f, -1f, 0f), VoxelFace.Up, 9f),
            (new Vector3(2.5f, 2.5f, -10f), new Vector3(0f, 0f, 1f), VoxelFace.North, 12f),
            (new Vector3(2.5f, 2.5f, 12f), new Vector3(0f, 0f, -1f), VoxelFace.South, 9f)
        ];

        foreach (var (origin, direction, face, distance) in axes)
        {
            var hit = VoxelPicker.TryPick(document, origin, direction, out var pick, 64f);
            Check(hit, $"pick.axis origin={origin} dir={direction} hit={hit} expected=True");
            if (!hit) continue;

            Check(pick.BlockPosition == new Vector3I(2, 2, 2),
                $"pick.axis origin={origin} block={pick.BlockPosition} expected=(2,2,2)");
            Check(pick.Face == face, $"pick.axis origin={origin} face={pick.Face} expected={face}");
            Check(MathF.Abs(pick.Distance - distance) < 1e-3f,
                $"pick.axis origin={origin} distance={pick.Distance} expected={distance}");
            Check(pick.State.Name == StoneName, $"pick.axis origin={origin} state={pick.State} expected={StoneName}");
        }

        // 起点在方块内部：没有穿过任何一个面，距离是 0。面用 None 而不是硬凑一个方向，
        // 否则调用方会拿它去算相邻方块，得到一个并不相邻的坐标。
        var inside = VoxelPicker.TryPick(document, new Vector3(2.5f, 2.5f, 2.5f), new Vector3(1f, 0f, 0f),
            out var inner, 64f);
        Check(inside && inner.Face == VoxelFace.None && inner.Distance == 0f,
            $"pick.inside hit={inside} face={(inside ? inner.Face : VoxelFace.None)} " +
            $"distance={(inside ? inner.Distance : -1f)} expected=None/0");

        // 起点在区域内部的空气里：脸由进入那一格的方向决定，不是 None。
        var inAir = VoxelPicker.TryPick(document, new Vector3(0.5f, 2.5f, 2.5f), new Vector3(1f, 0f, 0f),
            out var airHit, 64f);
        Check(inAir && airHit.Face == VoxelFace.West && MathF.Abs(airHit.Distance - 1.5f) < 1e-3f,
            $"pick.innerAir hit={inAir} face={(inAir ? airHit.Face : VoxelFace.None)} " +
            $"distance={(inAir ? airHit.Distance : -1f)} expected=West/1.5");

        Check(!VoxelPicker.TryPick(document, new Vector3(-10f, 2.5f, 2.5f), new Vector3(-1f, 0f, 0f), out _, 64f),
            "pick.behind expected=False");
        Check(!VoxelPicker.TryPick(document, new Vector3(-10f, 2.5f, 2.5f), new Vector3(1f, 0f, 0f), out _, 2f),
            "pick.maxDistanceTooShort expected=False");
        Check(!VoxelPicker.TryPick(document, new Vector3(-10f, 2.5f, 2.5f), Vector3.Zero, out _, 64f),
            "pick.zeroDirection expected=False");
    }

    // 两个区域一近一远，而远的那个在文件里排在前面。遍历顺序里先撞到谁，和"哪个更近"
    // 是两件事；只按前者返回的话，多区域重叠时会给出一个更远、甚至被挡住的方块。
    private static void CheckPickingNearestRegion()
    {
        RegionSpec far = new("far", new Vector3I(10, 0, 0), new Vector3I(3, 3, 3), [StoneName], [.. new int[27]]);
        RegionSpec near = new("near", new Vector3I(0, 0, 0), new Vector3I(3, 3, 3), [StoneName], [.. new int[27]]);

        var loaded = LitematicLoader.TryLoad(
            CoreFixtureBuilder.BuildLitematic(true, far, near),
            "fixture://pick-nearest-region");
        if (!RequireSuccess(loaded, "pick nearest region")) return;

        var hit = VoxelPicker.TryPick(loaded.Document!, new Vector3(-10f, 1.5f, 1.5f), new Vector3(1f, 0f, 0f),
            out var pick, 64f);
        Check(hit, $"pick.nearest hit={hit} expected=True");
        if (!hit) return;

        Check(pick.Region.Name == "near", $"pick.nearest region='{pick.Region.Name}' expected='near'");
        Check(pick.BlockPosition == new Vector3I(0, 1, 1), $"pick.nearest block={pick.BlockPosition} expected=(0,1,1)");
        Check(MathF.Abs(pick.Distance - 10f) < 1e-3f, $"pick.nearest distance={pick.Distance} expected=10");
    }

    // 对拍基准在 Tests/PickingReference.cs，故意写成最笨的等步长采样。
    // 两边除了"射线是哪条"之外不共享任何逻辑：共享了就等于自己确认自己。
    private static void CheckPickingAgainstReference()
    {
        const int SizeX = 8;
        const int SizeY = 6;
        const int SizeZ = 4;
        var indices = new int[SizeX * SizeY * SizeZ];

        for (var y = 0; y < SizeY; y++)
        for (var z = 0; z < SizeZ; z++)
        for (var x = 0; x < SizeX; x++)
        {
            var index = (y * SizeZ + z) * SizeX + x;

            // 稀疏且不成层。一层一层的图案会让"整格跳过去"这类错误蒙混过关。
            indices[index] = (x * 7 + y * 13 + z * 29) % 5 == 0 ? index % 2 == 0 ? 1 : 2 : 0;
        }

        var loaded = LitematicLoader.TryLoad(
            CoreFixtureBuilder.BuildLitematic(
                "cross",
                Vector3I.Zero,
                new Vector3I(SizeX, SizeY, SizeZ),
                [AirName, StoneName, DirtName],
                [.. indices]),
            "fixture://pick-cross-check");
        if (!RequireSuccess(loaded, "pick cross check")) return;

        var document = loaded.Document!;

        // 瞄的是某一格的中心，不是盒子中心。盒子中心在整数坐标上，那是八格共用的顶点，
        // 每条射线都会从一个格子的角上掠过——掠出来的碎片格宽度只有万分之一，
        // 等步长采样永远采不到，于是每条射线都报一次"两边不一致"。
        Vector3 center = new(SizeX * 0.5f + 0.5f, SizeY * 0.5f + 0.5f, SizeZ * 0.5f + 0.5f);
        var diagonal = new Vector3(SizeX, SizeY, SizeZ).Length();

        // 起点在盒子外一个半对角线处，射程留到三个对角线：射线必须在盒子的两侧都有余量。
        // 一格被完整穿过时至少占一个单位的参数长度（要离开一格必须整面跨出去），
        // 所以 0.01 的采样步长绝不会漏掉完整的一格；只有被 maxDistance 截断的那一格
        // 才会短于一步。射程正好切在盒子中间时，等步长采样漏掉末尾那格，
        // 看上去就像拾取凭空多命中了一个方块。
        var startRadius = diagonal * 1.5f;
        var maxDistance = diagonal * 3f;
        const float Step = 0.01f;

        Random random = new(20260925);
        var hits = 0;
        var ahead = 0;
        var mismatches = 0;

        for (var i = 0; i < 128; i++)
        {
            // 起点随机但方向一律指向盒心，保证每条射线都真的穿过这堆方块，
            // 不然大半条射线打空，"零命中"这种错误反而看不出来。
            var from = center + RandomDirection(random) * startRadius;
            var direction = Vector3.Normalize(center - from);

            var ddaHit = VoxelPicker.TryPick(document, from, direction, out var pick, maxDistance);
            var referenceHit = PickingReference.TryMarch(
                document, from, direction, maxDistance, Step, out var block, out var distance);

            // 基准在更近的地方命中了实心块，而拾取没命中、或者报了个更远的格子。
            // 这一条是"整格跳过去"那类错误唯一的表现形式，不放过。
            if (referenceHit && (!ddaHit || pick.Distance > distance + Step * 2f))
            {
                mismatches++;
                Debug.WriteLine(
                    $"[CORE][pick.cross.FAIL] i={i} 基准更近 dda={(ddaHit ? $"{pick.BlockPosition}@{pick.Distance}" : "miss")} " +
                    $"reference={block}@{distance} from={from} dir={direction}");
                continue;
            }

            if (!ddaHit) continue;

            hits++;

            // 拾取自己说命中了这一格，就用射线本身去验：报出来的格子必须真的含有
            // 射线上的那个点。跳格、算错下标、报错区域，都会在这里露出来。
            if (!CellHoldsPoint(pick, from, direction, out var pointCell, out var point))
            {
                mismatches++;
                Debug.WriteLine(
                    $"[CORE][pick.cross.FAIL] i={i} 报出的格子不在射线上 block={pick.BlockPosition}@{pick.Distance} " +
                    $"pointCell={pointCell} point={point} from={from} dir={direction}");
                continue;
            }

            if (pick.BlockPosition != block || MathF.Abs(pick.Distance - distance) > Step * 2f)
                // 拾取比基准更早命中，且它的结果自洽。基准漏掉的那一格必然是被盒子边界
                // 或者格子尖角截出来的碎片——完整的一格它漏不掉。
                ahead++;
        }

        Check(mismatches == 0,
            $"pick.cross rays=128 mismatches={mismatches} ahead={ahead} hits={hits} misses={128 - hits}");
        Check(hits > 32, $"pick.cross hits={hits} expected=>32");
    }

    // 真实文件上只做一件必然成立的事：从一个已知存在的方块中心横向打进去。
    // 命中的不一定就是瞄准的那一格（前面可能有别的方块挡着），但必须是同一区域里
    // 一个真实的非空气方块，且与对拍基准给出同一格。
    private static void CheckPickingRealFile(LitematicDocument document)
    {
        LitematicRegion? region = null;
        var aimIndex = -1;

        foreach (var candidate in document.Regions)
        {
            for (var i = 0; i < candidate.BlockIndices.Length; i++)
            {
                var paletteIndex = candidate.BlockIndices[i];
                if ((uint)paletteIndex < (uint)candidate.Palette.Length && !candidate.Palette[paletteIndex].IsAir)
                {
                    region = candidate;
                    aimIndex = i;
                    break;
                }
            }

            if (region is not null) break;
        }

        if (region is null || aimIndex < 0)
        {
            Check(false, $"pick.real region='{(region is null ? "<none>" : region.Name)}' 找不到任何非空气方块");
            return;
        }

        var aim = region.Bounds.Min + region.ToLocal(aimIndex);
        Vector3 origin = new(region.Bounds.Min.X - 1f, aim.Y + 0.5f, aim.Z + 0.5f);
        Vector3 direction = new(1f, 0f, 0f);
        var maxDistance = region.Bounds.Max.X - region.Bounds.Min.X + 4f;

        var hit = VoxelPicker.TryPick(document, origin, direction, out var pick, maxDistance);
        Check(hit, $"pick.real region='{region.Name}' aim={aim} hit={hit} expected=True");
        if (!hit) return;

        Check(!pick.State.IsAir, $"pick.real block={pick.BlockPosition} state={pick.State} expected=非空气");
        Check(region.Bounds.Contains(pick.BlockPosition),
            $"pick.real block={pick.BlockPosition} bounds={region.Bounds} expected=在区域包围盒内");
        Check((uint)pick.PaletteIndex < (uint)pick.Region.Palette.Length,
            $"pick.real paletteIndex={pick.PaletteIndex} paletteSize={pick.Region.Palette.Length} expected=在调色板内");

        // 这条射线是轴对齐且穿过格子中心的，两者没有掠射的余地，可以直接要求逐项相等。
        var referenceHit = PickingReference.TryMarch(
            document, origin, direction, maxDistance, 0.01f, out var block, out var refDistance);
        Check(referenceHit && block == pick.BlockPosition,
            $"pick.real cross dda={pick.BlockPosition}@{pick.Distance} reference={block}@{refDistance} expected=同一格");

        Debug.WriteLine(
            $"[CORE][pick.real] region='{region.Name}' aim={aim} block={pick.BlockPosition} " +
            $"face={pick.Face} state={pick.State}");
    }

    private static void CheckRealFile(string path)
    {
        var result = LitematicLoader.TryLoadFile(path);
        if (!RequireSuccess(result, $"real file '{path}'")) return;

        var document = result.Document!;
        var metadata = document.Metadata;

        if (metadata.RegionCount != 0)
            Check(document.Regions.Length == metadata.RegionCount,
                $"regions={document.Regions.Length} declared={metadata.RegionCount} expected=equal");

        if (metadata.EnclosingSize != Vector3I.Zero)
            Check(document.Bounds.Size == metadata.EnclosingSize,
                $"bounds={document.Bounds.Size} declaredEnclosing={metadata.EnclosingSize} expected=equal");

        if (metadata.TotalVolume != 0)
            Check(document.TotalVolume == metadata.TotalVolume,
                $"volume={document.TotalVolume} declaredVolume={metadata.TotalVolume} expected=equal");

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
            $"declaredBytesMismatch={document.TotalBlocks != metadata.TotalBlocks}");

        foreach (var issue in result.Issues)
        {
            Console.WriteLine($"    issue: {issue}");
            Debug.WriteLine($"[CORE][smoke.issue] {issue}");
        }

        CheckPickingRealFile(document);
    }

    private static void ExpectFailure(string what, byte[]? bytes, LoadErrorKind expected)
    {
        var result = bytes is null
            ? LitematicLoader.TryLoadFile(
                Path.Combine(Path.GetTempPath(), "litematica-viewer-does-not-exist.litematic"))
            : LitematicLoader.TryLoad(bytes, $"fixture://{what}");

        Check(!result.Success, $"errorInput '{what}' success={result.Success} expected=False");
        Check(result.Error == expected,
            $"errorInput '{what}' kind={result.Error} expected={expected} message={result.Message}");
        Check(result.Document is null, $"errorInput '{what}' document is null expected=True");
    }

    private static bool RequireSuccess(LoadResult result, string what)
    {
        if (result.Success) return true;

        Check(false, $"{what}: load failed kind={result.Error} message={result.Message}");
        return false;
    }

    // 射线上的那个点是否真的落在报出来的那一格里。
    //
    // 不能只往射线前方取样：命中点必然压在格子的边界上，而 Distance 是 entry 加上
    // 一串 tDelta 累加出来的，在这种量级下浮点漂移实测有 1e-4，重算出来的点会落在
    // 边界的任意一侧。所以取前后各 1e-3 的窗口。窗口能放这么大是因为一格被完整
    // 穿过时长度至少是 1：相邻的格子离 t 至少差一个单位，裹不进来。
    //
    // 拾取报出来的"是哪一格"来自整数步进，不受这个漂移影响，只有 Distance 受影响。
    private static bool CellHoldsPoint(VoxelHit hit, Vector3 origin, Vector3 direction, out Vector3I pointCell,
        out Vector3 point)
    {
        const float Window = 1e-3f;

        var dir = Vector3.Normalize(direction);
        point = origin + dir * hit.Distance;
        pointCell = CellOf(point);

        return !hit.State.IsAir &&
               (hit.BlockPosition == pointCell ||
                hit.BlockPosition == CellOf(point - dir * Window) ||
                hit.BlockPosition == CellOf(point + dir * Window));
    }

    private static Vector3I CellOf(Vector3 point)
    {
        return new Vector3I(
            (int)MathF.Floor(point.X),
            (int)MathF.Floor(point.Y),
            (int)MathF.Floor(point.Z));
    }

    // 球面上均匀取向。取值域故意避开接近轴对齐的方向：轴对齐射线在格子边界上是
    // 处在一种约定里的特例，对拍双方可以各按自己的约定挑到相邻的一格，两边都不算错。
    private static Vector3 RandomDirection(Random random)
    {
        while (true)
        {
            Vector3 candidate = new(
                (float)(random.NextDouble() * 2d - 1d),
                (float)(random.NextDouble() * 2d - 1d),
                (float)(random.NextDouble() * 2d - 1d));
            var lengthSquared = candidate.LengthSquared();
            if (lengthSquared is > 0.01f and <= 1f) return candidate / MathF.Sqrt(lengthSquared);
        }
    }

    private static int CountMismatches(ImmutableArray<int> expected, ImmutableArray<int> actual)
    {
        var count = 0;
        for (var i = 0; i < expected.Length && i < actual.Length; i++)
            if (expected[i] != actual[i])
                count++;

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
