using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Rendering;
using LitematicaViewer.Previewer.Diagnostics;

namespace LitematicaViewer.Previewer;

// 用 OpenGlControlBase 而非 NativeControlHost：Avalonia 已封装 GL 上下文生命周期。
// 若日后需要多视口或自定义上下文，再换。
//
// 顺带记一条后面会咬人的事：基类的 GlVersion 只有 getter，没法要求 3.3 core 之类的具体版本。
// 上下文版本由 Avalonia 按平台后端决定，所以着色器要按拿到的版本来写指令
// （实测 ANGLE 给的是 GLES，指令得是 `#version 300 es` 那一套），不能照抄桌面 GL 的写法。
//
// ICustomHitTest 不是可选项，见下面 HitTest 的注释：不实现它，这个控件对指针完全隐形。
public partial class Previewer : OpenGlControlBase, ICustomHitTest
{
    // 背景取蓝不取白：立方体的六个面按法线着色，全是浅色，白底上会糊成一片。
    // 也不取纯黑：纯黑与「这一帧什么都没画出来」在截图里分不开。
    private static readonly Vector3 ClearColor = new(0.12f, 0.30f, 0.55f);

    // 逐帧打桩会把终端冲掉，而上下文稳不稳定只要抽样看就够。
    private const int ProbeFrameInterval = 60;

    // 一次 tick 最多认 0.25 秒。两帧之间隔上几百毫秒是常事（拖窗口、断点停下、换显示器），
    // 不夹住的话控制器拿着这个 dt 一积分就是一整段瞬移，滚动缩放会直接跳穿；
    // 夹住之后最坏只是动作变慢，而那一眼就能看出来。
    private const double MaxTickSeconds = 0.25;

    // R4 的守卫。控件在 UI 线程上构造，之后每次 GL 回调都要求落在同一线程。
    // GL 上下文不跨线程，而这个错误通常不抛异常，只在某些机器上偶发黑屏或花屏。
    private readonly int _uiThreadId = Environment.CurrentManagedThreadId;

    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private TimeSpan _lastFrameElapsed;
    private int _framesRendered;
    private int _viewportWidth;
    private int _viewportHeight;
    private GlCubeRenderer? _cube;

    // 指针能不能选中本控件，由这一条说了算，而默认答案是「不能」。
    //
    // Avalonia 12 的命中测试走合成层（CompositingRenderer.HitTest / CompositionTarget.TryHitTest）：
    // 只有产生了合成视觉的元素才在命中范围里。别的控件靠 Border / Background 产生绘制内容，
    // 而本控件的画面是 GL 直接画到窗口上的，合成器那边它是空的——于是指针事件
    // 全部落到它下面那层容器（窗口模板里一个匿名 Panel）上，滚轮和按键在控件上永远不触发。
    //
    // 这个故障极具迷惑性：窗口激活、控件 Focusable、宿主调了 Focus()、
    // 事件订阅一个不少、探针格式全对，日志里却一路安静，看起来像「输入根本没进程序」。
    // 实际上真实输入进得来——隧道层收得到——只是命中的不是这个控件。
    // （合成事件不走命中测试，所以自检剧本永远是绿的，这也是它没被更早发现的原因。）
    //
    // ICustomHitTest 是给「自己画自己」的控件留的口子：命中范围由它说了算。
    // 判据用 Bounds 而不是「有没有画东西」：这个控件的画面盖满自己的整个矩形，
    // 让指针穿过去点它背后的东西本来就没有意义。
    bool ICustomHitTest.HitTest(Point point)
    {
        // 入参是 TopLevel 坐标，先转回自己的坐标系再跟 Bounds 比。
        if (TopLevel.GetTopLevel(this) is not { } root)
        {
            return false;
        }

        Point? local = root.TranslatePoint(point, this);
        return local is { } value && new Rect(Bounds.Size).Contains(value);
    }

#if DEBUG
    // 整张 framebuffer 读回来会强制 GPU 同步，逐帧读会把帧率打到地板上，
    // 所以只在相机变化时读，且总数封顶。换了相机就是换了一张画面，值得验一次；
    // 相机没变时同一张画面验两遍不给新信息。
    private const int MaxCameraVerifications = 6;

