using System.Diagnostics;
using Avalonia;
using Avalonia.Input;

namespace LitematicaViewer.Previewer;

// 本文件是 Previewer 的对外公开面：四个事件加 SetCamera。Phase D 的清单里这个文件叫
// Previewer.Events.cs，相机入口一并放这儿——它们都是同一个东西：Previewer 允许外界碰的表面。
//
// 没有 GetCamera，也没有任何读回相机状态的口子：外部想跟踪当前相机，自己在控制器里维护。
// Previewer 负责画，不是状态的权威持有者，开了这个口子就会有人拿它当状态源，
// 然后在某一帧上读到和控制器不一致的相机。
public partial class Previewer
{
    // 滚轮增量。正负方向不由 Previewer 约定：它只把原始增量递出去，
    // 「向上滚是拉近还是推远」是控制器的事，将来改手感不必动这里。
    public event Action<float>? Scrolled;

    // 拖拽手势。位置是控件坐标（DIP，不是物理像素）。
    //
    // 为什么给的是「一次拖拽」而不是「左键按下了」：按哪个键起拖是平台绑定，
    // 那属于输入适配器；而「按下之后指针移动了多少」是手势——差值要在能记住上一次位置的地方算，
    // 那是控制器，只有它知道拖多远算转多少度。这里只把起点、过程和终点报出来。
    //
    // 起点的那个位置不产生旋转（只有一次位置就没有「差」），它的用处是定下参照点——
    // 少了它，第一次移动会把「从上次拖拽留下的位置」到这里算成一段距离，画面上是一次凭空的跳转。
    public event Action<Point>? DragStarted;

    public event Action<Point>? DragMoved;

    public event Action? DragEnded;

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

    private CameraState _camera = CameraState.Default;

    // 相机版本号，单调递增。调试侧靠它判断「这一帧用的相机是否已经校验过」：
    // 相机一变就重验一帧，因为「SetCamera 到底有没有真的驱动渲染」只有画面能证明。
    // 不放进 #if DEBUG：那样 Release 下这个名字会在探针的参数里被引用而定义不存在，
    // 而 [Conditional] 是在语义分析之后才丢掉调用的，编译仍然要过。
    // 一次自增的代价可以忽略，不值得为它换一个只在 Debug 成立的声明。
    private int _cameraVersion;

    // 每次 SetCamera 打一整行（五个向量加六个标量）的话，拖拽时就是每秒六十行——
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

    // 拖拽的三条只有起点和终点打桩，中间那条不打：移动一秒几百条，逐条打会把终端冲掉，
    // 而「指针从哪到了哪」这类信息在两条日志之间本来就看得出来。
    internal void RaiseDragStarted(Point position)
    {
        Debug.WriteLine(
            $"[PREVIEWER][event.drag] started pos=({position.X:F0},{position.Y:F0}) " +
            $"handlers={CountHandlers(DragStarted)}");
        DragStarted?.Invoke(position);
    }

    internal void RaiseDragMoved(Point position) => DragMoved?.Invoke(position);

    internal void RaiseDragEnded()
    {
        Debug.WriteLine($"[PREVIEWER][event.drag] ended handlers={CountHandlers(DragEnded)}");
        DragEnded?.Invoke();
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
