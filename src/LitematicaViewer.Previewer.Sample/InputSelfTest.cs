using System.Diagnostics;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace LitematicaViewer.Previewer.Sample;

// 验收剧本。Phase D 立起来的是「输入能进来、相机能出去」，Phase E 起多一层：
// 滚轮不再由剧本自己改相机，而是交给 ScrollZoomController 与 CameraModel——剧本退到旁观者的位置。
// Phase F 的转视角与走动同样如此：剧本只合成手势、报事实，改相机的全程是控制器。
//
// 让剧本自己改相机的话，控制器整个删掉剧本照样全绿，而那恰恰是这几个相位要验的东西。
//
// 「事件触发了」光看探针说服力不够：没人订阅时它同样安静，而「安静」和「没接上」在日志里长得一样。
// 所以这里合成 Avalonia 的真实路由事件打进控件，再断言 Previewer 把每一次输入都翻成了自己的事件。
// 合成真事件而不是直接调内部方法：输入适配器那一层（订阅、映射、方向、手势的起止）也在被验的范围里。
internal sealed class InputSelfTest
{
    // 剧本按秒推进而不是按帧：帧率随机器和窗口大小变，验收脚本不该跟着变。
    private const double RotateAt = 0.4;
    private const double RotateBackAt = 0.8;
    private const double WheelAt = 1.2;
    private const double LookAroundAt = 1.6;
    private const double KeyDownAt = 2.4;
    private const double KeyUpAt = 3.2;
    private const double ResizeAt = 3.6;
    private const double GrazingAt = 4.0;
    private const double RightButtonAt = 4.4;

    // 每步按顺序编号，与 StepTimes 一一对应。
    private const int StepRotate = 0;
    private const int StepRotateBack = 1;
    private const int StepWheel = 2;
    private const int StepLookAround = 3;
    private const int StepKeyDown = 4;
    private const int StepKeyUp = 5;
    private const int StepResize = 6;
    private const int StepGrazing = 7;
    private const int StepRightButton = 8;

    private static readonly double[] StepTimes =
        [RotateAt, RotateBackAt, WheelAt, LookAroundAt, KeyDownAt, KeyUpAt, ResizeAt, GrazingAt, RightButtonAt];

    // 掠射姿态：相机几乎贴着 +X 面的平面，x 只比面心出去 0.008，其余两个方向在几米开外。
    // 那一张面按判据确实朝向我们（0.508 > 0.5），但视线与它的法线夹着 89.9 度，
    // 投影下来只剩三十几个像素——这是几何的必然，不是画错了。
    //
    // 它不是一个凑出来的怪姿态，而是**真实发生过的崩溃**：相机能自由转视角之后，
    // 任意一张面扫过镜头都会经过这个位置，而旧判据「可见面至少占画面万分之一」
    // （1184x768 下是 90 个像素）在那里必然红，Debug.Assert 失败直接终止进程。
    // 摆在这里是为了让那条判据再也不能退回去。
    private static readonly Vector3 GrazingPosition = new(0.508f, 3.76f, 4.53f);

    private const double ResizeDelta = 160;

    // 滚轮那三步：+1、+1、-1。净效果是正向一档，也就是推近到 0.8 倍距离。
    private const float WheelIn = 1f;
    private const float WheelOut = -1f;

    // 一程转视角：第一段移动只定下参照点（只要一次位置就没有「差」，不产生旋转），
    // 之后两段各往右下走 60x30 个 DIP。只往右下而不来回，理由和滚轮那三步一样：
    // 一来一回正好抵消，画面回到原样，「相机一变就验一帧」的那一帧看到的还是上一张，等于什么都没验。
    private const double LookStartX = 380;
    private const double LookStartY = 300;
    private const double LookStepX = 60;
    private const double LookStepY = 30;
    private const int LookSteps = 2;

    // 走动只按 W：A/S/D 与它共用同一段公式，逐个按键再验一遍不加分辨力。
    private const Key MoveKey = Key.W;

    // 松开按键之后隔几帧再确认相机停了。只等一帧会误判——那一帧本来就还没轮到控制器。
    private const int IdleCheckTicks = 5;

