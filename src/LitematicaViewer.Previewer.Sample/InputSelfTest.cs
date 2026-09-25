using System.Diagnostics;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace LitematicaViewer.Previewer.Sample;

// Phase D 的验收剧本。事件「触发了」这件事光看探针说服力不够——没人订阅时它同样安静，
// 而「安静」和「没接上」在日志里长得一样。所以这里把 Avalonia 的真实路由事件合成出来打进控件，
// 再断言 Previewer 把每一次输入都翻成了自己的事件、而且相机确实换掉了画面。
//
// 合成真事件而不是直接调 Previewer 的内部方法：这样输入适配器那一层（订阅、映射、方向、
// 只取 Y 分量）也在被验的范围里。绕过它，适配器写反了方向这个剧本照样全绿。
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
    private const float ZoomInFactor = 0.8f;
    private const float ZoomOutFactor = 1.25f;

    private readonly Window _window;
    private readonly Previewer _previewer;
    private readonly CameraState _startCamera;

    // 剧本自己记着当前相机。Previewer 不提供 GetCamera（状态的权威在消费侧），
    // 所以想知道「现在相机在哪」的调用方必须自己维护——这里就是第一个这样的调用方。
    private CameraState _camera;

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

    internal InputSelfTest(Window window, Previewer previewer)
    {
        _window = window;
        _previewer = previewer;
        _startCamera = CameraState.Default;
        _camera = _startCamera;
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

        Debug.WriteLine(
            $"[SAMPLE][selftest.summary] ticks={_ticks} scrolled={_scrolled} wheelIn={_wheelIn} wheelOut={_wheelOut} " +
            $"key={_keyEvents} keyDown={_sawKeyDown} keyUp={_sawKeyUp} resizes={_resizes} " +
            $"lastViewport={_lastViewport.Width}x{_lastViewport.Height} " +
            $"expected={expectedWidth}x{expectedHeight} cameraSets={_cameraSets} elapsed={_elapsed:F2}s");

        Debug.Assert(_ticks > 0, "[SAMPLE][selftest] Tick 一次都没触发");
        Debug.Assert(_scrolled == 3, $"[SAMPLE][selftest] Scrolled 触发次数不对 scrolled={_scrolled} expected=3");
        Debug.Assert(
            _wheelIn == 2 && _wheelOut == 1,
            $"[SAMPLE][selftest] 滚轮方向没走全 in={_wheelIn} out={_wheelOut} expected=2/1");
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

        // 两次旋转 + 三次滚轮。首帧那个相机是 Previewer 的默认值，没经过 SetCamera，不算在内。
        Debug.Assert(
            _cameraSets >= 5,
            $"[SAMPLE][selftest] 相机切换次数不对 cameraSets={_cameraSets} expected=>=5");

        Debug.WriteLine("[SAMPLE][selftest.summary] 四个事件都至少走通一次，且相机切了画面");
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
                // 滚轮走完整条链路：合成事件 -> 适配器 -> Scrolled -> 这里的订阅者改相机。
                // 三次而不是一上一下：一来一回正好抵消，相机回到原位，
                // 「相机一变就验一帧」的那一帧看到的还是上一张画面，等于什么都没验。
                // 净效果是推近到 0.8 倍距离，画面确实变了。
                RaiseWheel(1f);
                RaiseWheel(1f);
                RaiseWheel(-1f);
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

        // 沿视线推拉就是按比例缩放位置向量：相机一直看向原点，缩放后仍然看向原点，
        // 所以质心还是落在画面中心，画面校验的那几条继续有效。
        // 比例限制在 [0.8, 1.25]，免得一步就把相机推进立方体里或者推出画面外。
        CameraState camera = _camera;
        float factor = delta > 0 ? ZoomInFactor : ZoomOutFactor;
        ApplyCamera(camera with { Position = camera.Position * factor });
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

    private void ApplyCamera(CameraState camera)
    {
        _camera = camera;
        _cameraSets++;
        _previewer.SetCamera(camera);
    }

    // 合成按键。构造的是真实的路由事件，走的是控件上订阅的那套处理器，
    // 所以适配器里「哪个事件映射成 down、哪个映射成 up」也在被验。
    private void RaiseKey(Key key, bool down)
    {
        KeyEventArgs args = new()
        {
            RoutedEvent = down ? InputElement.KeyDownEvent : InputElement.KeyUpEvent,
            Key = key,
        };

        _previewer.RaiseEvent(args);
    }

    // 合成滚轮。rootVisual 传的就是控件自己，于是事件的坐标系与控件一致，
    // 适配器里读 e.Delta 不依赖任何窗口状态。
    private void RaiseWheel(float delta)
    {
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
    }
}
