using System.Diagnostics;
using System.Numerics;

namespace LitematicaViewer.Previewer.Diagnostics;

// 坐标轴那三条线的不变量检查。和 DebugCube 分开是因为它们的判据完全不同：
// 立方体的检查要读回整张画面（几何是死的，画得对不对只有画面能说），
// 而这三个轴是「数据写对了就等于画对了」——它们没有隐藏面、没有法线、没有材质，
// 所以这里只查顶点数据，一次读回都不用。
internal static class DebugAxes
{
    public static void CheckGeometry(float[] vertices, int[] indices, int axisCount)
    {
        Debug.Assert(
            vertices.Length == axisCount * 2 * GlAxesRenderer.FloatsPerVertex,
            $"[PREVIEWER][gl.axes] 顶点数不对 floats={vertices.Length} " +
            $"expected={axisCount * 2 * GlAxesRenderer.FloatsPerVertex}");
        Debug.Assert(
            indices.Length == axisCount * 2,
            $"[PREVIEWER][gl.axes] 索引数不对 indices={indices.Length} expected={axisCount * 2}");

        HashSet<Vector3> directions = [];

        for (var axis = 0; axis < axisCount; axis++)
        {
            var (color, direction) = GlAxesRenderer.Axes[axis];
            var negative = ReadPosition(vertices, axis * 2 + 0);
            var positive = ReadPosition(vertices, axis * 2 + 1);

            // 一条轴线就是两个端点，方向相反、长度相等。写成「两个端点在各自的位置上」
            // 而不是去比数学关系：这里是数据检查，端点写错一个数值就该红。
            Debug.Assert(
                IsClose(negative, direction * -GlAxesRenderer.AxisLength),
                $"[PREVIEWER][gl.axes] 负端不在轴上 axis={axis} got=({negative}) " +
                $"expected=({direction * -GlAxesRenderer.AxisLength})");
            Debug.Assert(
                IsClose(positive, direction * GlAxesRenderer.AxisLength),
                $"[PREVIEWER][gl.axes] 正端不在轴上 axis={axis} got=({positive}) " +
                $"expected=({direction * GlAxesRenderer.AxisLength})");

            // 端点到原点的距离必须等于半长：写成固定坐标（比如某个端点是 (1,0,0)）时，
            // 三根轴的伸出长度就会各不相同，而那在画面上只是「看着有点歪」。
            for (var end = 0; end < 2; end++)
            {
                var position = ReadPosition(vertices, axis * 2 + end);
                Debug.Assert(
                    MathF.Abs(position.Length() - GlAxesRenderer.AxisLength) < 1e-6f,
                    $"[PREVIEWER][gl.axes] 端点到原点的距离不是半长 axis={axis} end={end} " +
                    $"position=({position}) length={position.Length()}");
            }

            // 颜色跟着顶点走，同一根的两次端必须同色，而且就是声明里那个颜色。
            // 颜色错的表现是「三根轴看不出是哪根」——不报错，只是没法用了。
            for (var end = 0; end < 2; end++)
            {
                var got = ReadColor(vertices, axis * 2 + end);
                Debug.Assert(
                    IsClose(got, color),
                    $"[PREVIEWER][gl.axes] 端点颜色不对 axis={axis} end={end} got=({got}) expected=({color})");
            }

            // 轴向必须只有一个分量非零：混进第二个分量时画出来的线是斜的，
            // 而斜着的线看起来仍然「像根轴」，只有跟网格对不上时才看得出来。
            var nonZero = 0;
            for (var component = 0; component < 3; component++)
                if (Component(direction, component) != 0f)
                    nonZero++;

            Debug.Assert(
                nonZero == 1 && MathF.Abs(Vector3.Dot(direction, direction) - 1f) < 1e-6f,
                $"[PREVIEWER][gl.axes] 轴向不是单位轴 axis={axis} direction=({direction})");

            directions.Add(direction);
        }

        // 三根轴必须各占一个方向。重复时画面上只有两根线，而那看起来像「少画了一根」，
        // 很容易被当成渲染问题去查。
        Debug.Assert(
            directions.Count == axisCount,
            $"[PREVIEWER][gl.axes] 轴向有重复 count={directions.Count} expected={axisCount}");

        // 半透明度是编译期常量，范围由它的声明保证，这里没有可断的东西。
        //
        // 真正需要盯住的是「着色器里那个字面量和这个常量是同一个数」——它跨不过去，
        // 编译期查不了。所以那一条由画面上验：DebugCube 按 Blend 算出来的混合色去认轴线像素，
        // 两个数一旦不一致，那些像素就认不出来，直接撞上「认不出的颜色 <1%」那条断言。
        // 换句话说，这里不必再写一条注定恒真的检查。
    }

    // 轴线压在某个底色上时的混合结果在 PixelBlend.Over：画面校验需要它这件事不止轴线一家
    // （展台光环也要），所以算式与那个理由一并挪到了那儿。

    private static Vector3 ReadPosition(float[] vertices, int vertex)
    {
        var start = vertex * GlAxesRenderer.FloatsPerVertex;
        return new Vector3(vertices[start], vertices[start + 1], vertices[start + 2]);
    }

    private static Vector3 ReadColor(float[] vertices, int vertex)
    {
        var start = vertex * GlAxesRenderer.FloatsPerVertex + 3;
        return new Vector3(vertices[start], vertices[start + 1], vertices[start + 2]);
    }

    private static float Component(Vector3 v, int axis)
    {
        return axis switch
        {
            0 => v.X,
            1 => v.Y,
            _ => v.Z
        };
    }

    private static bool IsClose(Vector3 actual, Vector3 expected)
    {
        return (actual - expected).Length() < 1e-6f;
    }
}