    private static readonly Pointer TestPointer = new(1, PointerType.Mouse, isPrimary: true);

    // 右键按着时的指针状态。移动事件里报的按键状态是适配器的判据之一（它靠这个补上
    // 落在别处的那次抬起），所以按住期间合成的那几段移动必须如实带上右键——
    // 不带的话剧本会走一条真机走不到的路，而那种「合成事件与真实事件不一致」的漏洞
    // 正是这类剧本最容易放过去的东西。
    private static readonly PointerPointProperties RightButtonHeld =
        new(RawInputModifiers.RightMouseButton, PointerUpdateKind.Other);

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
    private int _moveKeyEvents;
    private bool _sawKeyDown;
    private bool _sawKeyUp;
    private int _resizes;
    private (int Width, int Height) _lastViewport;
    private int _cameraSets;
    private int _lookStarted;
    private int _lookMoved;
    private int _lookEnded;

    // 滚轮前后的距离。前面那个由剧本记下，后面那个从模型读回来对——中间隔着一整个控制器。
    private float _distanceBeforeZoom;
    private float _expectedDistanceAfterZoom;

    // 走动那一段：按下与松开时的累计时间、按下时的相机与距离、以及松手时的位置。
    // 期望位移由「按了多久」算出来，而按了多久就是两个累计时间之差——中间隔着控制器。
    private double _moveStartElapsed;
    private double _moveStopElapsed;
    private CameraState _moveStartCamera;
    private float _moveStartDistance;

    // 那一段用的速度，取按下那一刻的控件属性值。它是可调的（侧边栏），
    // 所以期望不能写死一个数；取「按下时」而不是「松手时」是为了让这一段的期望是确定的——
    // 走动期间有人改过速度的话，两边本来就不该对得上，而那是调整不是错。
    private float _moveSpeed;
    private Vector3 _positionAtKeyUp;
    private int _idleCheckAt;
    private int _ownInputEvents;
    private int _walkForeignBaseline;
    private int _idleForeignBaseline;

    // 适配器是「手势的起止」的持有者（只有它拿得到指针与捕获），所以收尾那一步得走它——
    // 剧本在最后调它的 ReleaseLook()，与宿主在窗口失活时调的是同一个口子。
    private readonly PreviewerInputAdapter _input;

    internal InputSelfTest(Window window, Previewer previewer, CameraModel camera, PreviewerInputAdapter input)
    {
        _window = window;
        _previewer = previewer;
        _camera = camera;
        _input = input;
        _startCamera = CameraState.Default;

        // 关掉固定鼠标。剧本摆的是合成的指针位置，而钉住光标会把参照点换成「读回来的真实光标位置」——
        // 于是脚本合成的那一段增量里会混进真实光标当时在哪，算出来的角度就不再是它自己给的那一段。
        //
        // 这是有意的取舍，不是漏测：钉住那一条的答案在系统里（光标被挪走之后落在哪），
        // 剧本要么作弊（自己假定一个答案），要么就得有一套真鼠标。它能被验的部分是
        // 「右键按住时不接管」——那一段完全不碰光标，所以合成事件说了算，下面单独有一步验它。
        // 钉住本身由 input.confine / input.right 那几行日志记着每一次的请求点与读回来的点。
        _input.ConfinePointer = false;
    }

    internal void Attach()
    {
        _previewer.Tick += OnTick;
        _previewer.Scrolled += OnScrolled;
        _previewer.KeyChanged += OnKeyChanged;
        _previewer.ViewportResized += OnViewportResized;
        _previewer.LookStarted += OnLookStarted;
        _previewer.LookMoved += OnLookMoved;
        _previewer.LookEnded += OnLookEnded;

        Debug.WriteLine(
            $"[SAMPLE][selftest.attach] steps={StepTimes.Length} last={StepTimes[^1]}s " +
            $"expected=窗口至少开 {StepTimes[^1] + 1.1:F1}s 剧本才跑得完");
    }