    private int _verifiedCameraVersion = -1;
    private int _cameraVerifications;
#endif

    protected override void OnOpenGlInit(GlInterface gl)
    {
        base.OnOpenGlInit(gl);

        Debug.Assert(
            Environment.CurrentManagedThreadId == _uiThreadId,
            $"[PREVIEWER][gl.init] 不在 UI 线程上 thread={Environment.CurrentManagedThreadId} expected={_uiThreadId}");
        Debug.Assert(
            !string.IsNullOrEmpty(gl.Version),
            "[PREVIEWER][gl.init] 版本串为空，上下文可能没建起来");

        // 每帧都设也成立，但只在 init 设一次能顺带证明 init 确实跑过。
        gl.ClearColor(ClearColor.X, ClearColor.Y, ClearColor.Z, 1f);

        Debug.WriteLine(
            $"[PREVIEWER][gl.init] version='{gl.Version}' renderer='{gl.Renderer}' vendor='{gl.Vendor}'");
        Debug.WriteLine(
            $"[PREVIEWER][gl.init] clearColor=({ClearColor.X},{ClearColor.Y},{ClearColor.Z},1) " +
            $"expected=(0.12,0.3,0.55,1)");

        if (gl.ContextInfo is { } info)
        {
            Debug.WriteLine(
                $"[PREVIEWER][gl.init] profile={info.Version.Type} gl={info.Version.Major}.{info.Version.Minor} " +
                $"compatibility={info.Version.IsCompatibilityProfile} extensions={info.Extensions.Count}");
        }

        Debug.WriteLine(
            $"[PREVIEWER][gl.init] cap.vao={gl.IsBindVertexArrayAvailable} cap.blit={gl.IsBlitFramebufferAvailable} " +
            $"cap.drawBuffer={gl.IsDrawBufferAvailable}");

#if DEBUG
        // 与 GL 无关，放这儿只是因为「上下文初始化」是唯一确定只跑一次的地方。
        CameraState.VerifyConvention();
#endif

        _cube = GlCubeRenderer.Create(gl);
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        _framesRendered++;

        TimeSpan now = _clock.Elapsed;
        double delta = _framesRendered == 1 ? 0 : (now - _lastFrameElapsed).TotalSeconds;
        _lastFrameElapsed = now;

        double tickSeconds = Math.Min(delta, MaxTickSeconds);
        if (tickSeconds < delta)
        {
            Debug.WriteLine(
                $"[PREVIEWER][gl.render] tick 被夹住 delta={delta:F3} clamped={tickSeconds:F3} " +
                $"max={MaxTickSeconds} expected=只在卡顿时才出现");
        }

        // 先 tick 再画：控制器在 Tick 里 SetCamera，这一帧立刻用得上新相机，少一帧延迟。
        // 反过来先画再 tick 也跑得起来，代价是相机永远落后一帧，拖起来像有阻尼。
        RaiseTick(tickSeconds);

        // 视口按物理像素算，Bounds 是 DIP。差一个 RenderScaling 在 100% 缩放的显示器上
        // 完全看不出来，只在缩放显示器上把画面缩进一角——那时已经在查别的地方了。
        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        int width = Math.Max(1, (int)Math.Round(Bounds.Width * scaling));
        int height = Math.Max(1, (int)Math.Round(Bounds.Height * scaling));

        if (width != _viewportWidth || height != _viewportHeight)
        {
            int previousWidth = _viewportWidth;
            int previousHeight = _viewportHeight;
            _viewportWidth = width;
            _viewportHeight = height;
            RaiseViewportResized(width, height, previousWidth, previousHeight);
        }

        // 显式绑一次 Avalonia 交给我们的那个 framebuffer。省掉这一步不会立刻报错，
        // 症状是画到别处去了，而屏幕上什么都没有。
        gl.BindFramebuffer(GlConsts.GL_FRAMEBUFFER, fb);
        gl.Viewport(0, 0, width, height);
        gl.Clear(GlConsts.GL_COLOR_BUFFER_BIT | GlConsts.GL_DEPTH_BUFFER_BIT);

        _cube?.Render(_camera, width, height);

        // 自驱动渲染循环：这一帧的末尾换来下一帧，节流交给 Avalonia 的合成器（实测就是显示刷新率）。
        // 不改成「只在相机变化时才请求」是因为 Tick 是控制器的时间来源：一旦没有输入就不出帧，
        // 靠时间推进的东西（惯性、缩放动画）会直接停住。代价是空闲时也按刷新率出帧。
        RequestNextFrameRendering();

        if (_framesRendered == 1 || _framesRendered % ProbeFrameInterval == 0)
        {
            Debug.Assert(
                _framesRendered > 1 || Environment.CurrentManagedThreadId == _uiThreadId,
                $"[PREVIEWER][gl.render] 首帧不在 UI 线程上 thread={Environment.CurrentManagedThreadId}");

            Debug.WriteLine(
                $"[PREVIEWER][gl.render] frame={_framesRendered} fb={fb} bounds={Bounds.Width}x{Bounds.Height} " +
                $"scaling={scaling} viewport={width}x{height} dt={tickSeconds * 1000:F1}ms " +
                $"elapsed={now.TotalSeconds:F2}s");
        }

        // 这一段是约定里说的例外：断言本身在 Release 下会消失，但读回那一下 GPU 同步不会，
        // 所以连调用点一起包掉。其余探针都不需要 #if DEBUG。
#if DEBUG
        if (_cameraVersion != _verifiedCameraVersion && _cameraVerifications < MaxCameraVerifications)
        {
            _verifiedCameraVersion = _cameraVersion;
            _cameraVerifications++;
            VerifyFrame(gl, width, height);
        }
#endif
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        double elapsed = _clock.Elapsed.TotalSeconds;
        double averageFps = elapsed > 0 ? _framesRendered / elapsed : 0;

        Debug.WriteLine(
            $"[PREVIEWER][gl.deinit] frames={_framesRendered} expected=>0 elapsed={elapsed:F2}s " +
            $"avgFps={averageFps:F1}");
        Debug.Assert(
            _framesRendered > 0,
            $"[PREVIEWER][gl.deinit] 挂上去了却一帧没画 frames={_framesRendered}");

        // 上下文还在，可以正常走 GL 的删除路径。
        _cube?.Dispose();
        _cube = null;

        base.OnOpenGlDeinit(gl);
    }

