using System.Collections.Immutable;
using System.Numerics;

namespace LitematicaViewer.Previewer.Sample;

// 展台上要展示的一个东西。纯数据（R6）：不可变记录，没有方法、没有状态。
//
// 要的是**水平半对角线**而不是外接球半径：底下那个光环是围着它画的一圈，而光环躺在水平面上，
// 所以决定光环多大的是水平的轮廓，不是斜着量出来的那个半径。
//
// 底面高度是光环所在的那个平面：模型的底在哪儿，光环就落在哪儿。写成一个数而不是「包围盒」，
// 是因为光环只要这一条；region 落地之后由 IntBounds 算出来。
internal readonly record struct ShowcaseTarget(string Name, Vector3 Centre, float Radius, float BaseY);

internal static class ShowcaseTargets
{
    // 今天只有一个目标：场景里那个单位立方体（半边 0.5，水平半对角线 0.5√2，底面在 −0.5）。
    //
    // 「分 region 展示」的接口就是这张表：region 的解析与渲染落地之后，这里每一项对应一个 region，
    // 尺寸从它的 IntBounds 来，而展台那一层（公转、取景、光环、切页）一行都不用改。
    internal static readonly ShowcaseTarget Demo = new(
        "立方体",
        Vector3.Zero,
        MathF.Sqrt(0.5f),
        -0.5f);

    internal static ImmutableArray<ShowcaseTarget> All { get; } = [Demo];
}
