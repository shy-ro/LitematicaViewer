using System.Diagnostics;
using System.Numerics;

namespace LitematicaViewer.Previewer.Sample;

// 相机状态的权威持有者。Previewer 只画不记（公开面里没有 GetCamera），
// 想知道「相机现在在哪」的都得来这里问；控制器改了它，再把结果推给 Previewer。
//
// 它只提供三个动作：缩放（推拉距离）、绕 Target 转视角、沿自己的视角平移。
// 三者都保持「相机看向 Target」这一条不变式——那正是 orbit 相机与自由飞行相机的分界，
// 也是画面校验（轮廓质心落在画面中心）成立的前提。
//
// 三个动作都从当前相机状态出发算，模型不另存一份 yaw/pitch：
// 存两份的话，「验收剧本摆了一个别的视角」与「模型以为的朝向」就会分叉，
// 症状是滚轮一滚相机自己转回某个旧方向，而两次操作单看都各自正确。
//
// 绕转与平移在 CameraModel.Navigation.cs：它们由连续输入驱动，打桩与节流的落点不一样。
internal sealed partial class CameraModel
{
    // 一档滚轮的比例。用比例而不是固定步长：距离是尺度量，等比例推拉才符合手感，
    // 固定步长在远处太慢、在近处一跳就穿。取 0.8 而不是 0.5，是为了三档能推出一个
    // 画面校验量得出来的差别（覆盖率涨 1.56 倍）而不至于一滚就贴脸。
    internal const float ZoomRatioPerStep = 0.8f;

    // 距离的上下界可选，默认两个都不设——「能推多近、能拉多远」是尺度上的偏好，
    // 取决于看的是什么、镜头多宽，模型没有立场替宿主定一个。
    //
    // 曾经把 2.4 / 7.0 写死在这里（当时画面里只有一个单位立方体）。Phase F 起改成入参、
    // 保留接口：平移与绕转一进来，可见范围就不再是「原点周围那一个方块」，
    // 而写死的界会在这里悄悄替人做决定——比如把相机卡在离 Target 2.4 的地方，
    // 于是凑近看一块砖成了做不到的操作，而那表现为「滚轮没反应」。
    private readonly float? _minDistance;
    private readonly float? _maxDistance;

    // 缩放的锚点，也是绕转的轴心、平移时跟着一起走的那个点。
    // 它是模型的一部分而不是从相机状态里反解出来的：单个相机状态反解不出 target，
    // 而「看着哪儿」正是 orbit 相机区别于自由飞行相机的那一个自由度。
    private Vector3 _target;

    // 相机状态本身就是权威，不再拆成 (yaw, pitch, distance) 存着：
    // 那个拆解不可逆（yaw 会跳变），而模型要做的只是「改一个数再交出去」。
    private CameraState _camera;

    internal CameraModel(
        CameraState initial,
        Vector3 target,
        float? minDistance = null,
        float? maxDistance = null)
    {
        // 只设一头是成立的（比如「别推近到穿模」但不管多远）。
        // 两头都设了还反过来就是调用方写错了，越早炸越好：夹取会退化成一个恒定值，
        // 症状是滚轮彻底不动，而那时离出错的地方已经很远。
        Debug.Assert(
            minDistance is not { } min || maxDistance is not { } max || min <= max,
            $"[SAMPLE][camera.ctor] 距离下界大于上界 min={minDistance} max={maxDistance}");

        _camera = initial;
        _target = target;
        _minDistance = minDistance;
        _maxDistance = maxDistance;
    }

    internal CameraState Camera => _camera;

    internal Vector3 Target => _target;

    internal float Distance => Vector3.Distance(_camera.Position, _target);

    // 整体替换相机状态。控制器不用它（它们只做增量），用它的是「把视角摆到某处」——
    // 验收剧本要造几个不同视角去看画面变化，将来的「重置视角」也会走这里。
    // 收 CameraState 而不是要求调用方给 (yaw, pitch, distance)：调用方手里本来就有一个
    // 相机状态（CameraState.LookAt 造出来的），逼它反解成 orbit 三元组等于让它重做一遍
    // 三角函数，而那份反解会带回 yaw 跳变和 1e-5 量级的位置漂移——画面校验正好在数像素。
    internal void Reset(CameraState camera) => _camera = camera;

    internal void Zoom(float steps)
    {
        float current = Distance;
        float next = ZoomCore(steps, out float wanted);

        // 夹住了要打出来：滚轮到底是「推到头停住」还是「推过头把相机丢出去了」，
        // 从画面上看都是「不动了」，只有日志能分开。
        Debug.WriteLine(
            $"[SAMPLE][camera.zoom] steps={steps} distance={current:F4}->{next:F4} " +
            $"clamped={next != wanted} bounds=[{Bound(_minDistance)},{Bound(_maxDistance)}]");
    }