    protected override void OnOpenGlLost()
    {
        // 上下文丢了，GPU 侧的对象随之消失，此时再发 Delete* 就是对着失效的函数指针发号施令。
        // 所以只丢引用、不发 GL 调用，等下一次 Init 重建。
        Debug.WriteLine($"[PREVIEWER][gl.lost] frames={_framesRendered} expected=之后会再来一次 gl.init");
        _cube?.Abandon();
        _cube = null;

        base.OnOpenGlLost();
    }

#if DEBUG
    private void VerifyFrame(GlInterface gl, int width, int height)
    {
        int error = ReadGlError(gl);
        Debug.WriteLine(
            $"[PREVIEWER][gl.error] code=0x{error:X} expected=0x0 cameraVersion={_cameraVersion}");
        Debug.Assert(error == 0, $"[PREVIEWER][gl.error] 有残留的 GL 错误 code=0x{error:X}");

        byte[]? pixels = ReadFrameBuffer(gl, width, height);
        if (pixels is null)
        {
            return;
        }

        DebugCube.CheckRendered(GlCubeRenderer.Vertices, pixels, width, height, _camera, ClearColor);
    }

    private int _pointerMoves;

    // 输入探针：把「控件本身收到了什么」和「链路把它翻成了什么」分成两段来记。
    // 两段断掉在日志里的样子一模一样（都是安静），但原因差着十万八千里——
    // 前一段断是命中测试、焦点、或者平台后端没把消息送上来；后一段断是订阅漏了或者映射写错了。
    // 只查后一段（input.wheel / event.scrolled）会让人一直在适配器里找，而问题在更前面。
    //
    // 它不转发、不处理，只记录。挂载点选在输入适配器的构造函数里：那一行的意思是
    // 「有人打算处理输入了」，此时才需要知道输入有没有来。
    internal void AttachInputProbe()
    {
        Debug.WriteLine(
            $"[PREVIEWER][input.probe] attach hitTestVisible={IsHitTestVisible} focusable={Focusable} " +
            $"enabled={IsEnabled} bounds={Bounds.Width}x{Bounds.Height}");

        PointerEntered += (_, _) => Debug.WriteLine("[PREVIEWER][input.probe] pointer.entered");
        PointerExited += (_, _) => Debug.WriteLine("[PREVIEWER][input.probe] pointer.exited");
        GotFocus += (_, _) => Debug.WriteLine("[PREVIEWER][input.probe] focus.got");
        LostFocus += (_, _) => Debug.WriteLine("[PREVIEWER][input.probe] focus.lost");

        PointerPressed += (_, e) =>
        {
            Point position = e.GetPosition(this);
            Debug.WriteLine(
                $"[PREVIEWER][input.probe] pointer.pressed kind=" +
                $"{e.GetCurrentPoint(this).Properties.PointerUpdateKind} pos=({position.X:F0},{position.Y:F0})");
        };

        PointerReleased += (_, e) =>
        {
            Point position = e.GetPosition(this);
            Debug.WriteLine(
                $"[PREVIEWER][input.probe] pointer.released pos=({position.X:F0},{position.Y:F0})");
        };

        PointerWheelChanged += (_, e) =>
        {
            Point position = e.GetPosition(this);
            Debug.WriteLine(
                $"[PREVIEWER][input.probe] pointer.wheel delta=({e.Delta.X},{e.Delta.Y}) " +
                $"pos=({position.X:F0},{position.Y:F0}) handled={e.Handled}");
        };

        // 移动一秒能来几百条，逐条打会把终端冲掉。首条加每 60 条一条，
        // 足以回答「移动到底到没到控件」这一个是非题。
        PointerMoved += (_, e) =>
        {
            _pointerMoves++;
            if (_pointerMoves == 1 || _pointerMoves % 60 == 0)
            {
                Point position = e.GetPosition(this);
                Debug.WriteLine(
                    $"[PREVIEWER][input.probe] pointer.moved count={_pointerMoves} " +
                    $"pos=({position.X:F0},{position.Y:F0})");
            }
        };
    }

