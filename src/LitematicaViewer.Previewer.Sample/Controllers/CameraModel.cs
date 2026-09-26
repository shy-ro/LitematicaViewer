using System.Diagnostics;
using System.Numerics;

namespace LitematicaViewer.Previewer.Sample;

// 相机状态的权威持有者。Previewer 只画不记（公开面里没有 GetCamera），
// 想知道「相机现在在哪」的都得来这里问；控制器改了它，再把结果推给 Previewer。
//
// 三个动作：移动指针转视角（只改朝向，不动位置）、W/A/S/D 沿视线平移、滚轮沿视线推拉。
// 姿态是**第一视角**——像游戏里操作一个人物那样，拖鼠标是转头，不是绕着一个点公转。
//
// 曾经写成绕 Target 转的 orbit 相机：那个模型下平移会把 Target 一起带走，于是「走两步再转视角」
// 得到的是绕着脚边某个点公转，盯着画面看的人只会觉得手感很怪。orbit 唯一的好处是
// 「相机始终看向 Target」这条不变式撑着画面校验里「轮廓质心落在画面中心」那条断言，
// 而那一类检查现在有更直白的判据：相机看向原点时才算（见 DebugCube.IsVerifiable）。
//
// 于是 Target 不再是模型持有的轴心，而是**派生量**：Position + Forward * Distance，
// 也就是「视线正前方 Distance 处那个点」。滚轮推拉把它按住不动（推近就是朝它靠过去），
// 转视角让视线扫过它，平移把它一起带走。三者合起来正好是「看着哪儿」该有的行为。
//
// 三个动作都从当前相机状态出发算，模型不另存一份 yaw/pitch：
// 存两份的话，「验收剧本摆了一个别的视角」与「模型以为的朝向」就会分叉，
// 症状是滚轮一滚相机自己转回某个旧方向，而两次操作单看都各自正确。
//
// 转视角与平移在 CameraModel.Navigation.cs：它们由连续输入驱动，打桩与节流的落点不一样。
internal sealed partial class CameraModel
{
    // 一档滚轮推进的距离，单位是方块。固定值不随模型缩放：缩放的本质是推人物的位置，
    // 手感必须统一；合适的默认值按「一秒连滚五六档能走出 MC 飞行的速度」取。
    // 做成属性是为了让宿主能按自己的尺度改它（Sample 目前不覆盖）。
    internal float ZoomStep { get; set; } = 2f;

    // 距离的上下界可选，默认两个都不设——「能推多近、能拉多远」是尺度上的偏好，
    // 取决于看的是什么、镜头多宽，模型没有立场替宿主定一个。
    //
    // 曾经把 2.4 / 7.0 写死在这里（当时画面里只有一个单位立方体）。Phase F 起改成入参、
    // 保留接口：相机会走会转之后，可见范围不再是「原点周围那一个方块」，
    // 而写死的界会在这里悄悄替人做决定——比如把相机卡在离目标点 2.4 的地方，
    // 于是凑近看一块砖成了做不到的操作，而那表现为「滚轮没反应」。
    private readonly float? _minDistance;
    private readonly float? _maxDistance;

    // 缩放的参考距离：滚轮一档改的是它，相机随之沿视线前进/后退相同的量。
    //
    // 存这个标量而不是存一个世界坐标的点：第一视角下「看着哪儿」完全由 (Position, Yaw, Pitch)
    // 决定，再存一份 Target 就有了两个权威，转视角时它们必然分叉——而分叉的表现是
    // 「转完视角再滚轮，相机朝着一个谁都没在看的方向飞过去」。
    private float _zoomDistance;

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

        // 参考距离的初值取自「初始相机到给定点的距离」。给一个点而不是给一个数，
        // 是因为调用方手里本来就有那个点（立方体在原点），而它不必去想「参考距离该取多少」——
        // 那个数就是「一开始看得有多远」，让调用方自己量一遍反而多一次出错的机会。
        _zoomDistance = Vector3.Distance(initial.Position, target);
        Debug.Assert(
            float.IsFinite(_zoomDistance) && _zoomDistance > 0f,
            $"[SAMPLE][camera.ctor] 初始相机压在给定的点上，缩放没有可推拉的尺度 " +
            $"pos=({initial.Position}) target=({target})");

