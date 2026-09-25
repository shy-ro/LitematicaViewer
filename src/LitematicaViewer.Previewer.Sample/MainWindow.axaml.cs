using System.Diagnostics;
using System.Numerics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace LitematicaViewer.Previewer.Sample;

public partial class MainWindow : Window
{
    private readonly PreviewerInputAdapter _input;
    private readonly CameraModel _camera;
    private readonly ScrollZoomController _scroll;
    private readonly MouseLookController _look;
    private readonly WasdCameraController _wasd;
    private readonly Sidebar _sidebar;
    private readonly InputSelfTest? _selfTest;

    public MainWindow()
    {
        InitializeComponent();

        // 输入适配器接上，但窗口自己不订阅 Scrolled / KeyChanged / Look*：没人订阅时事件照常触发，
        // 只是什么都不发生。谁在意它们是控制器的事，窗口只负责把零件装到一起。
        _input = new PreviewerInputAdapter(Viewport);

        // 相机的权威从这一行开始就在模型手里。锚点取原点，因为立方体在原点——
        // 而 Phase C 起「轮廓质心落在画面中心」那条校验，正是靠「相机看向原点」才成立的。
        //
        // 距离的上下界不设：默认就没有，谁要谁在构造时给。模型在这里替人定一个「最近 2.4」，
        // 就等于替人决定「凑近看一块砖是不允许的」，而那是个尺度上的偏好。
        _camera = new CameraModel(CameraState.Default, Vector3.Zero);

#if DEBUG
        CameraModel.VerifyCameraMath();
#endif

        // 先把模型的相机推给控件一次。这一步不是多余的：Previewer 内部那份默认值只是占位，
        // 不推的话开窗第一帧用它、第一次滚轮用模型那份，中间会跳一下——
        // 而那是两处各自都「对」的默认值，从任何一处都看不出问题。
        Viewport.SetCamera(_camera.Camera);

        _scroll = new ScrollZoomController(Viewport, _camera);
        _look = new MouseLookController(Viewport, _camera);
        _wasd = new WasdCameraController(Viewport, _camera);
        _sidebar = new Sidebar(this, Viewport, _camera);

        // 侧边栏上的任何一次按下都把焦点还回视口。滑块的 Focusable 已经是 false（拖它不该抢
        // 键盘焦点），但「按一个不可聚焦的元素会不会把焦点清掉」由模板和焦点管理器决定——
        // 而焦点一旦不在控件上，按键就送不到 Previewer，WASD 整个失效，日志里只是「没反应」。
        //
        // 订阅用 Tunnel：滑块的手柄在处理按下时会把事件标成已处理，冒泡那一趟到不了这里；
        // handledEventsToo 也救不了，因为冒泡在源头上就停了。隧道这一趟是从窗口往下走的，
        // 一定先经过侧边栏这一层。设备产生的真实点击才走路由，合成事件绕过它——
        // 所以这一条与 ICustomHitTest 那条一样，只能靠真机的日志验。
        SidebarPanel.AddHandler(
            PointerPressedEvent,
            OnSidebarPointerPressed,
            RoutingStrategies.Tunnel);

        _selfTest = Program.SelfTestSeconds > 0 ? new InputSelfTest(this, Viewport, _camera, _input) : null;
        _selfTest?.Attach();

        // 客户端尺寸与 RenderScaling 是「视口算得对不对」的参照系：
        // 视口错了的时候，第一眼要拿来的对的就是这两个数，而不是去猜 DPI。
        Opened += OnOpened;
        Activated += OnActivated;
        Deactivated += OnDeactivated;
        Closed += OnClosed;

#if DEBUG
        // 指针的窗口层探针。控件那一层在 Previewer.AttachInputProbe 里，两层分开才有分辨力。
        //
        // 这里必须显式按 Tunnel 订阅：+= 的默认策略是 Direct|Bubble，而指针事件是
        // 「先隧道下来、再冒泡上去」，命中测试发生在两者之间。隧道那一次在命中之前就到达窗口，
        // 所以「隧道有、控件没有」把范围缩到命中测试；「隧道也没有」说明消息根本没进 Avalonia
        // （或者光标不在窗口上）。用 += 订阅反而会把这两种情况混成同一种安静。
        AddHandler(PointerWheelChangedEvent, OnWindowWheelTunnel, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnWindowMovedTunnel, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnWindowPressedTunnel, RoutingStrategies.Tunnel);
        AddHandler(PointerEnteredEvent, OnWindowEnteredTunnel, RoutingStrategies.Tunnel);
#endif
    }

#if DEBUG
    private int _windowPointerMoves;