    internal void Report()
    {
        // 期望尺寸按**视口**算，不按客户区：侧边栏也占客户区的一块，
        // 两者相等只在「视口铺满窗口」的时候成立。（这个等式曾经是：视口 == 客户区。）
        //
        // 算式与 Previewer.OnOpenGlRender 里那一处逐字相同——视口的物理像素 = DIP × RenderScaling。
        // 这里换一个算法就等于把「视口算得对不对」这条断言的前提换掉了。
        double scaling = _window.RenderScaling;
        int expectedWidth = (int)Math.Round(_previewer.Bounds.Width * scaling);
        int expectedHeight = (int)Math.Round(_previewer.Bounds.Height * scaling);

        Debug.WriteLine(
            $"[SAMPLE][selftest.summary] ticks={_ticks} scrolled={_scrolled} wheelIn={_wheelIn} wheelOut={_wheelOut} " +
            $"key={_keyEvents} keyDown={_sawKeyDown} keyUp={_sawKeyUp} resizes={_resizes} " +
            $"lastViewport={_lastViewport.Width}x{_lastViewport.Height} " +
            $"expected={expectedWidth}x{expectedHeight} client={_window.ClientSize} cameraSets={_cameraSets} " +
            $"look={_lookStarted}/{_lookMoved}/{_lookEnded} " +
            $"distance={_distanceBeforeZoom:F4}->{_camera.Distance:F4} elapsed={_elapsed:F2}s");

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
            $"[SAMPLE][selftest] 最后一次视口尺寸与控件的 bounds 不符 last={_lastViewport} " +
            $"expected=({expectedWidth},{expectedHeight}) bounds={_previewer.Bounds} scaling={scaling}");

        // 剧本自己摆的视角只有三次：两次旋转，一次掠射。滚轮、转视角、走动都不经过这里——
        // 它们走各自的控制器，那正是这几个相位要验的链路。外部输入也改不了这个计数：
        // 只有 ApplyCamera 会动它。
        Debug.Assert(
            _cameraSets == 3,
            $"[SAMPLE][selftest] 剧本摆视角的次数不对 cameraSets={_cameraSets} expected=3");

        Debug.WriteLine(
            "[SAMPLE][selftest.summary] 四个事件都至少走通一次，滚轮/转视角/走动各自经由控制器改了相机");
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

