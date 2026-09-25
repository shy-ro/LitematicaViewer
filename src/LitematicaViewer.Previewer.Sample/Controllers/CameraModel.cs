using System.Diagnostics;
using System.Numerics;

namespace LitematicaViewer.Previewer.Sample;

// 相机状态的权威持有者。Previewer 只画不记（公开面里没有 GetCamera），
// 想知道「相机现在在哪」的都得来这里问；控制器改了它，再把结果推给 Previewer。
//
// 缩放只动一个自由度：相机到 Target 的距离。朝向取自当前相机自己
// （Position - Target 那条方向），模型不另存一份 yaw/pitch——
// 存两份的话，「验收剧本摆了一个别的视角」与「模型以为的朝向」就会分叉，
// 症状是滚轮一滚相机自己转回某个旧方向，而两次操作单看都各自正确。
internal sealed class CameraModel
{
    // 距离的上下界，定在「立方体还完整地待在画面里」这一段：
    // 下界 2.4 大于外接球半径 0.866 除以 tan(22.5°) = 2.09，再近就该被近裁剪面切到了；
    // 上界 7.0 对应轮廓覆盖率约 3%，再远画面里只剩几个像素，缩放不再有意义。
    // 夹取放在模型里而不是等画面校验去发现：把相机推到看不见东西的位置本就不该是一次合法操作。
    internal const float MinDistance = 2.4f;
    internal const float MaxDistance = 7.0f;

    // 一档滚轮的比例。用比例而不是固定步长：距离是尺度量，等比例推拉才符合手感，
    // 固定步长在远处太慢、在近处一跳就穿。取 0.8 而不是 0.5，是为了三档能推出一个
    // 画面校验量得出来的差别（覆盖率涨 1.56 倍）而不至于一滚就贴脸。
    internal const float ZoomRatioPerStep = 0.8f;

    // 缩放的锚点。相机始终看向它，推拉就是沿这条视线进退。
    // 它是模型的一部分而不是从相机状态里反解出来的：单个相机状态反解不出 target，
    // 而「看着哪儿」正是 orbit 相机区别于自由飞行相机的那一个自由度。
    private readonly Vector3 _target;

    // 相机状态本身就是权威，不再拆成 (yaw, pitch, distance) 存着：
    // 那个拆解不可逆（yaw 会跳变），而模型要做的只是「改一个数再交出去」。
    private CameraState _camera;

    internal CameraModel(CameraState initial, Vector3 target)
    {
        _camera = initial;
        _target = target;
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
            $"clamped={next != wanted} range=[{MinDistance},{MaxDistance}]");
    }

    // 真正的计算。探针不在这里打是因为 VerifyZoom 会连着调上百次，逐次打桩会把终端冲掉；
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

        // Pow 在 |steps| 很大时会溢出到 Inf 或下溢到 0，而 Inf 与 0 被 Clamp 夹到上下界
        // 恰好就是想要的语义（滚到底就停在边界）。只有 NaN 会让 Clamp 失效，而 steps 有限、
        // current 有限是上面两条断言保证的。
        wanted = current * MathF.Pow(ZoomRatioPerStep, steps);
        float next = Math.Clamp(wanted, MinDistance, MaxDistance);

        // 单位方向乘距离再加回 target。写成 Position * ratio 只在 target 恰好是原点时才对，
        // 而「target 是不是原点」不该由缩放来假设。
        Vector3 direction = (_camera.Position - _target) / current;
        _camera = _camera with { Position = _target + (direction * next) };

        return next;
    }

#if DEBUG
    // 缩放的方向、比例、夹取。这三条是纯计算，跟 GL、跟窗口都无关，
    // 所以走模型而不是走合成事件：混进渲染里去验，算错了和画错了就分不开了。
    // 由 Sample 在建模型的时候跑一次（不是 Previewer 初始化时——模型在 Sample 这一侧）。
    //
    // 全程走 ZoomCore 而不是 Zoom：这里要连滚上百档，逐档打桩会把终端冲掉，
    // 而「第五十档被夹住了」这种信息本来也没有用。
    internal static void VerifyZoom()
    {
        CameraModel camera = new(CameraState.Default, Vector3.Zero);
        float start = camera.Distance;

        Debug.Assert(
            start > MinDistance && start < MaxDistance,
            $"[SAMPLE][camera.zoom] 默认距离落在夹取区间之外 distance={start} range=[{MinDistance},{MaxDistance}]");

        // 1. 正向滚轮是拉近，而且正好是一档的比例。
        camera.ZoomCore(1f, out _);
        float expected = start * ZoomRatioPerStep;
        Expect(camera.Distance, expected, "一档正向滚轮");

        // 2. 反向滚轮把距离推回原处。往返不闭合就说明两个方向的比例不是互为倒数，
        //    手感会变成「滚出去再滚回来，画面没回到原样」。
        camera.ZoomCore(-1f, out _);
        Expect(camera.Distance, start, "一来一回");

        // 3. 朝一个方向一直滚，必须停在边界上而不是穿过去。±50 档足够把 Pow 推到
        //    溢出区（0.8^50 ≈ 1.4e-5，1.25^50 ≈ 6.6e4），普通量级的夹取早就触发了。
        Vector3 forwardBefore = camera.Camera.Forward;
        for (int i = 0; i < 50; i++)
        {
            camera.ZoomCore(1f, out _);
        }

        Expect(camera.Distance, MinDistance, "连推近 50 档");

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

        Expect(camera.Distance, MaxDistance, "连推远 50 档");

        // 5. 夹到边界之后相机仍然是个能拿来画的相机。距离算错时 Position 会带上 NaN，
        //    而视图矩阵带着 NaN 走完 GL 全程都不报错，只留一块空白。
        Debug.Assert(
            float.IsFinite(camera.Camera.Position.X) &&
            float.IsFinite(camera.Camera.Position.Y) &&
            float.IsFinite(camera.Camera.Position.Z),
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
            $"step={ZoomRatioPerStep} range=[{MinDistance},{MaxDistance}]");
    }

    private static void Expect(float actual, float expected, string what)
    {
        // 容差 1e-3：距离是单位向量乘出来的再开方，往返一趟的误差在 1e-6 量级，
        // 留三个数量级是给浮点 Pow 的，不是为了放行算错的实现——算错的话偏差是 20% 这个量级。
        Debug.Assert(
            MathF.Abs(actual - expected) < 1e-3f,
            $"[SAMPLE][camera.zoom] {what} 的距离不对 actual={actual:F4} expected={expected:F4}");
    }
#endif
}
