using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using LitematicaViewer.Core.Model;
using LitematicaViewer.Core.Parsing;
using Poly.NBT;
using Poly.NBT.Dom;

namespace LitematicaViewer.Core.Tests;

// 手搭 DOM 再序列化出夹具，而不是拿解析器的输出当基准。
// 用同一个实现既写又读，双方的同一个误解会互相确认，夹具就永远测不出问题。
public static class CoreFixtureBuilder
{
    private static readonly NbtSerializer Serializer = NbtSerializer.Create(NbtOptions.JavaEdition);

    // 根 tag 名用空字符串，与真实文件一致；用 "Schematic" 会让夹具在
    // 根名相关的判断上给出一个真实世界不存在的分支。
    public static byte[] BuildLitematic(
        string regionName,
        Vector3I position,
        Vector3I size,
        ImmutableArray<string> paletteNames,
        ImmutableArray<int> indices,
        bool compress = true)
    {
        IntBounds bounds = IntBounds.FromPositionSize(position, size);

        // 索引数与体积不匹配时夹具自身就是坏的，而症状会伪装成解析器算错了长度。
        // 在这里炸掉，别让一个写错的夹具去冤枉被测代码。
        Debug.Assert(
            indices.Length == bounds.Volume,
            $"fixture indices={indices.Length} but volume={bounds.Volume}");

        ImmutableArray<long> packed =
            [.. BlockStatesCodec.Pack(indices.AsSpan(), paletteNames.Length)];

        ImmutableArray<BlockStateDefinition> palette =
            [.. paletteNames.Select(static n => new BlockStateDefinition(n, BlockStateDefinition.NoProperties))];

        long nonAir = 0;
        foreach (int index in indices)
        {
            if ((uint)index < (uint)palette.Length && !palette[index].IsAir)
            {
                nonAir++;
            }
        }

        NbtCompound metadata = Compound(
            ("EnclosingSize", Vec(bounds.Size)),
            ("Author", new NbtString("fixture")),
            ("Description", new NbtString(string.Empty)),
            ("Name", new NbtString("fixture")),
            ("Software", new NbtString("LitematicaViewer.Tests")),
            ("RegionCount", new NbtInt(1)),
            ("TimeCreated", new NbtLong(0)),
            ("TimeModified", new NbtLong(0)),
            ("TotalBlocks", new NbtLong(nonAir)),
            ("TotalVolume", new NbtLong(bounds.Volume)),
            ("PreviewImageData", new NbtIntArray([])));

        NbtCompound region = Compound(
            ("Position", Vec(position)),
            ("Size", Vec(size)),
            ("BlockStatePalette", new NbtList([.. palette.Select(static p => (NbtElement)Compound(
                ("Name", new NbtString(p.Name))))])),
            ("Entities", new NbtList()),
            ("TileEntities", new NbtList()),
            ("PendingBlockTicks", new NbtList()),
            ("PendingFluidTicks", new NbtList()),
            ("BlockStates", new NbtLongArray([.. packed])));

        NbtCompound root = Compound(
            ("Version", new NbtInt(6)),
            ("SubVersion", new NbtInt(1)),
            ("MinecraftDataVersion", new NbtInt(3465)),
            ("Metadata", metadata),
            ("Regions", Compound((regionName, region))));

        byte[] raw = Serializer.Serialize(new NbtDocument(string.Empty, root));
        return compress ? Gzip(raw) : raw;
    }

    public static byte[] BuildNbtWithoutRegions()
    {
        NbtCompound root = Compound(("Version", new NbtInt(6)));
        return Gzip(Serializer.Serialize(new NbtDocument(string.Empty, root)));
    }

    public static byte[] BuildPlainTextGzipped() =>
        Gzip(Encoding.UTF8.GetBytes("this is definitely not an nbt document"));

    public static byte[] Gzip(byte[] raw)
    {
        using MemoryStream output = new();
        using (GZipStream gzip = new(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(raw);
        }

        return output.ToArray();
    }

    public static byte[] Truncate(byte[] bytes, int keep)
    {
        byte[] result = new byte[keep];
        Array.Copy(bytes, result, keep);
        return result;
    }

    private static NbtCompound Vec(Vector3I v) =>
        Compound(("x", new NbtInt(v.X)), ("y", new NbtInt(v.Y)), ("z", new NbtInt(v.Z)));

    // NbtCompound 没有无参构造、也没有索引器 setter，只能用 pair 序列构造，
    // 集合初始化语法在这个类型上不成立。
    private static NbtCompound Compound(params (string Key, NbtElement Value)[] entries)
    {
        List<KeyValuePair<string, NbtElement>> pairs = new(entries.Length);
        foreach ((string key, NbtElement value) in entries)
        {
            pairs.Add(new KeyValuePair<string, NbtElement>(key, value));
        }

        return new NbtCompound(pairs);
    }
}