    // source 是命中测试找到的那个元素，而「命中到了谁」是这条链路的第一个未知数：
    // 隧道层收到事件只说明消息进了 Avalonia，落在哪个元素上要靠这一栏。
    // 落在 Previewer 上才是对的；落在别的元素上说明控件没被命中，
    // 而那条路接下来会一路安静——滚轮照滚、日志里只有更前面那几层有动静，相机永远不动。
    //
    // 打父链而不只打类型名：「Panel」这种基类名有两个完全不同的可能——
    // 它是 Previewer 的祖先（控件没进布局或没被命中），还是盖在 Previewer 之上的另一层（控件被挡住）。
    // 一眼分得开的只有链本身。
    private static string Describe(object? source)
    {
        StringBuilder text = new();
        Visual? current = source as Visual;

        while (current is not null)
        {
            text.Append(current.GetType().Name);

            // 名字是模板里写死的那个（PART_xxx）：只有它能把「一个匿名 Panel」
            // 和「模板里那个有职责的 Panel」分开，否则所有 Panel 看起来都一样。
            if (current is StyledElement styled && !string.IsNullOrEmpty(styled.Name))
            {
                text.Append('#').Append(styled.Name);
            }

            current = current.GetVisualParent();

            if (current is not null)
            {
                text.Append(" < ");
            }
        }

        return text.Length == 0 ? "null" : text.ToString();
    }

    // 只打完整链一次，之后退化成类型名：滚轮一来就是几十条，逐条打父链会把终端冲掉。
    private bool _sourceChainDumped;

    private string SourceOf(object? source)
    {
        if (_sourceChainDumped)
        {
            return source?.GetType().Name ?? "null";
        }

        _sourceChainDumped = true;
        return Describe(source);
    }

    private void OnWindowWheelTunnel(object? sender, PointerWheelEventArgs e) =>
        Debug.WriteLine(
            $"[SAMPLE][input.probe.window] tunnel wheel delta=({e.Delta.X},{e.Delta.Y}) source={SourceOf(e.Source)}");

    private void OnWindowEnteredTunnel(object? sender, PointerEventArgs e) =>
        Debug.WriteLine($"[SAMPLE][input.probe.window] tunnel pointer.entered source={SourceOf(e.Source)}");

    private void OnWindowPressedTunnel(object? sender, PointerPressedEventArgs e) =>
        Debug.WriteLine(
            $"[SAMPLE][input.probe.window] tunnel pointer.pressed kind=" +
            $"{e.GetCurrentPoint(this).Properties.PointerUpdateKind} source={SourceOf(e.Source)}");

    // 移动一秒几百条，逐条打会把终端冲掉。首条加每 200 条一条，只回答「到没到」这一个是非题。
    private void OnWindowMovedTunnel(object? sender, PointerEventArgs e)
    {
        _windowPointerMoves++;
        if (_windowPointerMoves == 1 || _windowPointerMoves % 200 == 0)
        {
            Point position = e.GetPosition(this);
            Debug.WriteLine(
                $"[SAMPLE][input.probe.window] tunnel pointer.moved count={_windowPointerMoves} " +
                $"pos=({position.X:F0},{position.Y:F0}) source={SourceOf(e.Source)}");
        }
    }

