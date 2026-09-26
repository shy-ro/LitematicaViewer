using LitematicaViewer.Core.Model;

namespace LitematicaViewer.Core.Picking;

public static class VoxelFaces
{
    // North 是 -Z，与 Minecraft 一致，与"屏幕往里是北"的直觉相反。
    // 拿错的话症状是贴图朝向前后颠倒，而且只在有朝向的方块上才看得出来。
    public static Vector3I NormalOf(VoxelFace face)
    {
        return face switch
        {
            VoxelFace.Down => new Vector3I(0, -1, 0),
            VoxelFace.Up => new Vector3I(0, 1, 0),
            VoxelFace.North => new Vector3I(0, 0, -1),
            VoxelFace.South => new Vector3I(0, 0, 1),
            VoxelFace.West => new Vector3I(-1, 0, 0),
            VoxelFace.East => new Vector3I(1, 0, 0),
            _ => Vector3I.Zero
        };
    }

    // axis：0=x 1=y 2=z；sign 是面朝向，+1 表示位于该轴的正方向侧。
    public static VoxelFace FromAxis(int axis, int sign)
    {
        return (axis, sign) switch
        {
            (0, 1) => VoxelFace.East,
            (0, -1) => VoxelFace.West,
            (1, 1) => VoxelFace.Up,
            (1, -1) => VoxelFace.Down,
            (2, 1) => VoxelFace.South,
            (2, -1) => VoxelFace.North,
            _ => VoxelFace.None
        };
    }
}
