using LitematicaViewer.Core.Model;

namespace LitematicaViewer.Core.Picking;

// 带上 Region 引用而不是只回坐标：调用方接着要问的多半是"这是什么方块"，
// 而坐标换算依赖区域的 Bounds。只给坐标等于逼调用方自己再找一遍区域，
// 多区域相邻时还可能找错那一个。
public readonly record struct VoxelHit(
    LitematicRegion Region,
    Vector3I BlockPosition,
    VoxelFace Face,
    float Distance,
    int PaletteIndex,
    BlockStateDefinition State)
{
    // 命中的是 BlockPosition 朝向 Face 的那一面，所以贴着这一面往外退一格就是
    // 相邻的方块。Face 为 None（起点在方块内部）时退化成自身。
    public Vector3I AdjacentPosition => BlockPosition + VoxelFaces.NormalOf(Face);
}
