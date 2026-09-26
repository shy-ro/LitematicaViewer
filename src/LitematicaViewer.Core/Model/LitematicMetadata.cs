namespace LitematicaViewer.Core.Model;

public sealed record LitematicMetadata(
    int Version,
    int SubVersion,
    int MinecraftDataVersion,
    string Name,
    string Author,
    string Description,
    string Software,
    int RegionCount,
    long TotalBlocks,
    long TotalVolume,
    Vector3I EnclosingSize,
    long TimeCreated,
    long TimeModified)
{
    public static LitematicMetadata Empty { get; } = new(
        0,
        0,
        0,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        0,
        0,
        0,
        Vector3I.Zero,
        0,
        0);
}
