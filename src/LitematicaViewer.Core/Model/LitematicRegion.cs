using System.Collections.Immutable;

namespace LitematicaViewer.Core.Model;

// Palette 保证非空：解析器在调色板缺失时补一个 air 进去。
// 否则 GetState 的越界回退无处可退，调用方会拿到 IndexOutOfRange 而不是一个方块。
public sealed record LitematicRegion(
    string Name,
    Vector3I Position,
    Vector3I Size,
    IntBounds Bounds,
    ImmutableArray<BlockStateDefinition> Palette,
    ImmutableArray<int> BlockIndices)
{
    public long Volume => Bounds.Volume;

    // 索引顺序 x 最快、然后 z、最后 y。Minecraft 在区域内的列式遍历就是这个顺序，
    // 中途改成行式遍历不会报错，只会让整个区域看起来被转置过。
    public int ToIndex(int x, int y, int z)
    {
        var size = Bounds.Size;
        return (y * size.Z + z) * size.X + x;
    }

    public Vector3I ToLocal(int index)
    {
        var size = Bounds.Size;
        return new Vector3I(index % size.X, index / (size.X * size.Z), index / size.X % size.Z);
    }

    public BlockStateDefinition GetState(Vector3I world)
    {
        var local = world - Bounds.Min;
        var size = Bounds.Size;
        if (local.X < 0 || local.Y < 0 || local.Z < 0 ||
            local.X >= size.X || local.Y >= size.Y || local.Z >= size.Z)
            // 区域外回退到 palette[0]。调用方要是把这里的越界当成了真实方块，
            // 症状是区域边界外长出一层薄壳，而不是异常。
            return Palette[0];

        return GetStateAt(ToIndex(local.X, local.Y, local.Z));
    }

    public BlockStateDefinition GetStateAt(int index)
    {
        if ((uint)index >= (uint)BlockIndices.Length) return Palette[0];

        var paletteIndex = BlockIndices[index];
        return (uint)paletteIndex < (uint)Palette.Length ? Palette[paletteIndex] : Palette[0];
    }

    public long CountNonAirBlocks()
    {
        var air = new bool[Palette.Length];
        for (var i = 0; i < air.Length; i++) air[i] = Palette[i].IsAir;

        long count = 0;
        foreach (var index in BlockIndices)
            if ((uint)index < (uint)air.Length && !air[index])
                count++;

        return count;
    }

    public int CountOutOfPaletteIndices()
    {
        var count = 0;
        foreach (var index in BlockIndices)
            if ((uint)index >= (uint)Palette.Length)
                count++;

        return count;
    }
}
