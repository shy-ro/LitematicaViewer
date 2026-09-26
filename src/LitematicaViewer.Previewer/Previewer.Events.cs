using System.Diagnostics;
using Avalonia;
using Avalonia.Input;
using Matrix4x4 = System.Numerics.Matrix4x4;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace LitematicaViewer.Previewer;

// 本文件是 Previewer 的对外公开面：七个事件、SetCamera、四个手感的导航设置（MoveSpeed /
// LookSensitivity / DragSensitivity / Spin*）、以及展台光环那一组（SetPedestal）。
// Phase D 的清单里这个文件叫 Previewer.Events.cs，后来加的一并放这儿——
// 它们都是同一个东西：Previewer 允许外界碰的表面。
//
// 没有 GetCamera，也没有任何读回相机状态的口子：外部想跟踪当前相机，自己在控制器里维护。
// Previewer 负责画，不是状态的权威持有者，开了这个口子就会有人拿它当状态源，
// 然后在某一帧上读到和控制器不一致的相机。
public partial class Previewer
{
    // 滚轮增量。正负方向不由 Previewer 约定：它只把原始增量递出去，
    // 「向上滚是拉近还是推远」是控制器的事，将来改手感不必动这里。
    public event Action<float>? Scrolled;

    // 看向手势：指针在控件上移动就是在转视角，不需要按任何键——第一人称的默认操作。
    //
    // 报的是**增量**（控件坐标下的 DIP，不是物理像素），而不是指针位置。这一条是 Phase F 改的，
    // 起因是固定鼠标：适配器把光标钉在视口中心，于是「指针在哪」这个信息在钉住的时候恒等于中心，
    // 真正有意义的只剩两次之间的差。而那个差必须在**知道光标被挪到哪儿**的那一层算出来——
    // 控制器不知道光标被挪走过，它按事件位置相减得到的会是「挪动 + 手移动」的混合，
    // 表现是转一下画面就跳一段，而单看灵敏度、方向、符号全都是对的。
    //
    // 起点不带参数：它现在只表示「一段手势开始了」，没有任何数值是这一层知道的
    // （参照点在适配器手里）。终点同样不带：这一程转了多少，由控制器自己汇总。
    //
    // 它原来叫 DragStarted/DragMoved/DragEnded，改成默认转动之后「拖拽」是假话：没有键被按着。
    public event Action? LookStarted;

    public event Action<Vector>? LookMoved;

    public event Action? LookEnded;

    // 按键状态变化。key 直接用 Avalonia 的枚举：Previewer 本来就依赖 Avalonia，
    // 再造一个 Key 枚举只会多一层翻译表，而翻译表是漏项的高发地。
    // 长按会重复触发 down（平台自动重复），事件只报告状态，去重是控制器的事——
    // 控制器按状态处理时重复的 down 天然幂等，按边沿处理才会踩到。
    public event Action<Key, bool>? KeyChanged;

    // 指针悬停位置（控件 DIP 坐标），不需要按键。拾取链路的入口：宿主把它换算成
    // 射线去问体素数据「鼠标指到了哪个方块」。移动一秒几百条，宿主自己节流——
    // 每次都做完整拾取的话可以在 Tick 里做（存最新位置，每帧算一次）。
    public event Action<Avalonia.Vector>? HoverMoved;

    // 物理像素尺寸，与 GL 视口一致（不是 DIP）。首帧会发一次（从 0x0 到实际尺寸），
    // 之后每次尺寸变化各发一次。
    public event Action<int, int>? ViewportResized;

    // 每渲染一帧发一次，参数是距上一帧的秒数。控制器靠它推进与时间有关的东西（惯性、动画）。
    public event Action<double>? Tick;

    // 两个手感参数。它们是 AvaloniaProperty 而不是控制器里的常量，理由是**一个系统里
    // 能调同一个东西的地方只能有一个**：控制器是这个控件最亲近的几个对象，但控件才是它们
    // 唯一的共同引用，而侧边栏、将来的设置面板拿到的也是控件。
    //
    // 做成 AvaloniaProperty 而不是普通属性，是为了让它能配在 XAML 里、能被滑块绑上：
    // 普通属性在 UI 那一层没有可订立的通知，滑块就只能自己维护一份影子状态，
    // 于是「滑块显示的值」与「控制器实际用的值」成了两处。
    //
    // 控件自己不读这两个值：它不移动相机，动相机是挂上来的导航控制器那一层的事。
    public static readonly StyledProperty<float> MoveSpeedProperty =
        AvaloniaProperty.Register<Previewer, float>(nameof(MoveSpeed), defaultValue: 5f);

