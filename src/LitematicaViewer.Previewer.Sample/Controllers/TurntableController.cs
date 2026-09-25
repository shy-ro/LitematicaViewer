using System.Collections.Immutable;
using System.Diagnostics;
using System.Numerics;
using Avalonia;
using LitematicaViewer.Previewer;

namespace LitematicaViewer.Previewer.Sample;

// 展台：拖动带惯性、松手之后缓慢自转、只允许缩放，不允许移动视角。
//
// 和自由视角的控制器分成两个类，不是同一个类里按模式分支：两边共用的只有「订阅点什么、
// 把结果推给 Previewer」，动作本身完全不同（一个改朝向、一个绕圆心公转）。
// 混在一起之后，「这个模式该不该响应这条事件」会变成一串 if，而漏掉一处就是「某个模式下没反应」。
//
// 时间只从 Tick 来：移动事件没有时间戳（它只报增量），而惯性与自转都是时间的函数。
internal sealed class TurntableController : IDisposable
{
    // 进模式、以及换目标时的展台角。取 25 度和自由视角的默认俯仰一致，切过来时画面不会跳。
    internal const float DefaultPitch = 25f;

    // 抬降的两头：正的是俯视、负的是仰视（钻到底下看）。区间是策略，所以留在这儿而不是写进模型。
    internal const float MinPitch = -60f;
    internal const float MaxPitch = 60f;

    // 取景距离 = 目标的水平半对角线 × 这个数。6.6 让模型连同外圈光环一起舒服地落在画面里
    // ——按 fov 45 度竖着算，模型的投影高度约占画面的三分之一。
    private const float FrameFactor = 6.6f;

    // 缩放的夹取。太近会推进模型里面去，太远模型就成了一小块。展台的主语是模型，
    // 这两个界是「展示」这件事本身要求的，所以这次没有留给调用方去配。
    internal const float MinZoomFactor = 3.0f;
    internal const float MaxZoomFactor = 12.0f;

    // 手速估计的低通系数。0.5 下大约两帧收敛，跟得上一甩，也滤得掉单帧抖动。
    private const float MeasureBlend = 0.5f;

    // 速度小到这个量级就不推相机了。一阶滞后只是无限逼近零，不截断的话静止时每帧都 SetCamera，
    // 而 SetCamera 会让画面校验重验一帧——预算只有 24 帧，几下就用光了。
    private const float StillVelocity = 0.01f;

    private readonly Previewer _previewer;
    private readonly CameraModel _camera;
    private readonly ImmutableArray<ShowcaseTarget> _targets;
    private int _index;

    // 拖动中。起止条件与自由视角那边不同（那边靠指针进出，这边靠按键），但含义一样：
    // 手势进行中。
    private bool _dragging;

    // 角速度（度/秒，正数 = yaw 增大）。拖动时由手给，松手之后它衰减、最后被自转那个目标接管。
    private float _velocity;

    // 这一帧里拖动攒下的角度、以及一次手势的总量。前者用来估手速（移动事件没有时间戳），
    // 后者只用于收尾那一行日志。
    private float _dragYaw;
    private float _dragTotal;
    private int _dragMoves;

    // 松手之后的秒数，用来决定自转该不该起来。
    private double _idleSeconds;

    // 自转起来那一刻打一条日志。一帧一条会把终端冲掉，而「自转到底起没起来」是个是非题。
    private bool _driftLogged;

    private bool _disposed;

    internal TurntableController(
        Previewer previewer,
        CameraModel camera,
        ImmutableArray<ShowcaseTarget> targets)
    {
        Debug.Assert(
            !targets.IsEmpty,
            "[SAMPLE][showcase] 展台一个目标都没有 note=场景里至少要有一样东西可展示");

        _previewer = previewer;
        _camera = camera;
        _targets = targets;

        _previewer.LookStarted += OnLookStarted;
        _previewer.LookMoved += OnLookMoved;
        _previewer.LookEnded += OnLookEnded;
        _previewer.Scrolled += OnScrolled;
        _previewer.Tick += OnTick;

        // 进来就把相机摆到展台上：位置是照着「站在 DefaultPitch 上、离目标这么远」反解出来的，
        // 而不是保持原来那个随机的位置。滚轮推拉的参考距离也在这一步和公转半径对齐。
        Frame();
    }

