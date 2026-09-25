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
        Version: 0,
        SubVersion: 0,
        MinecraftDataVersion: 0,
        Name: string.Empty,
        Author: string.Empty,
        Description: string.Empty,
        Software: string.Empty,
        RegionCount: 0,
        TotalBlocks: 0,
        TotalVolume: 0,
        EnclosingSize: Vector3I.Zero,
        TimeCreated: 0,
        TimeModified: 0);
}