    public static readonly StyledProperty<float> LookSensitivityProperty =
        AvaloniaProperty.Register<Previewer, float>(nameof(LookSensitivity), defaultValue: 0.10f);

    // 每秒走多少世界单位（＝方块）。5 是每秒五个方块：渲染器里那个单位立方体就是按
    // 「一个 MC 方块 = 1×1×1 世界单位」画的（MC 里一方块也是一米），MC 里走路大约 4.3 方块/秒、
    // 冲刺 5.6——这个数调在两者之间，是在真机上拖滑块试出来的。
    //
    // 材质包的分辨率（16x16 / 256x256 / 2048x2048）不该配在这里：它决定一个方块贴多少纹素，
    // 方块的世界尺寸始终是 1，要按材质包配的是贴图采样（mipmap、过滤）。
    public float MoveSpeed
    {
        get => GetValue(MoveSpeedProperty);
        set => SetValue(MoveSpeedProperty, value);
    }

    // 指针每移过一个 DIP 转多少度。0.10 下横着扫满 1024 DIP 宽的窗口约 102 度，转半圈要横移一千八百多个 DIP。
    //
    // 用 DIP 而不是物理像素：150% 缩放的显示器上同样的手部动作该转同样的角度，
    // 按物理像素算的话那台机器上会快 1.5 倍，表现成「换了台显示器手感就变了」。
    public float LookSensitivity
    {
        get => GetValue(LookSensitivityProperty);
        set => SetValue(LookSensitivityProperty, value);
    }

    // 展台模式：按住指针每移过一个 DIP 转多少度。比自由视角大，因为拖动是「一次有头有尾的手势」——
    // 自由视角下光标被钉在中心、手可以一直划，所以灵敏度要压小（0.10）；拖动的行程受屏幕限制，
    // 0.20 下横扫 1024 DIP 约 205 度，甩半圈是一个手势的事。
    public static readonly StyledProperty<float> DragSensitivityProperty =
        AvaloniaProperty.Register<Previewer, float>(nameof(DragSensitivity), defaultValue: 0.20f);

    // 甩出去之后角速度衰减的时间常数（秒）。一阶滞后的时间常数，0.35 秒下大约一秒停稳。
    public static readonly StyledProperty<float> SpinDampingProperty =
        AvaloniaProperty.Register<Previewer, float>(nameof(SpinDamping), defaultValue: 0.35f);

    // 松手之后过多少秒开始自转（秒）。不是「到点突然起步」：角速度的衰减目标从 0 换成自转速度，
    // 速度本身是连续的，所以衔接处只有加速度跳一下。
    public static readonly StyledProperty<float> SpinIdleDelayProperty =
        AvaloniaProperty.Register<Previewer, float>(nameof(SpinIdleDelay), defaultValue: 1.5f);

    // 自转速度（度/秒），逆时针。8 度下转一圈 45 秒——足够慢，能看清模型的每一面而不晕。
    public static readonly StyledProperty<float> SpinIdleSpeedProperty =
        AvaloniaProperty.Register<Previewer, float>(nameof(SpinIdleSpeed), defaultValue: 8f);

    public float DragSensitivity
    {
        get => GetValue(DragSensitivityProperty);
        set => SetValue(DragSensitivityProperty, value);
    }

    public float SpinDamping
    {
        get => GetValue(SpinDampingProperty);
        set => SetValue(SpinDampingProperty, value);
    }

    public float SpinIdleDelay
    {
        get => GetValue(SpinIdleDelayProperty);
        set => SetValue(SpinIdleDelayProperty, value);
    }

    public float SpinIdleSpeed
    {
        get => GetValue(SpinIdleSpeedProperty);
        set => SetValue(SpinIdleSpeedProperty, value);
    }

