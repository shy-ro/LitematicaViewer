namespace LitematicaViewer.Core.Picking;

// 顺序照抄 Minecraft 的 Direction（DOWN/UP/NORTH/SOUTH/WEST/EAST），整体后移一位给 None 让位。
// 将来要按面取方块模型的贴图时可以直接用 (byte)Face - 1 当下标，不必再写一张映射表，
// 也就不会出现"表里忘了一个方向"这种只在某些朝向下才看得到的错。
public enum VoxelFace : byte
{
    // 射线起点在方块内部，没有穿过任何一个面。也用作"没有命中"的默认值。
    None = 0,

    Down = 1,
    Up = 2,
    North = 3,
    South = 4,
    West = 5,
    East = 6
}
