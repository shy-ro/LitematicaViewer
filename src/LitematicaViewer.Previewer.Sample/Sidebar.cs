using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;

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
    private readonly CameraModel _camera;
    private readonly TextBlock _distanceText;
    private readonly Slider _dragSensitivity;
    private readonly TextBlock _dragSensitivityText;
    private readonly TextBlock _fileText;
    private readonly TextBlock _forwardText;
    private readonly TextBlock _fpsText;
    private readonly StackPanel _freeLookGroup;

    private readonly IViewModeHost _host;
    private readonly Slider _lookSensitivity;
    private readonly TextBlock _lookSensitivityText;
    private readonly TextBlock _lookText;
    private readonly Button _modeButton;
    private readonly Slider _moveSpeed;
    private readonly TextBlock _moveSpeedText;
    private readonly Button _nextTarget;
    private readonly TextBlock _pickText;
    private readonly TextBlock _positionText;
    private readonly Previewer _previewer;
    private readonly Button _previousTarget;
    private readonly StackPanel _showcaseGroup;
    private readonly TextBlock _showcaseTargetText;
    private readonly Slider _spinDamping;
    private readonly TextBlock _spinDampingText;
    private readonly Slider _spinIdleDelay;
    private readonly TextBlock _spinIdleDelayText;
    private readonly Slider _spinIdleSpeed;
    private readonly TextBlock _spinIdleSpeedText;
    private readonly TextBlock _viewportText;
    private bool _disposed;
    private int _modeLogs;
    private int _statsLogs;
    private long _totalFrames;
    private int _windowFrames;

    private double _windowSeconds;

    // 控件用名字找，不走一长串构造参数：十来个控件的参数表没有任何一个调用点读得懂，
    // 而名字写错在 Debug 下会当场断言炸掉，比「某一行永远是空的」好查。
    internal Sidebar(IViewModeHost host, Window window, Previewer previewer, CameraModel camera)
    {
        _host = host;
        _previewer = previewer;
        _camera = camera;

        _freeLookGroup = Required<StackPanel>(window, "FreeLookGroup");
        _showcaseGroup = Required<StackPanel>(window, "ShowcaseGroup");
        _modeButton = Required<Button>(window, "ModeButton");
        _previousTarget = Required<Button>(window, "PreviousTargetButton");
        _nextTarget = Required<Button>(window, "NextTargetButton");
        _moveSpeed = Required<Slider>(window, "MoveSpeedSlider");
        _lookSensitivity = Required<Slider>(window, "LookSensitivitySlider");
        _dragSensitivity = Required<Slider>(window, "DragSensitivitySlider");
        _spinDamping = Required<Slider>(window, "SpinDampingSlider");
        _spinIdleDelay = Required<Slider>(window, "SpinIdleDelaySlider");
        _spinIdleSpeed = Required<Slider>(window, "SpinIdleSpeedSlider");
        _moveSpeedText = Required<TextBlock>(window, "MoveSpeedText");
        _lookSensitivityText = Required<TextBlock>(window, "LookSensitivityText");
        _showcaseTargetText = Required<TextBlock>(window, "ShowcaseTargetText");
        _dragSensitivityText = Required<TextBlock>(window, "DragSensitivityText");
        _spinDampingText = Required<TextBlock>(window, "SpinDampingText");
        _spinIdleDelayText = Required<TextBlock>(window, "SpinIdleDelayText");
        _spinIdleSpeedText = Required<TextBlock>(window, "SpinIdleSpeedText");
        _fpsText = Required<TextBlock>(window, "FpsText");
        _viewportText = Required<TextBlock>(window, "ViewportText");
        _fileText = Required<TextBlock>(window, "FileText");
        _pickText = Required<TextBlock>(window, "PickText");
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
        _dragSensitivity.ValueChanged += OnDragSensitivityChanged;
        _spinDamping.ValueChanged += OnSpinDampingChanged;
        _spinIdleDelay.ValueChanged += OnSpinIdleDelayChanged;
        _spinIdleSpeed.ValueChanged += OnSpinIdleSpeedChanged;

        // 按钮的 Click 与滑块的 ValueChanged 不是一回事：Click 是「这一次按下被认成了一次点击」，
        // 由模板里的按钮部分在抬起时判出来。接线断了的话表现是「按了没反应」，
        // 而下面那一条日志是它唯一的证据（按下本身有 sidebar.focus 那一行，所以分得开）。
        _modeButton.Click += OnModeButtonClick;
        _previousTarget.Click += OnPreviousTargetClick;
        _nextTarget.Click += OnNextTargetClick;

        // 初值从控件属性取，不在 XAML 里再写一份：属性是权威，写两份就有两个默认值，
        // 而它们分叉时「程序里用的是哪个」从界面上看不出来。
        //
        // 滑块会把 Value 夹进自己的区间：区间盖不住属性值时夹取的结果会被回写进属性，
        // 那是一次静默的改值（XAML 里写 20 而区间到 8，实际生效的是 8）。
        // 所以两侧的默认值都落在区间内（5 ∈ [0.1,8]、0.10 ∈ [0.01,0.6]、0.20 ∈ [0.05,0.8]、
        // 0.35 ∈ [0.05,1.5]、1.5 ∈ [0,5]、8 ∈ [0,30]），而真有越界时那几行日志记着改成了多少。
        _moveSpeed.Value = _previewer.MoveSpeed;
        _lookSensitivity.Value = _previewer.LookSensitivity;
        _dragSensitivity.Value = _previewer.DragSensitivity;
        _spinDamping.Value = _previewer.SpinDamping;
        _spinIdleDelay.Value = _previewer.SpinIdleDelay;
        _spinIdleSpeed.Value = _previewer.SpinIdleSpeed;

        // 初值推完再摆显隐。反过来的话，那几行「滑块真的接上了」会写在一个还没显示出来的组里，
        // 拖起来才发现区间不对时，日志上看不出是初值推错了还是根本没推。
        SetMode(_host.Mode);

        _previewer.Tick += OnTick;
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _previewer.Tick -= OnTick;
        _moveSpeed.ValueChanged -= OnMoveSpeedChanged;
        _lookSensitivity.ValueChanged -= OnLookSensitivityChanged;
        _dragSensitivity.ValueChanged -= OnDragSensitivityChanged;
        _spinDamping.ValueChanged -= OnSpinDampingChanged;
        _spinIdleDelay.ValueChanged -= OnSpinIdleDelayChanged;
        _spinIdleSpeed.ValueChanged -= OnSpinIdleSpeedChanged;
        _modeButton.Click -= OnModeButtonClick;
        _previousTarget.Click -= OnPreviousTargetClick;
        _nextTarget.Click -= OnNextTargetClick;
    }

    // 按模式整块显隐。写在侧边栏里而不是宿主的 XAML 里：模式一变这两块要一起动，
    // 分成两处的话「切了模式但侧边栏没跟上」是一类只看得见一部分的症状。
    internal void SetMode(ViewMode mode)
    {
        var showcase = mode == ViewMode.Showcase;

        // 赋值前先比一次：TextBlock / Button 的 Content 重复赋值会走一遍属性相等性比较，
        // 而 IsVisible 同样会向下传播一次失效——切一次模式而已，只有真的变了才写。
        if (_freeLookGroup.IsVisible == showcase) _freeLookGroup.IsVisible = !showcase;

        if (_showcaseGroup.IsVisible != showcase) _showcaseGroup.IsVisible = showcase;

        var caption = showcase
            ? "模式：展台（点这里或按 1 切回自由视角）"
            : "模式：自由视角（点这里或按 2 切展台）";

        if (!string.Equals(_modeButton.Content as string, caption, StringComparison.Ordinal))
            _modeButton.Content = caption;

        // 只打前两次：这条要回答的是「切换这条链走到了侧边栏」，
        // 一次就够，而后面的每一次都会和上面那两行成对出现，多打只是噪声。
        if (_modeLogs++ < 2)
            Debug.WriteLine(
                $"[SAMPLE][sidebar.mode] mode={mode} freeLookGroup={_freeLookGroup.IsVisible} " +
                $"showcaseGroup={_showcaseGroup.IsVisible} note=只打前两次");
    }

    // 当前载入的文件那一行。文本由宿主拼好递进来，侧边栏只管显示：
    // 「文件名 / region 数 / 方块数」这三样事实的来源是载入结果，不是侧边栏自己数得出来的。
    internal void SetFile(string text)
    {
        Set(_fileText, text);
    }

    // 指针悬停拾取的那一行。文本由宿主拼好递进来（方块名/坐标/命中面）。
    internal void SetPick(string text)
    {
        Set(_pickText, text);
    }

    private static T Required<T>(Window window, string name)
        where T : Control
    {
        var control = window.FindControl<T>(name);
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

    private void OnDragSensitivityChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        _previewer.DragSensitivity = (float)e.NewValue;
        Debug.WriteLine($"[SAMPLE][sidebar.drag] dragSensitivity={_previewer.DragSensitivity}");
    }

    private void OnSpinDampingChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        _previewer.SpinDamping = (float)e.NewValue;
        Debug.WriteLine($"[SAMPLE][sidebar.spin] spinDamping={_previewer.SpinDamping}");
    }

    private void OnSpinIdleDelayChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        _previewer.SpinIdleDelay = (float)e.NewValue;
        Debug.WriteLine($"[SAMPLE][sidebar.spin] spinIdleDelay={_previewer.SpinIdleDelay}");
    }

    private void OnSpinIdleSpeedChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        _previewer.SpinIdleSpeed = (float)e.NewValue;
        Debug.WriteLine($"[SAMPLE][sidebar.spin] spinIdleSpeed={_previewer.SpinIdleSpeed}");
    }

    // 模式按钮不是「切到展台」而是「切一下」：按钮只有一个，而它要说的那句话取决于当前是哪一边，
    // 于是这里必须问一次状态。切到哪一边由宿主决定，侧边栏只负责把请求递过去——
    // 侧边栏要真去装拆控制器，就必须持有那两套零件，而它今天一个都不认识。
    private void OnModeButtonClick(object? sender, RoutedEventArgs e)
    {
        Debug.WriteLine($"[SAMPLE][sidebar.mode] 模式按钮被按下 from={_host.Mode}");
        _host.ToggleMode();
    }

    private void OnPreviousTargetClick(object? sender, RoutedEventArgs e)
    {
        _host.StepTarget(-1);
    }

    private void OnNextTargetClick(object? sender, RoutedEventArgs e)
    {
        _host.StepTarget(+1);
    }

    private void OnTick(double delta)
    {
        _windowSeconds += delta;
        _windowFrames++;
        _totalFrames++;

        if (_windowSeconds < StatsIntervalSeconds) return;

        var fps = _windowSeconds > 0 ? _windowFrames / _windowSeconds : 0;
        var frameMilliseconds = _windowFrames > 0 ? _windowSeconds * 1000 / _windowFrames : 0;

        _windowSeconds = 0;
        _windowFrames = 0;

        // 相机状态来自模型（它是权威），视口尺寸来自控件自己的 Bounds。
        // 两边都不是「侧边栏自己记一份」——记一份就会在某一刻和真正的权威不一致，
        // 而那时侧边栏显示的数字恰好是最不该信的那个。
        var camera = _camera.Camera;
        var position = camera.Position;
        var forward = camera.Forward;

        Set(_fpsText, $"fps {fps:F1}  帧时 {frameMilliseconds:F1}ms  共 {_totalFrames} 帧");
        Set(_viewportText, $"视口 {BoundsText()} 缩放 {Scaling():F2} → {PixelText()}");
        Set(_positionText, $"位置 ({position.X:F3}, {position.Y:F3}, {position.Z:F3})");
        Set(_lookText, $"朝向 yaw {camera.Yaw:F2}  pitch {camera.Pitch:F2}");
        Set(_forwardText, $"视线 ({forward.X:F3}, {forward.Y:F3}, {forward.Z:F3})");
        Set(_distanceText, $"距离 {_camera.Distance:F4} 方块");
        Set(_moveSpeedText, $"移速 {_previewer.MoveSpeed:F2} 方块/秒");
        Set(_lookSensitivityText, $"灵敏度 {_previewer.LookSensitivity:F3} 度/DIP");

        // 展台那几行只在那一组真的显示着的时候刷。它们在自由视角下是隐藏的，
        // 而给一个隐藏的 TextBlock 写文本照样会让布局失效往上传一趟——
        // 每秒四次乘以四行，为一个没人看得见的数字。
        if (_showcaseGroup.IsVisible)
        {
            Set(_showcaseTargetText, $"目标 {_host.ShowcaseTargetCaption}");
            Set(_dragSensitivityText, $"拖动灵敏度 {_previewer.DragSensitivity:F3} 度/DIP");
            Set(_spinDampingText, $"惯性阻尼 {_previewer.SpinDamping:F3} 秒");
            Set(_spinIdleDelayText, $"自转延时 {_previewer.SpinIdleDelay:F2} 秒");
            Set(_spinIdleSpeedText, $"自转速度 {_previewer.SpinIdleSpeed:F2} 度/秒");
        }

        // 只打第一条：整流刷新每秒四次，逐条打会把终端冲掉，而这条要回答的是
        // 「刷新这条链（Tick -> 攒计数 -> 写文本）到底跑了没有」——一次就够了。
        // 空白的侧边栏和「程序没跑起来」在界面上分不开，所以这句话得留在日志里。
        if (_statsLogs++ == 0)
            Debug.WriteLine(
                $"[SAMPLE][sidebar.stats] 侧边栏开始刷新（只打这一条）mode={_host.Mode} fps={fps:F1} frameMs={frameMilliseconds:F1} " +
                $"pos=({position}) yaw={camera.Yaw:F2} pitch={camera.Pitch:F2} distance={_camera.Distance:F4} " +
                $"viewport={_previewer.Bounds.Width:F0}x{_previewer.Bounds.Height:F0} scaling={Scaling():F2}");
    }

    // 物理像素那一栏与 Previewer 里算视口的算式逐字相同（DIP × RenderScaling 再四舍五入）：
    // 侧边栏显示的尺寸与渲染实际用的尺寸必须是同一个数，否则它作为参照系就没用了。
    private double Scaling()
    {
        return TopLevel.GetTopLevel(_previewer)?.RenderScaling ?? 1.0;
    }

    private string BoundsText()
    {
        return $"{_previewer.Bounds.Width:F0}x{_previewer.Bounds.Height:F0} DIP";
    }

    private string PixelText()
    {
        var scaling = Scaling();
        var width = Math.Max(1, (int)Math.Round(_previewer.Bounds.Width * scaling));
        var height = Math.Max(1, (int)Math.Round(_previewer.Bounds.Height * scaling));
        return $"{width}x{height} px";
    }

    // 只在文本真的变了的时候写。同一个字符串重复赋值在 Avalonia 里会走到属性相等性比较，
    // 但那之后仍然有一次布局失效的传播——每秒四次乘以八行，不必要。
    private static void Set(TextBlock block, string text)
    {
        if (!string.Equals(block.Text, text, StringComparison.Ordinal)) block.Text = text;
    }
}