    // 展台底面那个光环。四个参数一组（画不画、圆心、半径、底面高度），所以是一次调用而不是四个属性：
    // 半径、圆心与底面高度都来自「当前展示的目标」，分开设会出现「半径已经换了、圆心还是上一个目标的」
    // 那一帧——而那一帧看起来只是「光环陷进去了」。圆心丢了 X/Z 的话光环会钉在世界原点，
    // 模型一挪位置圈和模型就分家，转展台时模型绕着圈外的一个点公转。
    //
    // 它不碰 GPU 资源：光环的网格是半径为 1 的那一份，平移缩放抬升走矩阵，
    // 于是换目标不重建任何东西（R5）。
    public void SetPedestal(bool visible, Vector3 centre, float radius, float baseY)
    {
        Debug.Assert(
            !visible || (float.IsFinite(radius) && radius > 0f && float.IsFinite(baseY)
                && float.IsFinite(centre.X) && float.IsFinite(centre.Y) && float.IsFinite(centre.Z)),
            $"[PREVIEWER][gl.pedestal] 光环参数不合法 visible={visible} centre=({centre}) radius={radius} baseY={baseY}");

        // 同一个值重复设不打桩：宿主可能在每帧的末尾都推一次，而这里要的是「变没变」这件事。
        if (_pedestalVisible == visible && _pedestalRadius == radius && _pedestalBaseY == baseY
            && _pedestalCentre == centre)
        {
            return;
        }

        _pedestalVisible = visible;
        _pedestalCentre = centre;
        _pedestalRadius = radius;
        _pedestalBaseY = baseY;

        Debug.WriteLine(
            $"[PREVIEWER][gl.pedestal.set] visible={visible} centre=({centre.X:F2},{centre.Y:F2},{centre.Z:F2}) " +
            $"radius={radius:F3} baseY={baseY:F3}");
    }

    private bool _pedestalVisible;
    private Vector3 _pedestalCentre;
    private float _pedestalRadius = 1f;
    private float _pedestalBaseY;

    // 拾取高亮框（画不画、哪个方块）。null 是清掉。位置是世界坐标（与网格顶点同一坐标系）。
    // 用 float 而不是 Core 的 Vector3I：Previewer 不引用 Core（分层单向），
    // 方块坐标是整数语义，float 到百万量级都装得下精确值。
    public void SetHighlight(Vector3? blockPosition)
    {
        if (Nullable.Equals(_highlightPosition, blockPosition))
        {
            return;
        }

        _highlightPosition = blockPosition;
        Debug.WriteLine(
            $"[PREVIEWER][gl.highlight.set] " +
            $"block={(blockPosition is { } p ? $"{p.X:F0},{p.Y:F0},{p.Z:F0}" : "null")}");
    }

    private Vector3? _highlightPosition;

    // 屏幕点 → 世界射线。拾取链路的相机侧：与渲染共用同一份 (view * proj) 矩阵求逆，
    // 于是「画面上鼠标指着的那条线」与「拾取问体素数据的那条线」必然是同一条——
    // 两套换算各自为政的话，症状是准星压着 A 却拾到 B，且只在某些视角下出现。
    // 入参用控件 DIP 坐标（与 HoverMoved 一致），宽高也用 DIP：比例与物理像素一致。
    public (Vector3 Origin, Vector3 Direction) PointToRay(Vector point)
    {
        double width = Math.Max(1.0, Bounds.Width);
        double height = Math.Max(1.0, Bounds.Height);
        float ndcX = (float)(2.0 * point.X / width - 1.0);
        float ndcY = (float)(1.0 - 2.0 * point.Y / height);

        // System.Numerics 的投影是右手的：NDC z=-1 是近平面、+1 是远平面。
        // 用 GL 深度那套 [0,1] 会得到两条完全不一样的射线，而且不报错。
        Matrix4x4 viewProjection = _camera.GetViewMatrix() * _camera.GetProjectionMatrix((float)(width / height));

        // Invert 的结果先接出来再断言：Debug.Assert 的调用点在 Release 下整个消失，
        // 但 out 变量的「明确赋值」是语义分析期的事——直接在 Assert 里 out 的话，
        // Release 编译器会认为 inverse 未赋值，恰好是 C# 那条 Conditional 的老坑。
        bool invertible = Matrix4x4.Invert(viewProjection, out Matrix4x4 inverse);
        Debug.Assert(invertible, "[PREVIEWER][pick] 视图投影矩阵不可逆");
        Vector4 nearPoint = Vector4.Transform(new Vector4(ndcX, ndcY, -1f, 1f), inverse);
        Vector4 farPoint = Vector4.Transform(new Vector4(ndcX, ndcY, 1f, 1f), inverse);

        // 反投影 z=-1 得到的是**近平面上**的点，不是相机本身——原点必须显式用相机位置。
        // 方向取近平面点到远平面点的连线：透视下这条线必然穿过相机，方向不受近平面影响。
        Vector3 origin = _camera.Position;
        Vector3 nearWorld = new(nearPoint.X / nearPoint.W, nearPoint.Y / nearPoint.W, nearPoint.Z / nearPoint.W);
        Vector3 farWorld = new(farPoint.X / farPoint.W, farPoint.Y / farPoint.W, farPoint.Z / farPoint.W);
        return (origin, Vector3.Normalize(farWorld - nearWorld));
    }

