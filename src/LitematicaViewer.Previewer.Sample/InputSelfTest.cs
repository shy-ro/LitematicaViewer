using System.Diagnostics;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace LitematicaViewer.Previewer.Sample;

// 验收剧本。Phase D 立起来的是「输入能进来、相机能出去」，Phase E 起多一层：
// 滚轮不再由剧本自己改相机，而是交给 ScrollZoomController 与 CameraModel——剧本退到旁观者的位置。
//
// 让剧本继续自己改相机的话，控制器整个删掉剧本照样全绿，而那恰恰是这一相位要验的东西。
//
// 「事件触发了」光看探针说服力不够：没人订阅时它同样安静，而「安静」和「没接上」在日志里长得一样。
// 所以这里合成 Avalonia 的真实路由事件打进控件，再断言 Previewer 把每一次输入都翻成了自己的事件。
// 合成真事件而不是直接调内部方法：输入适配器那一层（订阅、映射、方向、只取 Y 分量）也在被验的范围里。
internal sealed class InputSelfTest
{
    // 剧本按秒推进而不是按帧：帧率随机器和窗口大小变，验收脚本不该跟着变。
    private const double RotateAt = 0.4;
    private const double RotateBackAt = 0.8;
    private const double WheelAt = 1.2;
    private const double KeyDownAt = 1.6;
    private const double KeyUpAt = 2.0;
    private const double ResizeAt = 2.4;

    // 每步按顺序编号，与 StepTimes 一一对应。
    private const int StepRotate = 0;
    private const int StepRotateBack = 1;
    private const int StepWheel = 2;
    private const int StepKeyDown = 3;
    private const int StepKeyUp = 4;
    private const int StepResize = 5;

    private static readonly double[] StepTimes =
        [RotateAt, RotateBackAt, WheelAt, KeyDownAt, KeyUpAt, ResizeAt];

    private const double ResizeDelta = 160;

    // 滚轮那三步：+1、+1、-1。净效果是正向一档，也就是推近到 0.8 倍距离。
    private const float WheelIn = 1f;
    private const float WheelOut = -1f;

    private readonly Window _window;
    private readonly Previewer _previewer;

    // 相机状态的权威在模型那边，剧本不再自己存一份「当前相机」：
    // 存了它就会在某个时刻和真正的权威不一致，而那表现成画面校验偶尔红一次，很难归因。
    private readonly CameraModel _camera;

    private readonly CameraState _startCamera;

    private double _elapsed;
    private int _nextStep;
    private int _ticks;
    private int _scrolled;
    private int _wheelIn;
    private int _wheelOut;
    private int _keyEvents;
    private bool _sawKeyDown;
    private bool _sawKeyUp;
    private int _resizes;
    private (int Width, int Height) _lastViewport;
    private int _cameraSets;

    // 滚轮前后的距离。前面那个由剧本记下，后面那个从模型读回来对——中间隔着一整个控制器。
    private float _distanceBeforeZoom;
    private float _expectedDistanceAfterZoom;

    internal InputSelfTest(Window window, Previewer previewer, CameraModel camera)
    {
        _window = window;
        _previewer = previewer;
        _camera = camera;
        _startCamera = CameraState.Default;
    }

    internal void Attach()
    {
        _previewer.Tick += OnTick;
        _previewer.Scrolled += OnScrolled;
        _previewer.KeyChanged += OnKeyChanged;
        _previewer.ViewportResized += OnViewportResized;

        Debug.WriteLine(
            $"[SAMPLE][selftest.attach] steps={StepTimes.Length} last={StepTimes[^1]}s " +
            $"expected=窗口至少开 {StepTimes[^1] + 0.5:F1}s 剧本才跑得完");
    }