        _minDistance = minDistance;
        _maxDistance = maxDistance;
    }

    internal CameraState Camera => _camera;

    // 视线正前方 Distance 处的那个点。派生量，所以转视角时它跟着视线扫，平移时跟着一起走，
    // 只有滚轮推拉会把它按住不动。
    internal Vector3 Target => _camera.Position + (_camera.Forward * _zoomDistance);

    internal float Distance => _zoomDistance;

    // 整体替换相机状态。控制器不用它（它们只做增量），用它的是「把视角摆到某处」——
    // 验收剧本要造几个不同视角去看画面变化，将来的「重置视角」也会走这里。
    // 收 CameraState 而不是要求调用方给 (yaw, pitch, distance)：调用方手里本来就有一个
    // 相机状态（CameraState.LookAt 造出来的），逼它拆成三个数再拼回来等于让它重做一遍
    // 三角函数，而那份拆解会带回 yaw 跳变和 1e-5 量级的位置漂移——画面校验正好在数像素。
    //
    // 它不动 _zoomDistance：Reset 的语义是「把相机摆到某个姿态」，缩放的参考尺度
    // 跟姿态无关。摆到一个离原点更远的姿态之后再滚轮，推拉的步长仍是原来那个尺度——
    // 这不是遗漏，是「谁来定参考距离」只有一个答案：构造函数那一次。
    internal void Reset(CameraState camera) => _camera = camera;

    // 把相机摆成展台那一套：站在 target 的 pitch 方向上、与它相距 distance，并且**把参考距离
    // 也设成它**。
    //
    // Reset 不动参考距离是有意的（姿态与缩放的尺度无关），展台反过来：那里参考距离就是公转半径，
    // 必须是同一个数——不然滚轮推拉动的是「看着哪儿」，而公转绕着的是另一个点，
    // 表现是「滚一下，模型从画面中心漂走」。
    //
    // 朝向（yaw）不动，因为「从哪个角度看」是进来之前就定好的，而俯仰由展台角给定。
    // 位置不是「保持不动再把视线拧过去」——那样相机与目标的距离是随机的，
    // 公转半径跟着随机；位置是由 (target, yaw, pitch, distance) 算出来的那一个点。
    internal void FrameTurntable(Vector3 target, float pitch, float distance)
    {
        Debug.Assert(
            float.IsFinite(distance) && distance > 0f,
            $"[SAMPLE][camera.showcase] 展台距离不是正数 distance={distance}");

        float clamped = Math.Clamp(pitch, -CameraState.MaxPitch, CameraState.MaxPitch);
        Vector3 forward = CameraState.ForwardOf(_camera.Yaw, clamped);

        _zoomDistance = distance;
        _camera = _camera with
        {
            // 位置 = 目标 − 视线 × 距离：视线指着目标，于是从目标往回退一个距离就是相机该在的地方。
            Position = target - (forward * distance),
            Pitch = clamped,
        };

        // 这条断言就是「展台绕着的是模型中心」本身：Target 是派生量（位置 + 视线 × 距离），
        // 上面那个位置是照着它反解出来的，所以它必然落在 target 上。真不落在上面，
        // 只能是位置与参考距离被分成了两处算——而那正是要防的。
        Debug.Assert(
            (Target - target).Length() < 1e-3f * distance,
            $"[SAMPLE][camera.showcase] 取景之后 Target 不在目标上 target=({target}) " +
            $"actual=({Target}) distance={distance:F4}");
    }

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

        float current = _zoomDistance;

        // 距离可以为负（人物穿过了原来看着的点），只有非有限值才是失效状态——
        // 那会让视图矩阵带着 NaN 走完 GL 全程都不报错，只留一块空白。
        Debug.Assert(
            float.IsFinite(current),
            $"[SAMPLE][camera.zoom] 缩放前的参考距离不是有限值 distance={current} " +
            $"pos=({_camera.Position}) forward=({_camera.Forward})");

        // 人物位置推进：一档滚轮沿视线走 ZoomStep 距离，距离变为负值（相机穿过了
        // 原来看着的点）是合法状态——位置继续前移就是了。按方块统一而不按模型缩放：
        // 大模型按比例放步长的话，滚轮在画面上的推进感反而跟不上，而且用户在展台和
        // 自由视角之间得到的手感必须一致。
        wanted = current - (ZoomStep * steps);

        float next = wanted;
        if (_minDistance is { } min)
        {
            next = MathF.Max(next, min);
        }

        if (_maxDistance is { } max)
        {
            next = MathF.Min(next, max);
        }

        // 兜底只剩「算不出有限数」一种（线性运算只有步长×档数极大时才可能溢出）。
        // 负距离不再拦：那是「人物穿过了原来看着的点、继续往前飞」，位置推进语义下
        // 完全正常——旧实现把这里当成失效停住，症状是推到近处再滚就没反应。
        if (!float.IsFinite(next))
        {
            Debug.WriteLine(
                $"[SAMPLE][camera.zoom] 距离算不出有限数，维持原状 steps={steps} " +
                $"current={current} wanted={wanted}");
            return current;
        }

        // 沿视线把自己挪过去：推近就是把相机往「看着的那个点」送，而那个点本身不动。
        //
        // 位移取 (current - next) 而不是别的比例：这样 Position + Forward * Distance 在推拉前后
        // 是同一个世界坐标——凑近看某块砖时，目光落在砖上不动，而不是「滚一下砖自己跑了」。
        // 写成「位置按比例缩、方向另算」是 orbit 那个老实现的做法，两处一分开就又有了两个权威。
        Vector3 forward = _camera.Forward;
        _zoomDistance = next;
        _camera = _camera with { Position = _camera.Position + (forward * (current - next)) };

        return next;
    }

    private static string Bound(float? bound) => bound?.ToString("F2") ?? "未设";

    // yaw 落在 (-180,180]，两次取值之差可能绕了一圈（179.9 与 -179.9 只差 0.2 度）。
    // 转视角每动一下就归一次，不归的话连续移动会把 yaw 累到几万度，
    // 那时 sin/cos 的精度开始掉，表现是「转了很久之后视角开始抖」。
    private static float Wrap(float degrees)
    {
        float wrapped = (degrees + 180f) % 360f;
        if (wrapped < 0f)
        {
            wrapped += 360f;
        }

        return wrapped - 180f;
    }