    // 真正的计算。探针不在这里打是因为 VerifyCameraMath 会连着调上百次，逐次打桩会把终端冲掉；
    // 而「哪一次被夹住了」这种信息在连滚五十档的时候本来也没有用。
    private float ZoomCore(float steps, out float wanted)
    {
        Debug.Assert(float.IsFinite(steps), $"[SAMPLE][camera.zoom] steps 不是有限值 steps={steps}");

        float current = Distance;

        // 距离为 0 意味着 target 压在相机上，视图矩阵已经退化了，这是构造时就该排除的状态。
        // 这里只是别让 0 除进来变成 NaN 再一路传到 GPU——GL 对 NaN 一声不吭，画面直接空白。
        Debug.Assert(
            float.IsFinite(current) && current > 0f,
            $"[SAMPLE][camera.zoom] 缩放前的距离不是正数 distance={current} target=({_target}) " +
            $"pos=({_camera.Position})");

        // Pow 在两头的极端上会溢出成 Inf 或下溢成 0；在有界的模型里那被夹到边界，
        // 刚好就是想要的语义（滚到底就停在边界）。
        wanted = current * MathF.Pow(ZoomRatioPerStep, steps);

        float next = wanted;
        if (_minDistance is { } min)
        {
            next = MathF.Max(next, min);
        }

        if (_maxDistance is { } max)
        {
            next = MathF.Min(next, max);
        }

        // 默认不设界，于是连推上千档就会真的把距离推出浮点之外（0.8^-6000 溢出成 Inf）。
        // 位置跟着变成 Inf，视图矩阵整块失效，而 GL 一声不吭——画面空白，
        // 日志里只有几条看不出异常的 SetCamera。
        //
        // 这不是尺度上的限制（尺度由宿主决定，默认就是不给），是数值上的兜底：
        // 算不出一个能画的相机就原地不动，并把这件事打出来。
        if (!float.IsFinite(next) || next <= 0f)
        {
            Debug.WriteLine(
                $"[SAMPLE][camera.zoom] 距离算不出有限正数，维持原状 steps={steps} " +
                $"current={current} wanted={wanted}");
            return current;
        }

        // 单位方向乘距离再加回 target。写成 Position * ratio 只在 target 恰好是原点时才对，
        // 而「target 是不是原点」不该由缩放来假设。
        Vector3 direction = (_camera.Position - _target) / current;
        _camera = _camera with { Position = _target + (direction * next) };

        return next;
    }

    private static string Bound(float? bound) => bound?.ToString("F2") ?? "未设";

#if DEBUG
    // 模型自己的算术：缩放的比例与夹取、绕转的两轴、平移的方向与刚体性。
    // 这些都是纯计算，跟 GL、跟窗口都无关，所以走模型而不是走合成事件：
    // 混进渲染里去验，算错了和画错了就分不开了。
    // 由 Sample 在建模型的时候跑一次（不是 Previewer 初始化时——模型在 Sample 这一侧）。
    internal static void VerifyZoom()
    {
        VerifyZoomArithmetic();
        VerifyPanAndOrbit();
    }

