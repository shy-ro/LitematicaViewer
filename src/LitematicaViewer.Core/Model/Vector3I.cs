namespace LitematicaViewer.Core.Model;

// int 而不是 System.Numerics.Vector3：方块坐标是整数语义，用 float 会让
// region 偏移叠加后的相等性比较不可靠，包围盒的 Min/Max 也没法做精确的闭区间。
public readonly record struct Vector3I(int X, int Y, int Z)
{
    public static Vector3I Zero => new(0, 0, 0);
    public static Vector3I One => new(1, 1, 1);

    // 先转 long：区域边长相乘轻易越过 int，而且调用方拿到负数体积时
    // 往往是在几百行外才发现，不如在这里就堵死。
    public long ProductOfComponents => (long)X * Y * Z;

    public static Vector3I operator +(Vector3I left, Vector3I right)
    {
        return new Vector3I(left.X + right.X, left.Y + right.Y, left.Z + right.Z);
    }

    public static Vector3I operator -(Vector3I left, Vector3I right)
    {
        return new Vector3I(left.X - right.X, left.Y - right.Y, left.Z - right.Z);
    }

    public Vector3I Abs()
    {
        return new Vector3I(Math.Abs(X), Math.Abs(Y), Math.Abs(Z));
    }

    public static Vector3I ComponentMin(Vector3I left, Vector3I right)
    {
        return new Vector3I(Math.Min(left.X, right.X), Math.Min(left.Y, right.Y), Math.Min(left.Z, right.Z));
    }

    public static Vector3I ComponentMax(Vector3I left, Vector3I right)
    {
        return new Vector3I(Math.Max(left.X, right.X), Math.Max(left.Y, right.Y), Math.Max(left.Z, right.Z));
    }

    public override string ToString()
    {
        return $"({X},{Y},{Z})";
    }
}
