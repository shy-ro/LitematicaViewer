using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using LitematicaViewer.Core.Io;
using LitematicaViewer.Core.Model;
using Poly.NBT;
using Poly.NBT.Dom;

namespace LitematicaViewer.Core.Parsing;

public static class LitematicParser
{
    // NbtSerializer 未声明线程安全性，这里只在单线程加载路径上复用同一个实例，
    // 省掉每个文件一次的 Create 开销。R3 禁止 lock，所以不要给它加锁；
    // 真要并发加载，调用方各自建实例，不要共享这个字段。
    private static readonly NbtSerializer Serializer = NbtSerializer.Create(NbtOptions.JavaEdition);

    // 走 DOM 逐字段取值，不走 [GenerateShape] 的类型化反序列化。
    // 真实 .litematic 的可选字段缺失很常见，而类型化路径把缺席的构造参数当作必填，
    // 直接抛 InvalidDataException: Required constructor arguments are missing。
    public static LitematicParseResult ParseRaw(byte[] bytes)
    {
        NbtContainer container = NbtContainerReader.Read(bytes);
        NbtDocument document = Serializer.DeserializeDocument(container.Payload);
        if (document.RootElement is not NbtCompound root)
        {
            throw new LitematicFormatException(
                LoadErrorKind.NotNbt,
                $"root element is {document.RootElement?.GetType().Name ?? "null"}, expected a compound");
        }

        // 根 tag 的名字是空字符串（实测这个语料全如此），不是 "Schematic"。
        // 所以不能拿 RootTagName 判类型，只能靠 Regions 这类结构字段。
        Debug.WriteLine(
            $"[CORE][parse.root] rootTagName='{document.RootTagName}' " +
            $"keys=[{string.Join(",", root.Keys)}] expected=Compound");

        if (!root.TryGetValue("Regions", out NbtElement? regionsElement) || regionsElement is not NbtCompound regionNodes)
        {
            throw new LitematicFormatException(
                LoadErrorKind.Malformed,
                "missing 'Regions' compound; the file is NBT but not a litematic");
        }

        LitematicMetadata metadata = ReadMetadata(root);
        Debug.WriteLine(
            $"[CORE][parse.metadata] version={metadata.Version} subVersion={metadata.SubVersion} " +
            $"dataVersion={metadata.MinecraftDataVersion} name='{metadata.Name}' software='{metadata.Software}'");
        Debug.WriteLine(
            $"[CORE][parse.metadata] declaredRegions={metadata.RegionCount} declaredBlocks={metadata.TotalBlocks} " +
            $"declaredVolume={metadata.TotalVolume} enclosing={metadata.EnclosingSize}");

        ImmutableArray<string>.Builder issues = ImmutableArray.CreateBuilder<string>();
        ImmutableArray<RawRegion>.Builder regions = ImmutableArray.CreateBuilder<RawRegion>(regionNodes.Count);
        foreach (KeyValuePair<string, NbtElement> pair in regionNodes)
        {
            if (pair.Value is not NbtCompound node)
            {
                issues.Add($"region '{pair.Key}': value is {pair.Value?.GetType().Name ?? "null"}, expected a compound; skipped");
                continue;
            }

            RawRegion? region = ReadRegion(pair.Key, node, issues);
            if (region is not null)
            {
                regions.Add(region);
            }
        }

        return new LitematicParseResult(metadata, regions.ToImmutable(), issues.ToImmutable());
    }

    public static LitematicDocument ToDomain(LitematicParseResult result, string sourcePath)
    {
        ImmutableArray<LitematicRegion> regions =
        [
            .. result.Regions.Select(static raw => new LitematicRegion(
                raw.Name,
                raw.Position,
                raw.Size,
                IntBounds.FromPositionSize(raw.Position, raw.Size),
                raw.Palette,
                raw.BlockIndices)),
        ];

        // 空 Regions 时不能走 Enclose：default(IntBounds) 的 Min 与 Max 都是原点，
        // 会算出 Size=(1,1,1)、Volume=1 这种看着像真的假数据。
        IntBounds bounds = regions.IsEmpty ? default : IntBounds.Enclose(regions.Select(static r => r.Bounds));
        long totalBlocks = LitematicDocument.CountNonAirBlocks(regions);

        foreach (LitematicRegion region in regions)
        {
            Debug.WriteLine(
                $"[CORE][document.region] name='{region.Name}' position={region.Position} size={region.Size} " +
                $"bounds={region.Bounds} volume={region.Volume} palette={region.Palette.Length} " +
                $"indices={region.BlockIndices.Length} nonAir={region.CountNonAirBlocks()} " +
                $"outOfPalette={region.CountOutOfPaletteIndices()} expected=0");
        }

        Debug.WriteLine(
            $"[CORE][document] sourcePath='{sourcePath}' regions={regions.Length} " +
            $"declaredRegions={result.Metadata.RegionCount} expected=equal");
        Debug.WriteLine(
            $"[CORE][document] bounds={bounds} volume={bounds.Volume} " +
            $"declaredEnclosing={result.Metadata.EnclosingSize} declaredVolume={result.Metadata.TotalVolume}");
        Debug.WriteLine(
            $"[CORE][document] totalBlocks={totalBlocks} declaredTotalBlocks={result.Metadata.TotalBlocks} " +
            $"expected=equal (不等时以本值为准，文件头记的是保存瞬间的世界统计)");

        return new LitematicDocument(sourcePath, result.Metadata, regions, bounds, totalBlocks);
    }

