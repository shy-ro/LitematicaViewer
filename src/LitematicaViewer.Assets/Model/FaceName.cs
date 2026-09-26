using System.Numerics;

namespace LitematicaViewer.Assets.Model;

// 与 MC Direction 同序：Down, Up, North(-Z), South(+Z), West(-X), East(+X)。
// 这里定义自己的枚举而不是复用 Core 的 VoxelFace：Assets 不知道 Core 存在，
// 顺序映射发生在网格阶段。
public enum FaceName
{
    Down,
    Up,
    North,
    South,
    West,
    East,
}

// 元素级旋转。MC 的 angle 只允许 ±22.5/±45 的倍数；axis 0/1/2 = x/y/z。
public readonly record struct ElementRotation(int Axis, float AngleDegrees, Vector3 Origin);

// 一个 model element：几何体（模型空间，单位 1/16 方块）加它自己的各面。
// 面列表与 JsonElement 里 faces 的键一一对应，不保证六个方向齐全。
public sealed record ModelElement(
    Vector3 From,
    Vector3 To,
    ElementRotation? Rotation,
    IReadOnlyList<ElementFace> Faces);

// element 里的一个面。Uv 是 0..16 的贴图坐标（MC 资产约定），Sprite 是归一化后的
// "namespace:path"——#引用已在解析时解开，这里拿到的一定是最终贴图名。
public sealed record ElementFace(
    FaceName Face,
    Vector4 Uv,
    string Sprite,
    string? Cullface,
    int TintIndex,
    int UvRotationDegrees);

// 一次命中（variant 或 multipart apply）的最终结果：模型 + 整体旋转。
// XDegrees/YDegrees 来自 blockstate 的 variant 字段，mesh 阶段把它们变成矩阵。
public sealed record ResolvedVariant(string ModelId, ResolvedBlockModel Model, float XDegrees, float YDegrees);

public sealed record ResolvedBlockModel(string ModelId, IReadOnlyList<ModelElement> Elements);

public sealed record ResolvedBlockState(string BlockId, IReadOnlyList<ResolvedVariant> Variants);