    // 命中测试是整条链路里唯一看不见的一段：消息进没进来有探针，事件发没发有探针，
    // 而「系统认为光标下面是哪个元素」只能主动问。控件被别的层盖住、或者没进布局时，
    // 表现就是输入一路安静——和「消息根本没来」长得一模一样。
    //
    // 从 Opened 往后放半秒：早一点布局还没稳，Bounds 还是 0x0，探针会给出假答案。
    // 取三个点而不只取中心：控件只盖住一部分时，中心可能恰好在外面。
    private void ProbeHitTest()
    {
        Debug.WriteLine($"[SAMPLE][input.hittest] client={ClientSize} scaling={RenderScaling}");

        // 控件的自身状态先打出来：命中测试把它整个跳过时，原因只有几种可能——
        // 尺寸没算出来、可见性没生效、或者从可视树上掉了下去——而这三条一眼就能排除。
        Debug.WriteLine(
            $"[SAMPLE][input.hittest] previewer bounds={Viewport.Bounds} " +
            $"visible={Viewport.IsVisible} effectiveVisible={Viewport.IsEffectivelyVisible} " +
            $"hitTestVisible={Viewport.IsHitTestVisible} topLevel={TopLevel.GetTopLevel(Viewport)?.GetType().Name ?? "null"} " +
            $"parent={Describe(Viewport.GetVisualParent())}");

        // 取样点按**控件自己的坐标系**给，再转到窗口坐标去问那两个 API。按客户区给点不行了：
        // 客户区里还有侧边栏那一块，取到的点会落在侧边栏上，于是下面那条断言红得莫名其妙
        // （而 Debug 断言失败会直接杀掉进程）。
        //
        // 取三个点而不只取中心：控件只盖住一部分时，中心可能恰好在外面。
        Point[] points =
        [
            new(Viewport.Bounds.Width / 2, Viewport.Bounds.Height / 2),
            new(10, 10),
            new(Viewport.Bounds.Width - 10, Viewport.Bounds.Height - 10),
        ];

        foreach (Point local in points)
        {
            // 转换失败本身就是一条要报的故障：变换链断掉时 Bounds 看着是对的，
            // 而命中永远落不到控件身上——那时这两个数（bounds 与命中点）只有一起看才说得清。
            Point? translated = Viewport.TranslatePoint(local, this);
            Debug.Assert(
                translated is not null,
                $"[SAMPLE][input.hittest] 控件里的点 ({local.X:F0},{local.Y:F0}) 转不到窗口坐标");
            if (translated is not { } point)
            {
                continue;
            }

            // 两个 API 都问一遍。它们走的是同一条合成层命中路径，正常时结果一致；
            // 一起打出来是为了在结果异常时能立刻分辨「命中的是谁、它挂在哪」——
            // 只打一个的话，拿到一个陌生的元素名仍然不知道它是谁。
            IInputElement? inputHit = this.InputHitTest(point);
            Debug.WriteLine(
                $"[SAMPLE][input.hittest] point=({point.X:F0},{point.Y:F0}) local=({local.X:F0},{local.Y:F0}) " +
                $"inputHitTest={Describe(inputHit)}");

            // 证据先打完再断言：断言失败会直接杀掉进程，而那时这一条正是唯一说得清「命中的是谁」的东西。
            int index = 0;
            foreach (Visual visual in this.GetVisualsAt(point))
            {
                Debug.WriteLine(
                    $"[SAMPLE][input.hittest] point=({point.X:F0},{point.Y:F0}) hit[{index++}] " +
                    $"{Describe(visual)}");
            }

            if (index == 0)
            {
                Debug.WriteLine($"[SAMPLE][input.hittest] point=({point.X:F0},{point.Y:F0}) 一个都没命中");
            }

            // 这条断言是 ICustomHitTest 那个修复的守卫，也是唯一能守住它的一条：
            // 合成事件走 RaiseEvent，根本不经过命中测试，所以整个自检剧本对这一类故障是全绿的。
            // 命中失败时输入会一路安静——和「消息没进程序」长得一模一样，人手排查要花掉一整天。
            Debug.Assert(
                ReferenceEquals(inputHit, Viewport),
                $"[SAMPLE][input.hittest] 指针没命中控件 point=({point.X:F0},{point.Y:F0}) " +
                $"local=({local.X:F0},{local.Y:F0}) hit={Describe(inputHit)} expected=Viewport#Viewport。" +
                $"命中落在别的元素上时，滚轮和按键都到不了控件，而日志里看起来只是「没反应」");
        }

        // 命中列表里冒出几个匿名元素时，唯一能回答「它是谁」的就是树本身：
        // 兄弟顺序给出层级，名字给出模板里的职责，bounds 给出它盖住了多大。
        //
        // 控件自己在窗口坐标里的位置也要问一次：命中测试用的是变换之后的矩形，
        // 变换链断掉（TranslatePoint 返回 null）时 Bounds 看着是对的，而命中永远落不到它身上。
        Point? topLeft = Viewport.TranslatePoint(default, this);
        Point? bottomRight = Viewport.TranslatePoint(new Point(Viewport.Bounds.Width, Viewport.Bounds.Height), this);
        Debug.WriteLine(
            $"[SAMPLE][input.hittest] previewerInWindow topLeft={Format(topLeft)} bottomRight={Format(bottomRight)}");

        StringBuilder tree = new();
        DumpTree(this, 0, tree);
        Debug.WriteLine($"[SAMPLE][input.hittest.tree]\n{tree}");
    }

    private static string Format(Point? point) =>
        point is { } value ? $"({value.X:F0},{value.Y:F0})" : "null";

    private static void DumpTree(Visual visual, int depth, StringBuilder text)
    {
        if (depth > 6)
        {
            text.Append(' ', depth * 2).Append("…\n");
            return;
        }

        text.Append(' ', depth * 2).Append(visual.GetType().Name);

        if (visual is StyledElement styled && !string.IsNullOrEmpty(styled.Name))
        {
            text.Append('#').Append(styled.Name);
        }

        text.Append(" bounds=").Append(visual.Bounds);

        // 有没有 Background 决定 Panel / Border 这类容器参不参与命中：
        // 没有背景的容器是「空的」，指针会穿过去；有背景的才是实心的一块。
        IBrush? background = visual switch
        {
            Border border => border.Background,
            Panel panel => panel.Background,
            _ => null,
        };

        text.Append(" background=").Append(background is null ? "null" : "set");

        // ZIndex 而不是兄弟顺序：兄弟顺序只说明默认的绘制次序，
        // 而 ZIndex 能让后加的那个压到前面来——「谁在上面」和「谁是后加的孩子」是两件事。
        text.Append(" z=").Append(visual.ZIndex);

        if (visual is InputElement input && !input.IsHitTestVisible)
        {
            text.Append(" hitTestVisible=false");
        }

        text.Append('\n');

        foreach (Visual child in visual.GetVisualChildren())
        {
            DumpTree(child, depth + 1, text);
        }
    }
#endif