    private static RawRegion? ReadRegion(string name, NbtCompound node, ImmutableArray<string>.Builder issues)
    {
        if (!TryReadVector(node, "Position", out Vector3I position) ||
            !TryReadVector(node, "Size", out Vector3I size))
        {
            issues.Add($"region '{name}': missing or incomplete Position/Size; skipped");
            return null;
        }

        ImmutableArray<BlockStateDefinition> palette = ReadPalette(node);
        if (palette.IsEmpty)
        {
            // 补一个 air，保住"Palette 至少一项"这个不变量：GetState 的越界回退依赖它。
            palette = [new BlockStateDefinition("minecraft:air", BlockStateDefinition.NoProperties)];
            issues.Add($"region '{name}': empty BlockStatePalette, treated as a single air state");
        }

        IntBounds bounds = IntBounds.FromPositionSize(position, size);
        if (bounds.Volume > int.MaxValue)
        {
            issues.Add($"region '{name}': volume {bounds.Volume} exceeds the indexable range; skipped");
            return null;
        }

        int volume = (int)bounds.Volume;
        long[] packed = ReadLongArray(node, "BlockStates");
        int bits = BlockStatesCodec.GetBitsPerBlock(palette.Length);
        int required = BlockStatesCodec.GetPackedLongCount(volume, palette.Length);
        if (packed.Length < required)
        {
            issues.Add(
                $"region '{name}': BlockStates holds {packed.Length} longs, {required} required " +
                $"for {volume} blocks at {bits} bits; the missing tail reads as palette[0]");
        }

        ImmutableArray<int> indices = BlockStatesCodec.Unpack(packed, volume, palette.Length);

        Debug.WriteLine(
            $"[CORE][parse.region] name='{name}' position={position} size={size} absSize={size.Abs()} " +
            $"bounds={bounds} volume={volume} palette={palette.Length} bits={bits} " +
            $"longs={packed.Length} requiredLongs={required} paletteHead='{palette[0]}'");
        Debug.WriteLine(
            $"[CORE][parse.region] name='{name}' entities={CountListItems(node, "Entities")} " +
            $"tileEntities={CountListItems(node, "TileEntities")} " +
            $"pendingBlockTicks={CountListItems(node, "PendingBlockTicks")} " +
            $"pendingFluidTicks={CountListItems(node, "PendingFluidTicks")} " +
            $"note=本阶段不解，只确认它们存在且不被当成未知字段");

        return new RawRegion(name, position, size, palette, indices);
    }

    private static LitematicMetadata ReadMetadata(NbtCompound root)
    {
        NbtCompound? meta = root.TryGetValue("Metadata", out NbtElement? element) && element is NbtCompound found
            ? found
            : null;

        TryReadVector(meta, "EnclosingSize", out Vector3I enclosingSize);

        return new LitematicMetadata(
            Version: ReadInt(root, "Version", 0),
            SubVersion: ReadInt(root, "SubVersion", 0),
            MinecraftDataVersion: ReadInt(root, "MinecraftDataVersion", 0),
            Name: ReadString(meta, "Name", string.Empty),
            Author: ReadString(meta, "Author", string.Empty),
            Description: ReadString(meta, "Description", string.Empty),
            Software: ReadString(meta, "Software", string.Empty),
            RegionCount: ReadInt(meta, "RegionCount", 0),
            TotalBlocks: ReadLong(meta, "TotalBlocks", 0),
            TotalVolume: ReadLong(meta, "TotalVolume", 0),
            EnclosingSize: enclosingSize,
            TimeCreated: ReadLong(meta, "TimeCreated", 0),
            TimeModified: ReadLong(meta, "TimeModified", 0));
    }