        if (_idleCheckAt != 0 && _ticks >= _idleCheckAt)
        {
            _idleCheckAt = 0;
            CheckIdle();
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
                //
                // 就地验而不是留到 Report：RaiseWheel 是同步的，这一行跑到的时候这三次已经走完，
                // 此刻的距离就是脚本那三次的结果。留到 Report 再比的话，中间任何一次真实滚轮
                // 都会把它改掉——而那会让一条正确的实现红掉，比漏报还糟。
                Expect(_camera.Distance, _expectedDistanceAfterZoom, 1e-3f, "三次滚轮之后的距离");
                Debug.Assert(
                    _camera.Distance < _distanceBeforeZoom,
                    $"[SAMPLE][selftest] 正向滚轮没有把相机拉近 before={_distanceBeforeZoom:F4} " +
                    $"after={_camera.Distance:F4}");
                break;

            case StepLookAround:
                LookAround();
                break;

            case StepKeyDown:
                _moveStartElapsed = _elapsed;
                _moveStartCamera = _camera.Camera;
                _moveStartDistance = _camera.Distance;
                _moveSpeed = _previewer.MoveSpeed;
                _walkForeignBaseline = ForeignInputCount();
                RaiseKey(MoveKey, down: true);
                break;

            case StepKeyUp:
                _moveStopElapsed = _elapsed;
                _positionAtKeyUp = _camera.Camera.Position;
                RaiseKey(MoveKey, down: false);

                // 基线记在抬起之后（含这一次）：记在之前的话，这一次自己的抬起就会被算成
                // 「外部又插了一条输入」，于是停住检查永远被跳过——而它看起来像正常工作。
                _idleForeignBaseline = ForeignInputCount();
                CheckWalked();

                // 松手之后隔几帧再确认相机停了。放在这里而不是 Report 里：Report 要到窗口关闭
                // 才跑，中间还夹着一个拉伸窗口的步骤，那一段时间越长越容易被外部输入搅进来。
                _idleCheckAt = _ticks + IdleCheckTicks;
                break;

            case StepResize:
                _window.Width += ResizeDelta;
                break;

            case StepGrazing:
                // 摆到掠射姿态，让画面校验在「某个面只剩几十个像素」的记录上过一次。
                // 走模型而不是直接灌给 Previewer，理由同 ApplyCamera。
                ApplyCamera(CameraState.LookAt(
                    GrazingPosition,
                    Vector3.Zero,
                    _startCamera.Fov,
                    _startCamera.Near,
                    _startCamera.Far));
                break;

            case StepRightButton:
                RightButtonSuspend();
                break;

            default:
                Debug.Fail($"[SAMPLE][selftest.step] 未知的步骤 step={step}");
                break;
        }
    }

    // 转的是相机位置而不是只改 yaw：绕 +Y 转位置再 LookAt 原点，能保证相机仍然看向立方体中心，
    // 于是「轮廓质心落在画面中心」那条断言继续成立，验的就只剩可见面集合的变化。
    //
    // 第一视角下「转头」会让相机看向别处，那种姿态本来就验不了（IsVerifiable 会明说跳过）。
    // 所以剧本得自己摆出一个看向中心的姿态，画面校验才有东西可验——这里摆的是位置，
    // 而转视角那一步改的是朝向，两者验的不是同一件事。
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

    // 转视角走完整条链路：合成指针移动 -> 适配器（不需要按键）-> Look* -> 控制器 -> 模型 -> SetCamera。
    //
    // 断言的是「相机转了多少」，而不是「收到了几次移动」：前者才是这条链路存在的理由，
    // 后者只能证明事件到了。灵敏度取控件属性（侧边栏能改它）而不是写一个数：
    // 写死的话，改了默认值这条断言就红，而它要钉的是换算关系与方向，不是那个手感值。
    //
    // 合成的是 PointerMoved，而且 Properties 里一个键都不按：适配器如果还留着「必须按着左键」
    // 那道门，这里一步都转不动，而日志上看起来只是「指针移了但画面没动」。
    private void LookAround()
    {
        // 先把适配器手里可能挂着的手势收掉，再开始自己的。
        //
        // 现在指针一动就转视角，于是真实鼠标在窗口上的每一次移动都是一段手势——它是**连续**的。
        // 不先收掉的话，脚本的第一段移动会被当成「那一段真人手势的延续」：参照点还在真人那个位置上，
        // 差值就是一段凭空的跳转，而这与「换算写错了」在数值上分不开。
        //
        // 放在取基线之前：这一次收尾自己也会发一个 LookEnded，记在基线后面才算得对。
        // 这一段之后到断言为止是同步的（没有消息泵），外面插不进来，参照点从这里起是确定的。
        _input.ReleaseLook();

        float sensitivity = _previewer.LookSensitivity;
        int startedBefore = _lookStarted;
        int movedBefore = _lookMoved;
        int endedBefore = _lookEnded;

        CameraState before = _camera.Camera;
        float distanceBefore = _camera.Distance;

        // 第一段只把参照点写下来，它本身不产生旋转——角度是两次位置之差。
        Point position = new(LookStartX, LookStartY);
        RaisePointerMoved(position);

        for (int i = 0; i < LookSteps; i++)
        {
            position += new Avalonia.Vector(LookStepX, LookStepY);
            RaisePointerMoved(position);
        }

        // 收尾走适配器的 ReleaseLook()，与宿主在窗口失活时调的是同一个口子。
        // 没有抬起事件可以用了，所以「这一段结束」只能由位置本身或者宿主给出。
        _input.ReleaseLook();

        // yaw 增量直接相减而不绕回 ±180：这一段是正的 30 度，离跳变点还有 7 度以上；
        // 而「绕回」那个函数本身也是要验的东西，混进来会把两件事搅在一起。
        Expect(
            _camera.Camera.Yaw - before.Yaw,
            (float)(LookStepX * LookSteps) * sensitivity,
            0.1f,
            "转视角的 yaw 增量（往右移是往右转）");
        Expect(
            _camera.Camera.Pitch - before.Pitch,
            (float)(LookStepY * LookSteps) * sensitivity,
            0.1f,
            "转视角的 pitch 增量（往下移是往下看）");

        // 第一视角：转视角只改朝向，相机位置一个 bit 都不该动。
        // 位置动了就说明转视角又写成了「把相机挪到别处」（orbit 那个老实现），而单看角度增量、
        // 灵敏度、方向这些都是对的——只有连着转上一圈才发现画面是在绕着某点公转。
        Expect((_camera.Camera.Position - before.Position).Length(), 0f, 1e-6f, "转视角后的相机位置");

        // 参考距离是缩放的尺度，与转视角无关，同样不该动。
        Expect(_camera.Distance, distanceBefore, 1e-3f, "转视角后的参考距离");

        Debug.Assert(
            _lookStarted == startedBefore + 1,
            $"[SAMPLE][selftest] 一程转视角应该恰好一个起点 before={startedBefore} after={_lookStarted}");
        Debug.Assert(
            _lookMoved == movedBefore + LookSteps,
            $"[SAMPLE][selftest] 一程转视角的移动条数不对 before={movedBefore} after={_lookMoved} " +
            $"expected=+{LookSteps}（第一段只定参照点，不算在内）");
        Debug.Assert(
            _lookEnded == endedBefore + 1,
            $"[SAMPLE][selftest] 一程转视角应该恰好一个终点 before={endedBefore} after={_lookEnded}");
    }

    // 固定鼠标是「光标归程序」，于是拖窗口、点侧边栏上的滑块、够标题栏全都做不到。
    // 按住右键把这些还回去：转视角停下、光标放出来。这一步验的就是「还回去了」。
    //
    // 分三段：按住期间一个事件都不该来、相机一动不动；抬起之后必须重新起手势并真的转起来；
    // 以及**抬起事件落在别处**（拖到侧边栏上松开）时也要能自己恢复。
    //
    // 它的失效方式很静默：右键没被认出来时画面照旧跟着鼠标转，而「画面在转」在日志里
    // 和「这一段本来就该转」长得一模一样——用户看到的是「按了右键还是拖不动窗口」。
    private void RightButtonSuspend()
    {
        // 先把适配器手里可能挂着的手势收掉，理由同 LookAround。
        _input.ReleaseLook();

        float sensitivity = _previewer.LookSensitivity;
        int startedBefore = _lookStarted;
        int movedBefore = _lookMoved;
        CameraState before = _camera.Camera;

        RaisePointerPressed();

        // 按住期间连发几段移动：一段就够验「没起手势」，多发几段是为了连「手势起了又立刻收」
        // 这种半吊子实现一起挡住。移动事件里必须如实带上右键——适配器会拿这一栏判断挂起该不该继续。
        Point position = new(LookStartX, LookStartY);
        for (int i = 0; i <= LookSteps; i++)
        {
            position += new Avalonia.Vector(LookStepX, LookStepY);
            RaisePointerMoved(position, RightButtonHeld);
        }

        Debug.Assert(
            _lookStarted == startedBefore && _lookMoved == movedBefore,
            $"[SAMPLE][selftest.right] 按住右键期间不该接管指针 started=+{_lookStarted - startedBefore} " +
            $"moved=+{_lookMoved - movedBefore} expected=+0/+0");
        Debug.Assert(
            _camera.Camera == before,
            $"[SAMPLE][selftest.right] 按住右键期间相机被改了 yaw={_camera.Camera.Yaw:F3} " +
            $"pitch={_camera.Camera.Pitch:F3} expected=yaw={before.Yaw:F3} pitch={before.Pitch:F3}");

        RaisePointerReleased();

        // 抬起之后要两段移动才看得到角度：第一段起手势（不产生增量），第二段才真的转。
        // 只发一段的话，「抬起时忘了复位」和「复位了」在角度上完全一样——
        // 前者一条事件都不发，后者发一条起点，而两者都不转。
        position += new Avalonia.Vector(LookStepX, LookStepY);
        RaisePointerMoved(position);
        position += new Avalonia.Vector(LookStepX, LookStepY);
        RaisePointerMoved(position);

        Debug.Assert(
            _lookStarted == startedBefore + 1,
            $"[SAMPLE][selftest.right] 抬起右键之后没有重新起手势 started=+{_lookStarted - startedBefore} " +
            $"expected=+1");
        Expect(
            YawDelta(before.Yaw, _camera.Camera.Yaw),
            (float)LookStepX * sensitivity,
            0.05f,
            "抬起右键之后第一段增量的 yaw");

        Debug.WriteLine(
            $"[SAMPLE][selftest.right] 按住期间 0 条事件、相机一动不动；抬起后重新起手势并转了 " +
            $"{(float)LookStepX * sensitivity:F2} 度");

        // 第三段：抬起事件**根本没送到控件**时也要能自己恢复。
        //
        // 这不是假想：右键按住之后可以把光标拖到侧边栏上再松开，那一下抬起落在侧边栏上，
        // 而侧边栏不是控件的祖先，事件不会回来。标志会一直挂着，症状是回来之后鼠标怎么动都不转。
        // 适配器因此不看那个事件，而看移动事件里报的按键状态——这一步钉的就是那条判断。
        RaisePointerPressed();

        position += new Avalonia.Vector(LookStepX, LookStepY);
        RaisePointerMoved(position, RightButtonHeld);
        Debug.Assert(
            _lookStarted == startedBefore + 1,
            $"[SAMPLE][selftest.right] 挂起期间又起了手势 started=+{_lookStarted - startedBefore} expected=+1");

        float yawBeforeRecovery = _camera.Camera.Yaw;

        // 第二段带的是「右键已经松开」的状态，而抬起事件一次都不发。
        position += new Avalonia.Vector(LookStepX, LookStepY);
        RaisePointerMoved(position);
        position += new Avalonia.Vector(LookStepX, LookStepY);
        RaisePointerMoved(position);

        Debug.Assert(
            _lookStarted == startedBefore + 2,
            $"[SAMPLE][selftest.right] 抬起事件缺失时没能自己恢复 started=+{_lookStarted - startedBefore} " +
            $"expected=+2 note=适配器要从移动事件报的按键状态补上这次抬起");
        Expect(
            YawDelta(yawBeforeRecovery, _camera.Camera.Yaw),
            (float)LookStepX * sensitivity,
            0.05f,
            "抬起事件缺失时第一段增量的 yaw");

        _input.ReleaseLook();
    }

    // 两次 yaw 之间的真实增量。
    //
    // yaw 的落点是 (-180,180]（模型里每改一次就归一次，见 CameraModel.Wrap），所以
    // 「后一次减前一次」在跨过那条分界时会得到 -354 这种数，而真实增量是 +6。
    // 这一步（以及它前面那一步）的起点正好停在 179.x——它就在分界上，必须折回来比。
    //
    // 别处那一步（LookAround）仍然直接相减，那里离分界很远，而它的注释说了理由：
    // 那一段要钉的是「换算与方向」，把绕回这件事混进去会让两件事搅在一起。
    // 归位本身在模型自检里有一条（正反各转 90 度再回来）。
    private static float YawDelta(float before, float after)
    {
        float delta = (after - before) % 360f;

        if (delta > 180f)
        {
            delta -= 360f;
        }
        else if (delta < -180f)
        {
            delta += 360f;
        }

        return delta;
    }

    // 走动那一段的结论：位移大小由「按了多久」算，方向由按下时的视线定。
    //
    // 时长用两个累计时间之差，而不是脚本自己数帧：控制器的位移就是这些 tick 的 delta 积出来的，
    // 而脚本的累计时间加的是同一批 delta。换一种数法，两边就会差出几帧的位移，
    // 那时容差要放大到看不出错的程度才过得去。
    private void CheckWalked()
    {
        double seconds = _moveStopElapsed - _moveStartElapsed;
        Vector3 moved = _camera.Camera.Position - _moveStartCamera.Position;
        float distance = moved.Length();
        float expected = _moveSpeed * (float)seconds;

        // 数据先打出来，结论再看前提：按住 W 的这 0.8 秒里只要有人动了鼠标或滚轮，
        // 相机就会合理地多走/少走一段，而这几个数与「控制器算错了」在数值上分不开。
        // 那就不是接线问题，说明白比红掉好——红一条正确的实现比漏报还糟。
        int interference = ForeignInputCount() - _walkForeignBaseline;
        if (interference != 0)
        {
            Debug.WriteLine(
                $"[SAMPLE][selftest.walk] seconds={seconds:F3} speed={_moveSpeed} expected={expected:F4} " +
                $"actual={distance:F4} 跳过断言 note=按住期间外部输入插了 {interference} 条，位移里混着别人的");
            return;
        }

        Debug.Assert(
            seconds > 0.5,
            $"[SAMPLE][selftest] 走动那一段太短，验不出东西 seconds={seconds:F3}");

        // 容差 1%：两边都是浮点积分，只是求和顺序不同，误差在 1e-6 量级；
        // 而方向反了、速度写错了、根本没走，偏差都是 100% 这个量级。
        Expect(distance, expected, expected * 0.01f, "按住 W 走出的距离");

        // 方向是按下那一刻的视线在水平面上的投影。按住期间没有再动视角，所以这条能精确成立；
        // 方向算错（比如用了含俯仰的视线、或者左右反了）会立刻偏出去。
        Vector3 forward = _moveStartCamera.Forward;
        Vector3 groundForward = Vector3.Normalize(new Vector3(forward.X, 0f, forward.Z));
        Expect(Vector3.Dot(Vector3.Normalize(moved), groundForward), 1f, 1e-4f, "走动的方向");

        // 走动是平移：朝向一个 bit 都不该动。「低头之后 W 还往前走」这件事，
        // 靠的就是前进方向另算、而相机姿态不动。
        Debug.Assert(
            _camera.Camera.Forward == forward,
            $"[SAMPLE][selftest] 走动把朝向改了 before={forward} after={_camera.Camera.Forward}");

        // 距离也不该动：平移是刚体的，Target 跟着一起走。
        Expect(_camera.Distance, _moveStartDistance, 1e-3f, "走动后的距离");

        Debug.WriteLine(
            $"[SAMPLE][selftest.walk] seconds={seconds:F3} speed={_moveSpeed} expected={expected:F4} " +
            $"actual={distance:F4} dot={Vector3.Dot(Vector3.Normalize(moved), groundForward):F6}");
    }

    // 松开按键之后相机必须停住。这一条单独验，是因为它失效的样子最难看：键状态没清掉的话
    // 相机一直往前走，回来看到的是镜头自己飞了——而前面那些断言照样全绿，它们只看走了多远。
    private void CheckIdle()
    {
        int interference = ForeignInputCount() - _idleForeignBaseline;
        if (interference != 0)
        {
            // 松手之后又来了输入，说明有人在外面动鼠标或键盘。那不是接线问题，
            // 而这条断言的前提（「松手之后没有任何输入」）已经不成立了——说明白比红掉好。
            Debug.WriteLine(
                $"[SAMPLE][selftest.walk] 跳过停住检查 note=松手后又收到 {interference} 条" +
                $"输入事件，外部输入插进来了");
            return;
        }

        Vector3 drift = _camera.Camera.Position - _positionAtKeyUp;
        Debug.Assert(
            drift == Vector3.Zero,
            $"[SAMPLE][selftest.walk] 松开按键之后相机还在走 drift=({drift}) " +
            $"note=键状态没清掉，或者 Tick 里没判有没有按键");
        Debug.WriteLine($"[SAMPLE][selftest.walk] 松开后 {IdleCheckTicks} 帧位移为 0，键状态确实清掉了");
    }

    // 能改相机的外部输入的总数。滚轮、转视角、按键都算——它们里的任何一个在停住检查的窗口里
    // 出现，都会让相机合理地动起来，而那与「键状态没清掉」在数值上分不开。
    private int InputEventCount() => _scrolled + _lookStarted + _lookMoved + _moveKeyEvents;

    // 其中有多少是剧本自己合成的。
    //
    // 减掉它而不是记一个「事件总数」的基线：总量在两次采样之间必然变，而变的那部分里
    // 有一部分本来就是自己发的。前面写错过一次——基线记在按下之前，于是自己的那次抬起
    // 被当成外来的，停住检查永远跳过，而它看起来像「一切正常」。
    //
    // 只算「能移动相机的那些」：转视角的终点不在内（它动不了相机），所以合成的收尾也不记。
    private int ForeignInputCount() => InputEventCount() - _ownInputEvents;
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
        Debug.WriteLine($"[SAMPLE][selftest.scroll] delta={delta} distance={_camera.Distance:F4}");
    }

    private void OnKeyChanged(Key key, bool isDown)
    {
        _keyEvents++;

        if (key != MoveKey)
        {
            // 别的键也可能是系统塞进来的（焦点切换之类），只记不判，免得把无关事件算进来。
            Debug.WriteLine($"[SAMPLE][selftest.key] 收到剧本之外的按键 key={key} down={isDown}");
            return;
        }

        _moveKeyEvents++;
        _sawKeyDown |= isDown;
        _sawKeyUp |= !isDown;
    }

    private void OnViewportResized(int width, int height)
    {
        _resizes++;
        _lastViewport = (width, height);
    }

    private void OnLookStarted() => _lookStarted++;

    // 写全 Avalonia.Vector：System.Numerics 里也有个 Vector（静态类），简单名会撞上。
    private void OnLookMoved(Avalonia.Vector delta) => _lookMoved++;

    private void OnLookEnded() => _lookEnded++;

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
        _ownInputEvents++;

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
        _ownInputEvents++;

        PointerWheelEventArgs args = new(
            _previewer,
            TestPointer,
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

    // 合成指针移动。走真实的路由事件而不是直接调 Previewer 的 RaiseLook*：
    // 「指针一动就开始转、不需要按键」那一段在适配器里，直接调就把它整个跳过去了。
    //
    // 按键状态默认是「一个键都没按」（default 就是 RawInputModifiers.None）：那正是要验的前提——
    // 转视角不需要按键，适配器如果还留着那道门，这里就一步都转不动，
    // 而日志上看起来只是「指针移了，画面没动」。
    private void RaisePointerMoved(Point position, PointerPointProperties properties = default)
    {
        _ownInputEvents++;

        // 移动事件的第一个参数是路由事件本身：PointerEventArgs 收 RoutedEvent，
        // 而 PointerPressed/Released 那两个不收——它们自己就知道该用哪条路由事件。
        // 三种构造函数的形状各不相同，写错了编译不过，也就没机会在运行期悄悄递错。
        PointerEventArgs args = new(
            InputElement.PointerMovedEvent,
            _previewer,
            TestPointer,
            _previewer,
            position,
            0UL,
            properties,
            KeyModifiers.None);

        _previewer.RaiseEvent(args);
    }

    // 合成右键按下。走真实的路由事件，所以「适配器认不认得出右键」也在被验的范围里——
    // 直接调它内部的方法就把那一层跳过去了，而那一层正是这一步要验的东西。
    //
    // 按键状态同时写进两栏（修饰位和 PointerUpdateKind）：适配器读的是哪一栏由它的实现决定，
    // 而合成的这一份必须与真实鼠标产生的那一份一样。只填一栏的话，
    // 「适配器读错了那一栏」这个 bug 会被剧本放过去，真机上才暴露。
    private void RaisePointerPressed()
    {
        _ownInputEvents++;

        PointerPressedEventArgs args = new(
            _previewer,
            TestPointer,
            _previewer,
            default,
            0UL,
            new PointerPointProperties(RawInputModifiers.RightMouseButton, PointerUpdateKind.RightButtonPressed),
            KeyModifiers.None,
            clickCount: 1);

        _previewer.RaiseEvent(args);
    }

    private void RaisePointerReleased()
    {
        _ownInputEvents++;

        PointerReleasedEventArgs args = new(
            _previewer,
            TestPointer,
            _previewer,
            default,
            0UL,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.RightButtonReleased),
            KeyModifiers.None,
            MouseButton.Right);

        _previewer.RaiseEvent(args);
    }

    private static void Expect(float actual, float expected, float tolerance, string what)
    {
        Debug.Assert(
            MathF.Abs(actual - expected) < tolerance,
            $"[SAMPLE][selftest] {what} 不对 actual={actual:F6} expected={expected:F6} tolerance={tolerance}");
    }
}