    internal ShowcaseTarget Current => _targets[_index];

    internal int Index => _index;

    internal int Count => _targets.Length;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _previewer.LookStarted -= OnLookStarted;
        _previewer.LookMoved -= OnLookMoved;
        _previewer.LookEnded -= OnLookEnded;
        _previewer.Scrolled -= OnScrolled;
        _previewer.Tick -= OnTick;

        // 光环是这个控制器点起来的，也由它熄掉：留着不管的话，切回自由视角之后底面上还浮着一圈
        // 发蓝的东西，而那时它没有任何含义。
        _previewer.SetPedestal(false, Current.Radius, Current.BaseY);
    }

    // 切到下一个／上一个目标。今天表里只有一个，所以它是「接口先就位」——
    // 而正因为只有一个，它必须写成能直接用的样子：等多 region 落地时改的是那张表，不是这里。
    internal void Next() => Move(+1);

    internal void Previous() => Move(-1);

    // 窗口失活时由宿主调。速度清零，而不是留着：
    //
    // 适配器那边的 ReleaseLook() 已经会把手势收掉（它会发 LookEnded），所以进来时 _dragging
    // 通常已经是 false——但 _velocity 是上一帧估出来的，留着它接下来会照着这个数一直转下去。
    // 那个数来自一双已经离开键盘的手（Alt+Tab 的那一刻），而表现是「切回来发现模型自己在飞快地转」。
    // 所以这里不判断 _dragging，一律清：这一条的语义是「刚才那一下不算数」。
    internal void ReleaseDrag()
    {
        Debug.WriteLine(
            $"[SAMPLE][showcase.drag] 失活，收掉拖动并把速度清零 wasDragging={_dragging} " +
            $"velocity={_velocity:F2} 度/秒");

        _dragging = false;
        _velocity = 0f;
        _dragYaw = 0f;
        _dragTotal = 0f;
        _dragMoves = 0;
        _idleSeconds = 0d;
        _driftLogged = false;
    }

    private void Move(int step)
    {
        _index = ((_index + step) % _targets.Length + _targets.Length) % _targets.Length;
        Frame();
    }

    // 把相机摆到当前目标上，并把光环按它的尺寸放好。
    private void Frame()
    {
        ShowcaseTarget target = Current;
        float distance = target.Radius * FrameFactor;

        _camera.FrameTurntable(target.Centre, DefaultPitch, distance);

        _velocity = 0f;
        _dragYaw = 0f;
        _dragTotal = 0f;
        _dragMoves = 0;
        _idleSeconds = 0d;
        _driftLogged = false;

        // 光环的半径就是目标的水平半对角线：网格那一份是按「半径为 1 = 刚好贴住目标外接圆」画的，
        // 内缘贴着它外侧一点点（具体倍数在 GlPedestalRenderer.RingRadii[0]），留出一条细缝。
        _previewer.SetPedestal(true, target.Radius, target.BaseY);
        _previewer.SetCamera(_camera.Camera);

        Debug.WriteLine(
            $"[SAMPLE][showcase.frame] 目标={target.Name} index={_index + 1}/{_targets.Length} " +
            $"centre=({target.Centre}) radius={target.Radius:F4} baseY={target.BaseY:F3} " +
            $"distance={distance:F4} pitch={DefaultPitch}");
    }

    private void OnLookStarted()
    {
        // 手一按住就停：模型在被抓着的过程中还在自转的话，拖动会被叠加的旋转拽着走，
        // 手感是「拖不动」或者「拖过了」。
        _dragging = true;
        _velocity = 0f;
        _dragYaw = 0f;
        _dragTotal = 0f;
        _dragMoves = 0;
        _idleSeconds = 0d;
        _driftLogged = false;

        Debug.WriteLine(
            $"[SAMPLE][showcase.drag] 起手 yaw={_camera.Camera.Yaw:F2} pitch={_camera.Camera.Pitch:F2} " +
            $"distance={_camera.Distance:F4} dragSensitivity={_previewer.DragSensitivity}");
    }

    private void OnLookMoved(Avalonia.Vector delta)
    {
        _dragMoves++;

        // 灵敏度每条重新读：拖滑块要当场生效，而一次拖动可以持续几秒。
        float sensitivity = _previewer.DragSensitivity;

        // 往右拖 yaw 增大（相机往右绕），往下拖 pitch 增大（相机抬高、更俯视）——
        // 与自由视角的鼠标方向一致，两套手势的手感不该打架。
        float yaw = (float)delta.X * sensitivity;
        float pitch = (float)delta.Y * sensitivity;

        _dragYaw += yaw;
        _dragTotal += yaw;

        _camera.Orbit(yaw, pitch, MinPitch, MaxPitch);
        _previewer.SetCamera(_camera.Camera);
    }

    private void OnLookEnded()
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        _idleSeconds = 0d;

        // _velocity 就是甩出去的速度（Tick 里一直在估）。这一行是「拖完松手到底会不会转起来」
        // 的唯一证据——而它是个是非题，所以逐次打不亏。
        Debug.WriteLine(
            $"[SAMPLE][showcase.drag] 松手 moves={_dragMoves} 攒下={_dragTotal:F2}度 " +
            $"velocity={_velocity:F2} 度/秒 yaw={_camera.Camera.Yaw:F2} pitch={_camera.Camera.Pitch:F2}");

        _dragMoves = 0;
        _dragTotal = 0f;
    }

    private void OnScrolled(float steps)
    {
        _camera.Zoom(steps);

        ShowcaseTarget target = Current;
        float min = target.Radius * MinZoomFactor;
        float max = target.Radius * MaxZoomFactor;

        if (_camera.Distance < min || _camera.Distance > max)
        {
            // 夹住时用取景那条路复位，而不是自己算一遍位置：公转的几何（位置 = 目标 − 视线 × 距离）
            // 只有那一份实现，第二份迟早和第一份分叉。
            float clamped = Math.Clamp(_camera.Distance, min, max);
            _camera.FrameTurntable(target.Centre, _camera.Camera.Pitch, clamped);

            Debug.WriteLine(
                $"[SAMPLE][showcase.zoom] 距离夹住 wanted={_camera.Distance:F4} clamped={clamped:F4} " +
                $"range=[{min:F4},{max:F4}] target={target.Name}");
        }

        _previewer.SetCamera(_camera.Camera);
    }

    private void OnTick(double delta)
    {
        if (_dragging)
        {
            // 拖动中不积分：转了多少是手直接给的（LookMoved），不是从速度推出来的。
            // 这一帧攒的角度只有估手速这一个用途。
            _velocity = TurntableSpin.Measure(_dragYaw, delta, _velocity, MeasureBlend);
            _dragYaw = 0f;
            return;
        }

        // 第一帧的 delta 是 0（没有上一帧），累加它没有意义。
        if (delta <= 0d)
        {
            return;
        }

        _idleSeconds += delta;

        // 自转不是「到点突然起步」：速度的衰减目标从 0 换成自转速度，速度本身是连续的，
        // 于是甩出去的余速会自己接上自转，中间不会先停住、再重新起步。
        float target = _idleSeconds >= _previewer.SpinIdleDelay
            ? TurntableSpin.IdleSpeed(_previewer.SpinIdleSpeed)
            : 0f;

        _velocity = TurntableSpin.Decay(_velocity, target, delta, _previewer.SpinDamping);

        if (MathF.Abs(_velocity) < StillVelocity)
        {
            _velocity = 0f;
            return;
        }

        if (target != 0f && !_driftLogged)
        {
            _driftLogged = true;
            Debug.WriteLine(
                $"[SAMPLE][showcase.spin] 自转起来了 velocity={_velocity:F3} 度/秒 " +
                $"idleDelay={_previewer.SpinIdleDelay} idleSpeed={_previewer.SpinIdleSpeed} " +
                $"note=逆时针（yaw 减小）");
        }

        _camera.Orbit(_velocity * (float)delta, 0f, MinPitch, MaxPitch);
        _previewer.SetCamera(_camera.Camera);
    }
}