    // 整张 framebuffer 读回来。宽高用物理像素，与视口一致。
    private static byte[]? ReadFrameBuffer(GlInterface gl, int width, int height)
    {
        IntPtr entry = gl.GetProcAddress("glReadPixels");
        if (entry == IntPtr.Zero)
        {
            Debug.WriteLine("[PREVIEWER][gl.readback] glReadPixels 取不到，跳过");
            return null;
        }

        ReadPixels readPixels = Marshal.GetDelegateForFunctionPointer<ReadPixels>(entry);
        int byteCount = width * height * 4;
        IntPtr buffer = Marshal.AllocHGlobal(byteCount);
        try
        {
            readPixels(0, 0, width, height, GlConsts.GL_RGBA, GlConsts.GL_UNSIGNED_BYTE, buffer);

            byte[] pixels = new byte[byteCount];
            Marshal.Copy(buffer, pixels, 0, byteCount);
            return pixels;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // GlInterface 没有包 glGetError。读一次只能知道「出过某种错」，给不出位置，
    // 但 GL 的错误状态会一直累积到被读走为止，所以一次读等于给整条绘制路径兜了一次底。
    private static int ReadGlError(GlInterface gl)
    {
        IntPtr entry = gl.GetProcAddress("glGetError");
        if (entry == IntPtr.Zero)
        {
            return 0;
        }

        GetError getError = Marshal.GetDelegateForFunctionPointer<GetError>(entry);
        return getError();
    }

    // GlInterface 没有包这两个，只能自己从上下文里取函数地址。
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void ReadPixels(int x, int y, int width, int height, int format, int type, IntPtr pixels);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetError();
#endif
}
