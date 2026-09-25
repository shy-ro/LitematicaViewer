using System.Diagnostics;
using System.Numerics;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace LitematicaViewer.Previewer.Sample;

// 左侧那块「调参 + 读数」。
//
// 读数不在 Tick 里逐帧刷：侧边栏和视口在同一行，文本一变就要重新排版，而逐帧改文本等于
// 每帧多一次布局（fps 这个数每秒变四次已经比人眼快了）。
//
// 帧率用 Previewer 给的 delta 累加而不是自己起一块秒表：那是渲染与控制器共用的同一条时间轴，
// 另起一条表会让「帧时」里混进两块表之间的偏差，而它看起来像掉帧。
internal sealed class Sidebar : IDisposable
{
    private const double StatsIntervalSeconds = 0.25;

    private readonly Previewer _previewer;
    private readonly CameraModel _camera;
    private readonly Slider _moveSpeed;
    private readonly Slider _lookSensitivity;
    private readonly TextBlock _moveSpeedText;
    private readonly TextBlock _lookSensitivityText;
    private readonly TextBlock _fpsText;
    private readonly TextBlock _viewportText;
    private readonly TextBlock _positionText;
    private readonly TextBlock _lookText;
    private readonly TextBlock _forwardText;
    private readonly TextBlock _distanceText;

    private double _windowSeconds;
    private int _windowFrames;
    private long _totalFrames;
    private int _statsLogs;
    private bool _disposed;

    // 控件用名字找，不走一长串构造参数：十来个控件的参数表没有任何一个调用点读得懂，
    // 而名字写错在 Debug 下会当场断言炸掉，比「某一行永远是空的」好查。
    internal Sidebar(Window window, Previewer previewer, CameraModel camera)
    {
        _previewer = previewer;
        _camera = camera;

        _moveSpeed = Required<Slider>(window, "MoveSpeedSlider");
        _lookSensitivity = Required<Slider>(window, "LookSensitivitySlider");
        _moveSpeedText = Required<TextBlock>(window, "MoveSpeedText");
        _lookSensitivityText = Required<TextBlock>(window, "LookSensitivityText");
        _fpsText = Required<TextBlock>(window, "FpsText");
        _viewportText = Required<TextBlock>(window, "ViewportText");
        _positionText = Required<TextBlock>(window, "PositionText");
        _lookText = Required<TextBlock>(window, "LookText");
        _forwardText = Required<TextBlock>(window, "ForwardText");
        _distanceText = Required<TextBlock>(window, "DistanceText");

        // 先接上事件再把初值推给滑块，顺序不能反：这一次赋值会走一遍 OnMoveSpeedChanged 那条路
        // （值没变，写回属性是无害的），于是每一次启动的日志里都留下两行「滑块真的接上了」。
        // 反过来先赋值再接事件的话，接线断没断在日志上完全看不出来——拖了没反应与没拖过
        // 长得一模一样。
        //
        // 用事件而不是 XAML 绑定：绑定的方向性与回写时机由控件注册的默认模式和模板决定，
        // 光读那一行 XAML 看不出「拖了之后值到底写没写进控件」，而这里要的只有那一件事。
        _moveSpeed.ValueChanged += OnMoveSpeedChanged;
        _lookSensitivity.ValueChanged += OnLookSensitivityChanged;

        // 初值从控件属性取，不在 XAML 里再写一份：属性是权威，写两份就有两个默认值，
        // 而它们分叉时「程序里用的是哪个」从界面上看不出来。
        //
        // 滑块会把 Value 夹进自己的区间：区间盖不住属性值时夹取的结果会被回写进属性，
        // 那是一次静默的改值（XAML 里写 20 而区间到 8，实际生效的是 8）。
        // 所以两侧的默认值都落在区间内（1.5 ∈ [0.1,8]、0.10 ∈ [0.01,0.6]），
        // 而真有越界时那两行日志记着改成了多少。
        _moveSpeed.Value = _previewer.MoveSpeed;
        _lookSensitivity.Value = _previewer.LookSensitivity;

        _previewer.Tick += OnTick;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _previewer.Tick -= OnTick;
        _moveSpeed.ValueChanged -= OnMoveSpeedChanged;
        _lookSensitivity.ValueChanged -= OnLookSensitivityChanged;
    }

    private static T Required<T>(Window window, string name)
        where T : Control
    {
        T? control = window.FindControl<T>(name);
        Debug.Assert(
            control is not null,
            $"[SAMPLE][sidebar] 找不到控件 name={name} note=MainWindow.axaml 里的名字改过或者那一行被删了");
        return control!;
    }