    // 装填一个有贴图的网格（pos3+normal3+uv2 交错）与它采样的 RGBA 图集。
    // 这是宿主把投影数据送进渲染的唯一口子：网格与图集一次装齐——uv 是按那张图集算出来的，
    // 分两个口子装就会出现「顶点已是新 uv、纹理还是上一张」的一帧，画面上只是颜色错乱一闪。
    // 全部 null 是清空（卸掉模型），清空之后画演示立方体。
    //
    // 这里只拷贝托管副本、不发任何 GL 调用：控件可能还没走到 OnOpenGlInit
    // （宿主在窗口内容加载完就装数据是合法的），上下文在不在不由调用方操心。
    // 那种情况下数据先存在 _pendingMesh 里，init 建好渲染器后推过去。
    public void SetMesh(float[]? vertices, int[]? indices, byte[]? atlasRgba, int atlasWidth, int atlasHeight)
    {
        Debug.Assert(
            Environment.CurrentManagedThreadId == _uiThreadId,
            $"[PREVIEWER][gl.mesh.set] 不在 UI 线程上 thread={Environment.CurrentManagedThreadId} " +
            $"expected={_uiThreadId}");

        if (_meshRenderer is null)
        {
            _pendingMesh = (vertices, indices, atlasRgba, atlasWidth, atlasHeight);
            Debug.WriteLine(
                $"[PREVIEWER][gl.mesh.set] 上下文未初始化，先暂存 " +
                $"vertices={vertices?.Length.ToString() ?? "null"} atlas={atlasWidth}x{atlasHeight}");
            return;
        }

        _meshRenderer.Load(vertices, indices, atlasRgba, atlasWidth, atlasHeight);
    }

    // init 之前到达的装填。四元组与 SetMesh 入参一致；null 表示「暂存的也是清空请求」，
    // 这种请求本来就不需要暂存，所以只会在有真数据时出现。
    private (float[]? Vertices, int[]? Indices, byte[]? Atlas, int Width, int Height)? _pendingMesh;

    private CameraState _camera = CameraState.Default;

    // 相机版本号，单调递增。调试侧靠它判断「这一帧用的相机是否已经校验过」：
    // 相机一变就重验一帧，因为「SetCamera 到底有没有真的驱动渲染」只有画面能证明。
    // 不放进 #if DEBUG：那样 Release 下这个名字会在探针的参数里被引用而定义不存在，
    // 而 [Conditional] 是在语义分析之后才丢掉调用的，编译仍然要过。
    // 一次自增的代价可以忽略，不值得为它换一个只在 Debug 成立的声明。
    private int _cameraVersion;

    // 每次 SetCamera 打一整行（五个向量加六个标量）的话，指针一动就是每秒六十行——
    // 这一条是全项目最重的一处 IO，而它的信息量在相邻两行之间几乎不增。
    // 首条加每 60 条一条：「相机到底有没有被推过来」是个是非题，抽样足够回答。
    // 调用方都在 UI 线程上，计数不用原子。
    private const int CameraSetLogInterval = 60;

