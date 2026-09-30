using System.Collections.Immutable;
using LitematicaViewer.Core.Model;
using Poly.NBT.Dom;

namespace LitematicaViewer.Core.Tests;

// 夹具里一个区域的全部输入。Indices 是区域存储顺序（x 最快、然后 z、最后 y）下的调色板下标。
public sealed record RegionSpec(
    string Name,
    Vector3I Position,
    Vector3I Size,
    ImmutableArray<string> PaletteNames,
    ImmutableArray<int> Indices)
{
    public ImmutableArray<NbtElement> TileEntities { get; init; } = [];
    public ImmutableArray<NbtElement> Entities { get; init; } = [];
}