    private static ImmutableArray<BlockStateDefinition> ReadPalette(NbtCompound node)
    {
        if (!node.TryGetValue("BlockStatePalette", out NbtElement? element) ||
            element is not NbtList list ||
            list.Count == 0)
        {
            return [];
        }

        ImmutableArray<BlockStateDefinition>.Builder builder = ImmutableArray.CreateBuilder<BlockStateDefinition>(list.Count);
        foreach (NbtElement item in list)
        {
            if (item is not NbtCompound entry)
            {
                // 调色板里出现非 Compound 说明结构已损坏。此处用 air 占位而不是跳过，
                // 因为调色板下标直接写进 BlockIndices，少一项会让后面所有方块整体错位。
                builder.Add(new BlockStateDefinition("minecraft:air", BlockStateDefinition.NoProperties));
                continue;
            }

            builder.Add(new BlockStateDefinition(ReadString(entry, "Name", "minecraft:air"), ReadProperties(entry)));
        }

        return builder.MoveToImmutable();
    }

    private static ImmutableDictionary<string, string> ReadProperties(NbtCompound entry)
    {
        if (!entry.TryGetValue("Properties", out NbtElement? element) ||
            element is not NbtCompound properties ||
            properties.Count == 0)
        {
            return BlockStateDefinition.NoProperties;
        }

        ImmutableDictionary<string, string>.Builder builder =
            ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, NbtElement> pair in properties)
        {
            // 属性值在 NBT 里未必是字符串（snowy 可能是 Byte，level 可能是 Int），
            // 一律转成字符串保存，因为下游只做相等匹配，不做算术。
            builder[pair.Key] = pair.Value switch
            {
                NbtString s => s.Value,
                NbtByte b => b.Value.ToString(CultureInfo.InvariantCulture),
                NbtShort sh => sh.Value.ToString(CultureInfo.InvariantCulture),
                NbtInt i => i.Value.ToString(CultureInfo.InvariantCulture),
                NbtLong l => l.Value.ToString(CultureInfo.InvariantCulture),
                _ => pair.Value.ToString() ?? string.Empty,
            };
        }

        return builder.ToImmutable();
    }

    private static long[] ReadLongArray(NbtCompound node, string key)
    {
        if (!node.TryGetValue(key, out NbtElement? element))
        {
            return [];
        }

        // BlockStates 在磁盘上是 TAG_Long_Array，但 NbtOptions.JavaEdition 打开了
        // OptimizePrimitiveListsToArrays，同构的 TAG_List<TAG_Long> 也会被读成 NbtLongArray。
        // 两种形态都可能落到这里，都认。
        return element switch
        {
            NbtLongArray array => array.Value,
            NbtList list when list.TryToArray(out long[]? values) => values,
            _ => [],
        };
    }

    private static int CountListItems(NbtCompound node, string key) =>
        node.TryGetValue(key, out NbtElement? element) && element is NbtList list ? list.Count : 0;

    private static bool TryReadVector(NbtCompound? node, string key, out Vector3I value)
    {
        value = Vector3I.Zero;
        if (node is null ||
            !node.TryGetValue(key, out NbtElement? element) ||
            element is not NbtCompound compound ||
            !compound.ContainsKey("x") ||
            !compound.ContainsKey("y") ||
            !compound.ContainsKey("z"))
        {
            // 缺分量时返回 false 而不是把缺的补 0：补 0 会把一个坏区域变成一个
            // "位置看着正常但整个位移了"的区域，比直接跳过难查得多。
            return false;
        }

        value = new Vector3I(ReadInt(compound, "x", 0), ReadInt(compound, "y", 0), ReadInt(compound, "z", 0));
        return true;
    }

    private static int ReadInt(NbtCompound? node, string key, int fallback) =>
        node is not null && node.TryGetValue(key, out NbtElement? element) && element is NbtInt value
            ? value.Value
            : fallback;

    private static long ReadLong(NbtCompound? node, string key, long fallback) =>
        node is not null && node.TryGetValue(key, out NbtElement? element)
            ? element switch
            {
                NbtLong l => l.Value,
                NbtInt i => i.Value,
                _ => fallback,
            }
            : fallback;

    private static string ReadString(NbtCompound? node, string key, string fallback) =>
        node is not null && node.TryGetValue(key, out NbtElement? element) && element is NbtString value
            ? value.Value
            : fallback;
}