    internal void Report()
    {
        double scaling = _window.RenderScaling;
        int expectedWidth = (int)Math.Round(_window.ClientSize.Width * scaling);
        int expectedHeight = (int)Math.Round(_window.ClientSize.Height * scaling);

        float distance = _camera.Distance;

        Debug.WriteLine(
            $"[SAMPLE][selftest.summary] ticks={_ticks} scrolled={_scrolled} wheelIn={_wheelIn} wheelOut={_wheelOut} " +
            $"key={_keyEvents} keyDown={_sawKeyDown} keyUp={_sawKeyUp} resizes={_resizes} " +
            $"lastViewport={_lastViewport.Width}x{_lastViewport.Height} " +
            $"expected={expectedWidth}x{expectedHeight} cameraSets={_cameraSets} " +
            $"distance={_distanceBeforeZoom:F4}->{distance:F4} elapsed={_elapsed:F2}s");

        Debug.Assert(_ticks > 0, "[SAMPLE][selftest] Tick 一次都没触发");

        // 计数只断言「至少」。验收窗口是一个有焦点、真实存在的窗口，用户在上面滚一下鼠标
        // 就会多出几次——那是外部输入，既不说明接线错了，也不说明接线对了。
        // 「恰好一次」那一条在 RaiseWheel / RaiseKey 里同步验，外部输入插不进那一段。
        Debug.Assert(
            _scrolled >= 3,
            $"[SAMPLE][selftest] Scrolled 少触发 scrolled={_scrolled} expected=>=3");
        Debug.Assert(
            _wheelIn >= 2 && _wheelOut >= 1,
            $"[SAMPLE][selftest] 滚轮方向没走全 in={_wheelIn} out={_wheelOut} expected=>=2/>=1");
        Debug.Assert(
            _sawKeyDown && _sawKeyUp,
            $"[SAMPLE][selftest] 按键没有成对到达 down={_sawKeyDown} up={_sawKeyUp}");

        // 一次是首帧（0x0 变成实际尺寸），一次是剧本里把窗口拉宽。
        // 只断言 >=1 的话，把窗口缩放那一步删掉也能过，那就白验了。
        Debug.Assert(
            _resizes >= 2,
            $"[SAMPLE][selftest] ViewportResized 只发了 {_resizes} 次，期望首帧与缩放各一次");
        Debug.Assert(
            _lastViewport == (expectedWidth, expectedHeight),
            $"[SAMPLE][selftest] 最后一次视口尺寸与窗口不符 last={_lastViewport} " +
            $"expected=({expectedWidth},{expectedHeight})");

        // 剧本自己摆的视角只有两次旋转。画面上还有滚动缩放，但它不经过这里——
        // 它走的是 ScrollZoomController，正是这一相位要验的那条链路。
        // 外部输入也改不了这个计数：只有 ApplyCamera 会动它。
        Debug.Assert(
            _cameraSets == 2,
            $"[SAMPLE][selftest] 剧本摆视角的次数不对 cameraSets={_cameraSets} expected=2");

        Debug.WriteLine("[SAMPLE][selftest.summary] 四个事件都至少走通一次，滚轮经由控制器改了相机，画面随之切换");
    }

    private void OnTick(double delta)
    {
        _ticks++;
        _elapsed += delta;

        // 一帧最多推进一步，而不是 while 把落下的步全补上：连推几步的话，
        // 中间那几次 SetCamera 根本没有帧用上它，而「相机一变就验一帧」正是靠
        // 每步之间至少隔一帧才成立。卡顿时剧本整体顺延，验收结论不受影响。
        if (_nextStep < StepTimes.Length && _elapsed >= StepTimes[_nextStep])
        {
            int step = _nextStep++;
            Debug.WriteLine($"[SAMPLE][selftest.step] step={step} elapsed={_elapsed:F2}s tick={_ticks}");
            RunStep(step);
        }
    }

    private void RunStep(int step)
    {
        switch (step)
        {
            case StepRotate:
                RotateAroundCube(90f);
                break;

            case StepRotateBack:
                RotateAroundCube(180f);
                break;

            case StepWheel:
                // 滚轮走完整条链路：合成事件 -> 适配器 -> Scrolled -> 控制器 -> 模型 -> SetCamera。
                // 三次而不是一上一下：一来一回正好抵消，相机回到原位，
                // 「相机一变就验一帧」的那一帧看到的还是上一张画面，等于什么都没验。
                // 净效果是推近到 0.8 倍距离，画面确实变了。
                _distanceBeforeZoom = _camera.Distance;

                // 期望值按「净一档」算，而不是把每一步的距离记下来逐个比：
                // 逐个比等于把控制器的实现抄一遍，抄错了也照样通过。
                _expectedDistanceAfterZoom = _distanceBeforeZoom * CameraModel.ZoomRatioPerStep;

                RaiseWheel(WheelIn);
                RaiseWheel(WheelIn);
                RaiseWheel(WheelOut);

                // 这一条是 Phase E 的核心：三次滚轮穿过
                // 事件 -> 适配器 -> ScrollZoomController -> CameraModel，最后落在模型的距离上。
                // 控制器没接上、订阅漏了、方向接反了、比例写错了，几种错法这里各红一次。
                // 容差 1e-3 对浮点足够，而算错一档的偏差是 20% 这个量级，不会混。
                //
                // 就地验而不是留到 Report：RaiseWheel 是同步的，这一行跑到的时候这三次已经走完，
                // 此刻的距离就是脚本那三次的结果。留到 Report 再比的话，中间任何一次真实滚轮
                // 都会把它改掉——而那会让一条正确的实现红掉，比漏报还糟。
                float afterWheels = _camera.Distance;
                Debug.Assert(
                    MathF.Abs(afterWheels - _expectedDistanceAfterZoom) < 1e-3f,
                    $"[SAMPLE][selftest] 三次滚轮之后的距离不对 actual={afterWheels:F4} " +
                    $"expected={_expectedDistanceAfterZoom:F4}，说明滚轮没有走到模型上");
                Debug.Assert(
                    afterWheels < _distanceBeforeZoom,
                    $"[SAMPLE][selftest] 正向滚轮没有把相机拉近 before={_distanceBeforeZoom:F4} " +
                    $"after={afterWheels:F4}");
                break;

            case StepKeyDown:
                RaiseKey(Key.W, down: true);
                break;

            case StepKeyUp:
                RaiseKey(Key.W, down: false);
                break;

            case StepResize:
                _window.Width += ResizeDelta;
                break;

            default:
                Debug.Fail($"[SAMPLE][selftest.step] 未知的步骤 step={step}");
                break;
        }
    }