#if DEBUG
    // 模型自己的算术：缩放的比例与夹取、转视角的两轴、平移的方向与刚体性。
    // 这些都是纯计算，跟 GL、跟窗口都无关，所以走模型而不是走合成事件：
    // 混进渲染里去验，算错了和画错了就分不开了。
    // 由 Sample 在建模型的时候跑一次（不是 Previewer 初始化时——模型在 Sample 这一侧）。
    internal static void VerifyCameraMath()
    {
        VerifyZoomArithmetic();
        VerifyLookAndPan();
    }

    private static void VerifyZoomArithmetic()
    {
        // 夹取要验就得先把界给出来：默认是没有界的，而「默认没有界」本身是另一条要验的。
        CameraModel camera = new(CameraState.Default, Vector3.Zero, minDistance: 2.4f, maxDistance: 7f);
        float start = camera.Distance;

        Debug.Assert(
            start > 2.4f && start < 7f,
            $"[SAMPLE][camera.zoom] 默认距离落在夹取区间之外 distance={start} range=[2.4,7]");

        // 1. 正向滚轮是拉近，而且正好是固定的一档步长。
        camera.ZoomCore(1f, out _);
        Expect(camera.Distance, start - camera.ZoomStep, 1e-3f, "一档正向滚轮");

        // 2. 反向滚轮把距离推回原处。固定步长下两个方向的推进量必须严格相等，
        //    手感才对称：滚出去再滚回来，画面必须回到原样。
        camera.ZoomCore(-1f, out _);
        Expect(camera.Distance, start, 1e-3f, "一来一回");

        // 3. 朝一个方向一直滚，必须停在边界上而不是穿过去。步长 1 时 50 档推近 50，
        //    普通量级的夹取早就触发了。
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

        // 6. 推拉是「朝看着的那个点靠过去」：那个点在推拉前后是同一个世界坐标。
        //    位置沿视线挪而参考距离按比例缩，这两件事是耦合的，得一起对——
        //    错了的表现是「滚一下，看着的那块砖自己跑了」，而单看距离变化完全正常。
        Vector3 targetBefore = camera.Target;
        camera.ZoomCore(1f, out _);
        Expect((camera.Target - targetBefore).Length(), 0f, 1e-4f, "推拉前后的 Target");

        Debug.Assert(
            camera.Camera.Forward == forwardBefore,
            $"[SAMPLE][camera.zoom] 推拉把朝向改了 before={forwardBefore} after={camera.Camera.Forward}");

        Debug.WriteLine(
            $"[SAMPLE][camera.zoom] 方向/步长/夹取/Target 不动全通过 start={start:F4} " +
            $"step={camera.ZoomStep} range=[2.4,7]");

        // 7. 默认不设界：推近是线性的，没有「推到底的边界」。穿过 0（相机越过了
        //    原来看着的点）之后继续推进，距离变负、位置继续前移——人物位置推进语义。
        //    哪天有人把界写回默认值、或者把穿越改成停住，这里立刻红。
        CameraModel unbounded = new(CameraState.Default, Vector3.Zero);
        float unboundedStart = unbounded.Distance;
        for (int i = 0; i < 3; i++)
        {
            unbounded.ZoomCore(1f, out _);
        }

        Expect(unbounded.Distance, unboundedStart - (3f * unbounded.ZoomStep), 1e-4f, "无界时线性推近");
        Debug.Assert(
            unbounded.Distance < 2.4f,
            $"[SAMPLE][camera.zoom] 无界时应该能推近到 2.4 以下 distance={unbounded.Distance}");

        for (int i = 0; i < 50; i++)
        {
            unbounded.ZoomCore(1f, out _);
        }

        Debug.Assert(
            unbounded.Distance < unboundedStart - (3f * unbounded.ZoomStep),
            $"[SAMPLE][camera.zoom] 穿过 0 之后推进停住了 distance={unbounded.Distance}");
        Debug.Assert(
            IsFinite(unbounded.Camera.Position),
            $"[SAMPLE][camera.zoom] 穿过 0 之后位置不是有限值 pos=({unbounded.Camera.Position})");

        // 8. 兜底只拦「算不出有限数」：档数大到 step*steps 溢出时该次缩放不发生。
        //    有限的大档位（1e9）则照常线性推进——位置仍然是有限值。
        float before8 = unbounded.Distance;
        unbounded.ZoomCore(1e9f, out float wanted);
        Debug.Assert(
            unbounded.Distance == before8 - (1e9f * unbounded.ZoomStep),
            $"[SAMPLE][camera.zoom] 巨大但有限的档位没有线性推进 " +
            $"before={before8} after={unbounded.Distance}");

        unbounded.ZoomCore(float.MaxValue, out float overflowWanted);
        Debug.Assert(
            !float.IsFinite(overflowWanted),
            $"[SAMPLE][camera.zoom] MaxValue 档本该把步长积溢出成非有限值 wanted={overflowWanted}");
        Debug.Assert(
            unbounded.Distance == before8 - (1e9f * unbounded.ZoomStep),
            $"[SAMPLE][camera.zoom] 溢出档位没有维持原状 after={unbounded.Distance}");

        Debug.WriteLine(
            $"[SAMPLE][camera.zoom] 无界：线性推近/穿过 0/巨大档位/溢出档全通过，" +
            $"穿过后距离 {unbounded.Distance:E3}");
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
