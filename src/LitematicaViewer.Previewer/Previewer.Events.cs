using System.Diagnostics;
using Avalonia;
using Avalonia.Input;

namespace LitematicaViewer.Previewer;

// 本文件是 Previewer 的对外公开面：七个事件、SetCamera、以及两个导航设置（MoveSpeed /
// LookSensitivity）。Phase D 的清单里这个文件叫 Previewer.Events.cs，后两者一并放这儿——
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
        AvaloniaProperty.Register<Previewer, float>(nameof(MoveSpeed), defaultValue: 1.5f);

    public static readonly StyledProperty<float> LookSensitivityProperty =
        AvaloniaProperty.Register<Previewer, float>(nameof(LookSensitivity), defaultValue: 0.10f);

    // 每秒走多少世界单位（＝方块）。1.5 是每秒一个半方块：渲染器里那个单位立方体就是按
    // 「一个 MC 方块 = 1×1×1 世界单位」画的（MC 里一方块也是一米）。
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
