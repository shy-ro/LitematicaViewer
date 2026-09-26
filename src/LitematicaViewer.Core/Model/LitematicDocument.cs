using System.Collections.Immutable;

namespace LitematicaViewer.Core.Model;

public sealed record LitematicDocument(
    string SourcePath,
    LitematicMetadata Metadata,
    ImmutableArray<LitematicRegion> Regions,
    IntBounds Bounds,
    long TotalBlocks)
{
    public long TotalVolume => Bounds.Volume;

    // 文件头也有一份 TotalBlocks，但它记的是保存瞬间世界的统计，与调色板重新数出来的
    // 非空气方块数并不总相等。对外只暴露这一个口径，差异放进调试探针里比对。
    public static long CountNonAirBlocks(ImmutableArray<LitematicRegion> regions)
    {
        long total = 0;
        foreach (var region in regions) total += region.CountNonAirBlocks();

        return total;
    }
}