    // 拖一次滑块几十条，但那是人手速率而不是帧速率（滑块不抢焦点，也没有键盘操作），
    // 所以不需要节流：这一行正是「滑块真的接上了」唯一的证据。
    private void OnMoveSpeedChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        _previewer.MoveSpeed = (float)e.NewValue;
        Debug.WriteLine($"[SAMPLE][sidebar.move] moveSpeed={_previewer.MoveSpeed}");
    }

    private void OnLookSensitivityChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        _previewer.LookSensitivity = (float)e.NewValue;
        Debug.WriteLine($"[SAMPLE][sidebar.look] lookSensitivity={_previewer.LookSensitivity}");
    }

    private void OnTick(double delta)
    {
        _windowSeconds += delta;
        _windowFrames++;
        _totalFrames++;

        if (_windowSeconds < StatsIntervalSeconds)
        {
            return;
        }

        double fps = _windowSeconds > 0 ? _windowFrames / _windowSeconds : 0;
        double frameMilliseconds = _windowFrames > 0 ? _windowSeconds * 1000 / _windowFrames : 0;

        _windowSeconds = 0;
        _windowFrames = 0;

        // 相机状态来自模型（它是权威），视口尺寸来自控件自己的 Bounds。
        // 两边都不是「侧边栏自己记一份」——记一份就会在某一刻和真正的权威不一致，
        // 而那时侧边栏显示的数字恰好是最不该信的那个。
        CameraState camera = _camera.Camera;
        Vector3 position = camera.Position;
        Vector3 forward = camera.Forward;

        Set(_fpsText, $"fps {fps:F1}  帧时 {frameMilliseconds:F1}ms  共 {_totalFrames} 帧");
        Set(_viewportText, $"视口 {BoundsText()} 缩放 {Scaling():F2} → {PixelText()}");
        Set(_positionText, $"位置 ({position.X:F3}, {position.Y:F3}, {position.Z:F3})");
        Set(_lookText, $"朝向 yaw {camera.Yaw:F2}  pitch {camera.Pitch:F2}");
        Set(_forwardText, $"视线 ({forward.X:F3}, {forward.Y:F3}, {forward.Z:F3})");
        Set(_distanceText, $"距离 {_camera.Distance:F4} 方块");
        Set(_moveSpeedText, $"移速 {_previewer.MoveSpeed:F2} 方块/秒");
        Set(_lookSensitivityText, $"灵敏度 {_previewer.LookSensitivity:F3} 度/DIP");

        // 只打第一条：整流刷新每秒四次，逐条打会把终端冲掉，而这条要回答的是
        // 「刷新这条链（Tick -> 攒计数 -> 写文本）到底跑了没有」——一次就够了。
        // 空白的侧边栏和「程序没跑起来」在界面上分不开，所以这句话得留在日志里。
        if (_statsLogs++ == 0)
        {
            Debug.WriteLine(
                $"[SAMPLE][sidebar.stats] 侧边栏开始刷新（只打这一条）fps={fps:F1} frameMs={frameMilliseconds:F1} " +
                $"pos=({position}) yaw={camera.Yaw:F2} pitch={camera.Pitch:F2} distance={_camera.Distance:F4} " +
                $"viewport={_previewer.Bounds.Width:F0}x{_previewer.Bounds.Height:F0} scaling={Scaling():F2}");
        }
    }

    // 物理像素那一栏与 Previewer 里算视口的算式逐字相同（DIP × RenderScaling 再四舍五入）：
    // 侧边栏显示的尺寸与渲染实际用的尺寸必须是同一个数，否则它作为参照系就没用了。
    private double Scaling() => TopLevel.GetTopLevel(_previewer)?.RenderScaling ?? 1.0;

    private string BoundsText() => $"{_previewer.Bounds.Width:F0}x{_previewer.Bounds.Height:F0} DIP";

    private string PixelText()
    {
        double scaling = Scaling();
        int width = Math.Max(1, (int)Math.Round(_previewer.Bounds.Width * scaling));
        int height = Math.Max(1, (int)Math.Round(_previewer.Bounds.Height * scaling));
        return $"{width}x{height} px";
    }

    // 只在文本真的变了的时候写。同一个字符串重复赋值在 Avalonia 里会走到属性相等性比较，
    // 但那之后仍然有一次布局失效的传播——每秒四次乘以八行，不必要。
    private static void Set(TextBlock block, string text)
    {
        if (!string.Equals(block.Text, text, StringComparison.Ordinal))
        {
            block.Text = text;
        }
    }
}