    // 转的是相机位置而不是只改 yaw：绕 +Y 转位置再 LookAt 原点，能保证相机仍然看向立方体中心，
    // 于是「轮廓质心落在画面中心」那条断言继续成立，验的就只剩可见面集合的变化。
    // 只改 yaw 的话相机会看向别处，画面校验会被一堆无关的原因搞红。
    private void RotateAroundCube(float degrees)
    {
        Vector3 position = Vector3.Transform(
            _startCamera.Position,
            Matrix4x4.CreateRotationY(float.DegreesToRadians(degrees)));

        ApplyCamera(CameraState.LookAt(
            position,
            Vector3.Zero,
            _startCamera.Fov,
            _startCamera.Near,
            _startCamera.Far));
    }

    private void OnScrolled(float delta)
    {
        _scrolled++;

        if (delta > 0)
        {
            _wheelIn++;
        }
        else if (delta < 0)
        {
            _wheelOut++;
        }

        // 相机不在这里改。滚轮改相机是 ScrollZoomController 的职责，剧本只是它的旁观者；
        // 顺手在这里也改一次的话，控制器坏掉了剧本照样全绿。
        // 这条日志的顺序也不承重：控制器和剧本都订阅 Scrolled，谁先谁后由订阅顺序决定，
        // 所以这里只报事实，结论留到 Report 里出。
        Debug.WriteLine(
            $"[SAMPLE][selftest.scroll] delta={delta} distance={_camera.Distance:F4} " +
            $"expected={_expectedDistanceAfterZoom:F4}");
    }

    private void OnKeyChanged(Key key, bool isDown)
    {
        _keyEvents++;

        if (key != Key.W)
        {
            // 别的键也可能是系统塞进来的（焦点切换之类），只记不判，免得把无关事件算进来。
            Debug.WriteLine($"[SAMPLE][selftest.key] 收到剧本之外的按键 key={key} down={isDown}");
            return;
        }

        _sawKeyDown |= isDown;
        _sawKeyUp |= !isDown;
    }

    private void OnViewportResized(int width, int height)
    {
        _resizes++;
        _lastViewport = (width, height);
    }

    // 摆视角走模型，不直接灌给 Previewer：否则模型手里的相机和画面上那个是两回事，
    // 之后滚轮一滚就会从模型记得的旧朝向重新出发，画面跳一下。
    private void ApplyCamera(CameraState camera)
    {
        _cameraSets++;
        _camera.Reset(camera);
        _previewer.SetCamera(_camera.Camera);
    }

    // 合成按键。构造的是真实的路由事件，走的是控件上订阅的那套处理器，
    // 所以适配器里「哪个事件映射成 down、哪个映射成 up」也在被验。
    private void RaiseKey(Key key, bool down)
    {
        int before = _keyEvents;

        KeyEventArgs args = new()
        {
            RoutedEvent = down ? InputElement.KeyDownEvent : InputElement.KeyUpEvent,
            Key = key,
        };

        _previewer.RaiseEvent(args);

        // RaiseEvent 是同步的，跑到这一行时这一次已经转发完，所以可以就地验「恰好一次」。
        // 留到 Report 里数总数的话，用户按一下键就分不清多出来的是他的、还是同一次被转发了两次。
        Debug.Assert(
            _keyEvents == before + 1,
            $"[SAMPLE][selftest] 一次合成按键应该恰好转发一次 KeyChanged before={before} after={_keyEvents}");
    }

    // 合成滚轮。rootVisual 传的就是控件自己，于是事件的坐标系与控件一致，
    // 适配器里读 e.Delta 不依赖任何窗口状态。
    private void RaiseWheel(float delta)
    {
        int before = _scrolled;

        PointerWheelEventArgs args = new(
            _previewer,
            new Pointer(1, PointerType.Mouse, isPrimary: true),
            _previewer,
            default,
            0UL,
            default,
            KeyModifiers.None,
            // 写全名：System.Numerics 里也有个 Vector（静态类），简单名会和 Avalonia.Vector 撞。
            new Avalonia.Vector(0, delta));

        _previewer.RaiseEvent(args);

        Debug.Assert(
            _scrolled == before + 1,
            $"[SAMPLE][selftest] 一次合成滚轮应该恰好转发一次 Scrolled before={before} after={_scrolled}");
    }
}