    private static void VerifyZoomArithmetic()
    {
        // 夹取要验就得先把界给出来：默认是没有界的，而「默认没有界」本身是另一条要验的。
        CameraModel camera = new(CameraState.Default, Vector3.Zero, minDistance: 2.4f, maxDistance: 7f);
        float start = camera.Distance;

        Debug.Assert(
            start > 2.4f && start < 7f,
            $"[SAMPLE][camera.zoom] 默认距离落在夹取区间之外 distance={start} range=[2.4,7]");

        // 1. 正向滚轮是拉近，而且正好是一档的比例。
        camera.ZoomCore(1f, out _);
        Expect(camera.Distance, start * ZoomRatioPerStep, 1e-3f, "一档正向滚轮");

        // 2. 反向滚轮把距离推回原处。往返不闭合就说明两个方向的比例不是互为倒数，
        //    手感会变成「滚出去再滚回来，画面没回到原样」。
        camera.ZoomCore(-1f, out _);
        Expect(camera.Distance, start, 1e-3f, "一来一回");

        // 3. 朝一个方向一直滚，必须停在边界上而不是穿过去。±50 档足够把 Pow 推到
        //    溢出区（0.8^50 ≈ 1.4e-5，1.25^50 ≈ 6.6e4），普通量级的夹取早就触发了。
        Vector3 forwardBefore = camera.Camera.Forward;
        for (int i = 0; i < 50; i++)
        {
            camera.ZoomCore(1f, out _);
        }

        Expect(camera.Distance, 2.4f, 1e-3f, "连推近 50 档");

        // 4. 缩放不改朝向。这里用精确相等而不是容差：Yaw/Pitch 根本没被碰过，
        //    方向是它们的纯函数，一个 bit 都不该动。给容差会把「谁顺手归一化了一下 yaw」放过去，
        //    而那正是一条朝向会慢慢漂移的路。
        Debug.Assert(
            camera.Camera.Forward == forwardBefore,
            $"[SAMPLE][camera.zoom] 缩放把朝向改了 before={forwardBefore} after={camera.Camera.Forward}");

        for (int i = 0; i < 50; i++)
        {
            camera.ZoomCore(-1f, out _);
        }

        Expect(camera.Distance, 7f, 1e-3f, "连推远 50 档");

        // 5. 夹到边界之后相机仍然是个能拿来画的相机。距离算错时 Position 会带上 NaN，
        //    而视图矩阵带着 NaN 走完 GL 全程都不报错，只留一块空白。
        Debug.Assert(
            IsFinite(camera.Camera.Position),
            $"[SAMPLE][camera.zoom] 夹取后位置不是有限值 pos=({camera.Camera.Position})");

        // 6. 缩放是绕 target 的：距离变了，到 target 的方向没变。
        Vector3 directionBefore = camera.Camera.Position - camera.Target;
        camera.ZoomCore(1f, out _);
        Vector3 directionAfter = camera.Camera.Position - camera.Target;
        float cosAngle = Vector3.Dot(
            Vector3.Normalize(directionBefore),
            Vector3.Normalize(directionAfter));
        Debug.Assert(
            cosAngle > 0.9999f,
            $"[SAMPLE][camera.zoom] 缩放把相机挪出了视线方向 cos={cosAngle}");

        Debug.WriteLine(
            $"[SAMPLE][camera.zoom] 方向/比例/夹取全通过 start={start:F4} " +
            $"step={ZoomRatioPerStep} range=[2.4,7]");

        // 7. 默认不设界：同样的五十档推不出边界，而是一直等比缩小。
        //    这一条把「默认无界」钉住——哪天有人把界写回默认值，这里立刻红。
        CameraModel unbounded = new(CameraState.Default, Vector3.Zero);
        float unboundedStart = unbounded.Distance;
        for (int i = 0; i < 50; i++)
        {
            unbounded.ZoomCore(1f, out _);
        }

        Expect(unbounded.Distance, unboundedStart * MathF.Pow(ZoomRatioPerStep, 50f), 1e-4f, "无界时连推近 50 档");
        Debug.Assert(
            unbounded.Distance < 2.4f,
            $"[SAMPLE][camera.zoom] 无界时应该能推近到 2.4 以下 distance={unbounded.Distance}");

        // 8. 无界也得停在「画得出来」的范围里。0.8^6000 下溢成 0、0.8^-6000 溢出成 Inf，
        //    两个方向各推一次，距离必须原地不动——不是被夹住，是这一次缩放没有发生。
        foreach (float extreme in new[] { 6000f, -6000f })
        {
            float before = unbounded.Distance;
            unbounded.ZoomCore(extreme, out float wanted);
            Debug.Assert(
                !float.IsFinite(wanted) || wanted <= 0f,
                $"[SAMPLE][camera.zoom] {extreme} 档本该把距离推出浮点范围 wanted={wanted}");
            Debug.Assert(
                unbounded.Distance == before,
                $"[SAMPLE][camera.zoom] {extreme} 档算不出有限距离时没有维持原状 " +
                $"before={before} after={unbounded.Distance}");
            Debug.Assert(
                IsFinite(unbounded.Camera.Position),
                $"[SAMPLE][camera.zoom] {extreme} 档之后位置不是有限值 pos=({unbounded.Camera.Position})");
        }

        Debug.WriteLine(
            $"[SAMPLE][camera.zoom] 无界：连推 50 档到 {unbounded.Distance:E3}（无界时不会停在边界），" +
            "两头溢出各维持原状一次");
    }

    private static float Wrap(float degrees)
    {
        // yaw 落在 (-180,180]，两次取值之差可能绕了一圈（179.9 与 -179.9 只差 0.2 度）。
        float wrapped = (degrees + 180f) % 360f;
        if (wrapped < 0f)
        {
            wrapped += 360f;
        }

        return wrapped - 180f;
    }

    private static bool IsFinite(Vector3 v) =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static void Expect(float actual, float expected, float tolerance, string what)
    {
        // 容差按量级给：距离是单位向量乘出来的再开方，往返一趟的误差在 1e-6 量级，
        // 留两三个数量级是给浮点 Pow 和三角函数链的，不是为了放行算错的实现——
        // 方向、比例这一类的错，偏差都在 1 或 20% 这个量级。
        Debug.Assert(
            MathF.Abs(actual - expected) < tolerance,
            $"[SAMPLE][camera] {what} 不对 actual={actual:F6} expected={expected:F6} tolerance={tolerance}");
    }
#endif
}