    // 订阅它的理由见构造函数里那一段。这里只记录、不追加断言：窗口没激活时第一次点击
    // 只负责把窗口激活，聚焦落空是系统行为而不是接线错了——而那与真正的焦点问题
    // 在日志里长得一样，所以两者都要打出来。
    private void OnSidebarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        bool focused = Viewport.Focus();
        IInputElement? focusedElement = TopLevel.GetTopLevel(Viewport)?.FocusManager?.GetFocusedElement();
        Debug.WriteLine(
            $"[SAMPLE][sidebar.focus] 侧边栏被按下，焦点还给视口 focused={focused} " +
            $"focusedElement={focusedElement?.GetType().Name ?? "null"} expected=Previewer");
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        Debug.WriteLine($"[SAMPLE][window.opened] client={ClientSize} scaling={RenderScaling}");

        // 键盘事件只发给有焦点的元素。窗口一开就把焦点给视口，否则「打开就跑」的人
        // 先按几下键发现没反应，再去猜是代码的问题——而焦点不在时的表现和事件没接上一模一样。
        bool focused = Viewport.Focus();
        Debug.WriteLine($"[SAMPLE][window.opened] viewportFocus={focused} expected=True");

        // Focus() 返回 true 只说明「请求被接受了」。真正决定键盘听到没听到的是
        // 焦点管理器此刻记着谁——两者不一致时前者会骗人。
        IInputElement? focusedElement = TopLevel.GetTopLevel(Viewport)?.FocusManager?.GetFocusedElement();
        Debug.WriteLine(
            $"[SAMPLE][window.opened] focusedElement={focusedElement?.GetType().Name ?? "null"} expected=Previewer");

#if DEBUG
        // 布局要几帧才稳：早一点控件 Bounds 还是 0x0，命中测试会给出假答案。
        DispatcherTimer.RunOnce(ProbeHitTest, TimeSpan.FromMilliseconds(500));
#endif
    }

    // 滚轮在 Win32 上是发给「焦点窗口」的，不是光标底下的窗口（光标只在算坐标时用得上）。
    // 所以窗口没激活时收不到滚轮是正常的，而这一条在日志里必须留下痕迹——
    // 否则「窗口在后台」和「事件没接上」看起来完全一样。
    private void OnActivated(object? sender, EventArgs e) =>
        Debug.WriteLine($"[SAMPLE][window.activated] client={ClientSize}");

    // 失活时把「按住的键」和「进行中的看向手势」清掉。Alt+Tab 走了之后，抬起的按键与
    // 指针的进出/捕获丢失都送到别的窗口去了，这里不会收到：还按着的 W 会让相机一直往前走，
    // 挂着的看向手势会让光标回来的第一帧跳一下——两种表现都像鼠标键盘坏了，
    // 而不是像有个状态没清。
    //
    // 两者清在不同的地方，因为它们记的状态在谁手里不同：按键记在控制器里（它自己维护那份集合），
    // 而手势记在输入适配器里（只有它拿得到指针与捕获）。所以这里一个调控制器、一个调适配器。
    //
    // 挂在窗口这一层而不是控件的 LostFocus 上：Win32 下 WM_KILLFOCUS 会不会让元素收到
    // LostFocus 由后端决定，而「失活必须清干净」这件事不该依赖那个细节。
    private void OnDeactivated(object? sender, EventArgs e)
    {
        Debug.WriteLine(
            "[SAMPLE][window.deactivated] note=此时滚轮收不到属于预期，不是接线问题；" +
            "按键与看向手势一并作废，否则抬起与进出事件不会来");
        _wasd.ReleaseKeys();
        _input.ReleaseLook();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Debug.WriteLine($"[SAMPLE][window.closed] client={ClientSize} scaling={RenderScaling}");

        // 先出验收结论再拆零件：拆完事件就不触发了，
        // 而验收要的正是「到这一刻为止，每个事件都至少走通过一次」。
        _selfTest?.Report();
        _sidebar.Dispose();
        _wasd.Dispose();
        _look.Dispose();
        _scroll.Dispose();
        _input.Dispose();
    }
}