    private int _cameraSetLogs;

    public void SetCamera(CameraState camera)
    {
        // 现在没有跨线程调用者：控制器（Phase E/F）和 GL 回调都在 UI 线程上，_uiThreadId 守着。
        // 将来相机若改由别的线程驱动，这里必须换成 Channel：CameraState 是六个字段的结构体，
        // 两个线程同时读写会撕裂，读到的可能是新 Position 配旧 Yaw，
        // 而那表现成「画面偶尔抖一下」——没人会想到是这里。
        Debug.Assert(
            Environment.CurrentManagedThreadId == _uiThreadId,
            $"[PREVIEWER][camera.set] 不在 UI 线程上 thread={Environment.CurrentManagedThreadId} expected={_uiThreadId}");
        Debug.Assert(
            camera.IsValid(out string reason),
            $"[PREVIEWER][camera.set] 相机状态不合法: {reason}");

        // 超范围的 pitch 不在入口夹，只在取用时夹（见 CameraState.ForwardOf），
        // 否则控制器手里的状态和实际生效的就不是一回事，两边会越差越远。这里只留个话。
        if (MathF.Abs(camera.Pitch) > CameraState.MaxPitch)
        {
            Debug.WriteLine(
                $"[PREVIEWER][camera.set] pitch 超范围，取用时会被夹住 pitch={camera.Pitch} max=±{CameraState.MaxPitch}");
        }

        _camera = camera;
        _cameraVersion++;

        if (_cameraSetLogs++ % CameraSetLogInterval == 0)
        {
            Debug.WriteLine(
                $"[PREVIEWER][camera.set] version={_cameraVersion} pos=({camera.Position}) " +
                $"yaw={camera.Yaw:F2} pitch={camera.Pitch:F2} forward=({camera.Forward}) " +
                $"fov={camera.Fov} near={camera.Near} far={camera.Far} note=每{CameraSetLogInterval}条一条");
        }
    }

    // 只有本程序集里的输入适配器调这两个。事件在别处没法触发，
    // 所以是 internal 而不是 public——外部想造一个滚轮事件得先有滚轮。
    internal void RaiseScrolled(float delta)
    {
        // 订阅者数量必须打出来：没人订阅时的行为和「事件根本没触发」一模一样，
        // 而 Phase D 的验收恰好是「能看到事件触发」，这里打出 0 就是答案。
        Debug.WriteLine($"[PREVIEWER][event.scrolled] delta={delta} handlers={CountHandlers(Scrolled)}");
        Scrolled?.Invoke(delta);
    }

    // 看向手势的三条只有起点和终点打桩，中间那条不打：转动一秒几百条，逐条打会把终端冲掉，
    // 而「转了多少」这类信息由控制器在收尾时汇总，它才知道该怎么划段。
    internal void RaiseLookStarted()
    {
        Debug.WriteLine($"[PREVIEWER][event.look] started handlers={CountHandlers(LookStarted)}");
        LookStarted?.Invoke();
    }

    internal void RaiseLookMoved(Vector delta) => LookMoved?.Invoke(delta);

    internal void RaiseLookEnded()
    {
        Debug.WriteLine($"[PREVIEWER][event.look] ended handlers={CountHandlers(LookEnded)}");
        LookEnded?.Invoke();
    }

    internal void RaiseKeyChanged(Key key, bool isDown)
    {
        Debug.WriteLine($"[PREVIEWER][event.key] key={key} down={isDown} handlers={CountHandlers(KeyChanged)}");
        KeyChanged?.Invoke(key, isDown);
    }

    private void RaiseViewportResized(int width, int height, int previousWidth, int previousHeight)
    {
        Debug.WriteLine(
            $"[PREVIEWER][event.resize] size={width}x{height} previous={previousWidth}x{previousHeight} " +
            $"handlers={CountHandlers(ViewportResized)}");
        ViewportResized?.Invoke(width, height);
    }

    private void RaiseTick(double deltaSeconds)
    {
        Tick?.Invoke(deltaSeconds);
    }

    private static int CountHandlers(Delegate? handler) => handler?.GetInvocationList().Length ?? 0;
}
