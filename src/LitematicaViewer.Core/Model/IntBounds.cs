namespace LitematicaViewer.Core.Model;

// Max 是闭区间端点，不是排他边界。litematic 的 Region.Size 是"两个角之差"，
// 可以为负，所以从 Position/Size 建包围盒时必须先归一化，不能直接 Position + Size - 1。
public readonly record struct IntBounds(Vector3I Min, Vector3I Max)
{
    public Vector3I Size => new(Max.X - Min.X + 1, Max.Y - Min.Y + 1, Max.Z - Min.Z + 1);

    public long Volume => Size.ProductOfComponents;

    public static IntBounds FromPositionSize(Vector3I position, Vector3I size)
    {
        Vector3I far = position + size;
        Vector3I min = Vector3I.ComponentMin(position, far);
        return new IntBounds(min, min + size.Abs() - Vector3I.One);
    }

    public static IntBounds Enclose(IEnumerable<IntBounds> bounds)
    {
        IntBounds result = default;
        bool any = false;
        foreach (IntBounds b in bounds)
        {
            result = any ? result.Union(b) : b;
            any = true;
        }

        return result;
    }

    public IntBounds Union(IntBounds other) =>
        new(Vector3I.ComponentMin(Min, other.Min), Vector3I.ComponentMax(Max, other.Max));

    public bool Contains(Vector3I point) =>
        point.X >= Min.X && point.X <= Max.X &&
        point.Y >= Min.Y && point.Y <= Max.Y &&
        point.Z >= Min.Z && point.Z <= Max.Z;

    public override string ToString() => $"{Min}..{Max} size={Size}";
}
