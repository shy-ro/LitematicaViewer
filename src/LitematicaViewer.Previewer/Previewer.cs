using System.Diagnostics;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Rendering;
using LitematicaViewer.Previewer.Diagnostics;
using LitematicaViewer.Previewer.Gpu;

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
    private GlAxesRenderer? _axes;
    private GlPedestalRenderer? _pedestal;
    private GlHighlightRenderer? _highlight;
    private GlMeshRenderer? _meshRenderer;
    private SupersampleTarget? _ssaa;

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
    //
    // 入参是**控件自己的坐标**。这一条到 Phase F 才确定：那时控件从窗口左上角挪到了侧边栏右边，
    // 而原来这里按「入参是 TopLevel 坐标」又转了一次（root.TranslatePoint(point, this)），
    // 于是左边缘那 300 个像素的命中被整个减没了——真实输入落在那一段时控件收不到任何东西。
    //
    // 它一直是错的，只是控件在原点时「转一次」正好等于不转（偏移是 0,0），
    // 所以从外观上验不出差别；换个布局才暴露。实测那一行的入参：探针在窗口坐标 (790,400) 提问，
    // 这里收到 (490,400)，正好差控件左边界的 300。
    bool ICustomHitTest.HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

#if DEBUG
    // 整张 framebuffer 读回来会强制 GPU 同步，逐帧读会把帧率打到地板上，
    // 所以只在相机变化时读，且总数封顶。换了相机就是换了一张画面，值得验一次；
    // 相机没变时同一张画面验两遍不给新信息。
    //
    // 6 改 24：Phase F 起每帧都可能换相机（按住 W 时就是），6 次在第一秒就被一个走动花光了，
    // 之后整个会话都不再抽样。24 仍然是「一次会话」的预算而不是每秒的——
    // 每次读回是几 MB 加一次同步，二十几次摊在一个会话里可以忽略，而摊不了帧率。
    private const int MaxCameraVerifications = 24;

    private int _verifiedCameraVersion = -1;
    private int _cameraVerifications;
    private bool _budgetExhaustedLogged;
    private int _skipLogs;
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

        // 两个渲染器各自设自己需要的 GL 状态（立方体开深度测试，轴线开混合），都在这里设一次，
        // 顺序只影响日志先后的可读性。轴线后建是因为它画在立方体之后——
        // 深度相等的那几个像素（轴线正好从面心穿出去的地方）归先画的那个。
        _axes = GlAxesRenderer.Create(gl);

        // 光环和另外两个一起建、常驻，只在展台模式下画：模式切换不该创建或销毁 GPU 资源（R5）。
        _pedestal = GlPedestalRenderer.Create(gl);

        // 拾取高亮框：常驻，只有 SetHighlight 传了位置才画。
        _highlight = GlHighlightRenderer.Create(gl);

        // 网格渲染器跨上下文持有数据：OnOpenGlLost 之后宿主不需要重新装填，
        // 所以这里只在第一次建，恢复场景下沿用旧实例（渲染器内部自愈）。
        if (_meshRenderer is null)
        {
            _meshRenderer = GlMeshRenderer.Create(gl);

            // init 之前到达的装填在这里补上。之后再有 SetMesh 直接走渲染器。
            if (_pendingMesh is { } pending)
            {
                _pendingMesh = null;
                _meshRenderer.Load(
                    pending.Vertices, pending.Indices, pending.AtlasLevels, pending.Width, pending.Height);
            }
        }

        // 超采样：先画进 2x FBO 再降采样回交换链。入口不齐时返回 null，渲染路径原样直画。
        _ssaa = SupersampleTarget.Create(gl);
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

        // 超采样把渲染分辨率乘 Scale：aspect 不变，投影矩阵对宽高只是取比值，
        // 各渲染器拿到的宽高换成放大后的值即可。
        int renderWidth = _ssaa is null ? width : width * SupersampleTarget.Scale;
        int renderHeight = _ssaa is null ? height : height * SupersampleTarget.Scale;

        // 显式绑一次画布：超采样时是我们的 2x FBO，否则是 Avalonia 交给的那个表面。
        // 省掉这一步不会立刻报错，症状是画到别处去了，而屏幕上什么都没有。
        if (_ssaa is null)
        {
            gl.BindFramebuffer(GlConsts.GL_FRAMEBUFFER, fb);
        }
        else
        {
            _ssaa.EnsureSize(renderWidth, renderHeight);
            _ssaa.BindForRender();
        }

        gl.Viewport(0, 0, renderWidth, renderHeight);
        gl.Clear(GlConsts.GL_COLOR_BUFFER_BIT | GlConsts.GL_DEPTH_BUFFER_BIT);

        // 装了模型就不再画演示立方体：立方体是「什么都没有时的参照物」，
        // 和真模型同时画只会互相穿插。axes 在两种模式下都画（xyz 参考线是独立功能）。
        if (_meshRenderer is { HasMesh: true } mesh)
        {
            mesh.Render(gl, _camera, renderWidth, renderHeight);
        }
        else
        {
            _cube?.Render(_camera, renderWidth, renderHeight);
        }

        // 轴线接着画：落在立方体里的那一段被深度测试挡住，露在外面的是从方块里伸出来的三根轴。
        _axes?.Render(_camera, renderWidth, renderHeight);

        // 拾取高亮框：线框 + 深度测试，被模型挡住的边自然看不见。位置没设就不画。
        if (_highlightPosition is { } highlightBlock)
        {
            _highlight?.Render(_camera, renderWidth, renderHeight, highlightBlock);
        }

        // 光环最后画。半透明的三个东西（轴线、光环）只有按「从远到近」画才对得上，
        // 而光环压在底面上、比它绕着的那块模型更靠前，所以它在轴线之后。
        // 反过来时，光环与轴线交叠的那几百个像素会先被光环写一遍、再被轴线混一遍，
        // 深度上就成了「轴线在光环前面」——两处都是半透明，画面看起来只是「有点怪」。
        if (_pedestalVisible)
        {
            _pedestal?.Render(_camera, renderWidth, renderHeight, _pedestalCentre, _pedestalRadius, _pedestalBaseY);
        }

        // 降采样回交换链：LINEAR 把 2x 的过采样平均掉，等于内置了一层抗锯齿。
        if (_ssaa is { } ssaa)
        {
            ssaa.ResolveTo(fb, width, height);
        }

        gl.Viewport(0, 0, width, height);

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
        if (_cameraVersion != _verifiedCameraVersion)
        {
            // 同一个姿态只判一次：没换相机时重算是白算，而读回那一下是强制 GPU 同步。
            _verifiedCameraVersion = _cameraVersion;
            VerifyFrame(gl, width, height);
        }
#endif

        // 截帧诊断（--shot）：这一帧已经画完、还在当前上下文里，此刻读回是唯一可靠的时机。
        // 回调在渲染线程（也就是 UI 线程）上执行，宿主拿去存盘后自行退出。
        if (_capture is { } capture)
        {
            _capture = null;
            byte[]? pixels = GlRaw.ReadPixels(gl, 0, 0, width, height);
            Debug.Assert(pixels is not null, "[PREVIEWER][gl.shot] glReadPixels 没拿到入口");
            capture(pixels ?? [], width, height);
        }
    }

    // 只截一次：请求之后的第一个渲染帧末尾读回并回调，然后自动清空。
    private Action<byte[], int, int>? _capture;

    // public 而不是 internal：控件的对外成员都在这条公开面上（Sample 是另一个程序集）。
    public void RequestCapture(Action<byte[], int, int> onCaptured) => _capture = onCaptured;

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
        _pedestal?.Dispose();
        _pedestal = null;
        _highlight?.Dispose();
        _highlight = null;
        _axes?.Dispose();
        _axes = null;
        _cube?.Dispose();
        _cube = null;
        _meshRenderer?.Dispose();
        _meshRenderer = null;
        _ssaa?.Dispose();
        _ssaa = null;

        base.OnOpenGlDeinit(gl);
    }

    protected override void OnOpenGlLost()
    {
        // 上下文丢了，GPU 侧的对象随之消失，此时再发 Delete* 就是对着失效的函数指针发号施令。
        // 所以只丢引用、不发 GL 调用，等下一次 Init 重建。
        Debug.WriteLine($"[PREVIEWER][gl.lost] frames={_framesRendered} expected=之后会再来一次 gl.init");
        _pedestal?.Abandon();
        _pedestal = null;
        _highlight?.Abandon();
        _highlight = null;
        _axes?.Abandon();
        _axes = null;
        _cube?.Abandon();
        _cube = null;
        _ssaa?.Dispose();
        _ssaa = null;

        // 网格渲染器是唯一不置 null 的：托管副本在它手里，置 null 就等于要宿主重新装填。
        // 下一次 init 沿用旧实例，下一次 Render 自愈（惰性重建缓冲与着色器）。
        _meshRenderer?.Abandon();

        base.OnOpenGlLost();
    }

#if DEBUG
    private void VerifyFrame(GlInterface gl, int width, int height)
    {
        int error = GlRaw.GetError(gl);
        Debug.WriteLine(
            $"[PREVIEWER][gl.error] code=0x{error:X} expected=0x0 cameraVersion={_cameraVersion}");
        Debug.Assert(error == 0, $"[PREVIEWER][gl.error] 有残留的 GL 错误 code=0x{error:X}");

        // 预算得真的在这里拦一道，而且必须在读回**之前**——读回才是贵的那一步
        // （强制 GPU 同步加拷几 MB 回来，后面还跟着每帧五百多万次像素距离计算）。
        //
        // 这个常量一度只声明、只自增、从没被判断过，于是注释里写的「一次会话封顶 24 次」
        // 实际是「相机每变一次读回一帧」——转视角和按住 W 时相机每帧都在变，那就是每秒六十次读回。
        // 症状是「一动鼠标就卡」，而它看起来像渲染慢，不像校验慢。
        if (_cameraVerifications >= MaxCameraVerifications)
        {
            // 只说一次。用尽之后每个可验的帧都打一条的话，日志反而比没预算之前更长。
            if (!_budgetExhaustedLogged)
            {
                _budgetExhaustedLogged = true;
                Debug.WriteLine(
                    $"[PREVIEWER][gl.readback] 读回预算用尽 {MaxCameraVerifications} 次，" +
                    $"本次会话不再做画面校验 cameraVersion={_cameraVersion}");
            }

            return;
        }

        // mesh 模式没有像素断言可做：DebugCube 的全部判据（六面法线色、面像素数、
        // 轮廓几何）都建立在「画面是那个已知立方体」上，真模型的画面不属于任何一条。
        // GL 错误检查在上面已经做过了，剩下的读回不做、预算不花。
        if (_meshRenderer is { HasMesh: true })
        {
            if (_skipLogs++ % CameraSetLogInterval == 0)
            {
                Debug.WriteLine(
                    $"[PREVIEWER][gl.readback] mesh 模式跳过立方体像素校验 " +
                    $"cameraVersion={_cameraVersion} note=每{CameraSetLogInterval}条一条");
            }

            return;
        }

        // 姿态不在校验适用的范围内就跳过，并且明说跳过了。
        // 「跳过」和「通过」在日志里必须是两句不同的话：相机可以自由转向、可以平移、可以无界缩放，
        // 有相当一部分姿态本来就验不了，把两者混成一片安静等于把这条检查整个作废。
        if (!DebugCube.IsVerifiable(_camera, width, height, out string reason))
        {
            // 跳过这件事在自由导航里是**连续**发生的：按住 W 走一秒就是几十帧，帧帧都跳。
            // 逐帧打的话这几行会把真正有用的东西挤出去（Phase F 之前相机只能沿一条固定的
            // 体对角线推拉，几乎不会跳过，所以没暴露）。计数在真读回那里清零，
            // 于是「上一次校验之后的第一跳」一定会打出来，那正是要看的那一条。
            if (_skipLogs++ % CameraSetLogInterval == 0)
            {
                Debug.WriteLine(
                    $"[PREVIEWER][gl.readback] 跳过画面校验 cameraVersion={_cameraVersion} " +
                    $"原因={reason} note=每{CameraSetLogInterval}条一条");
            }

            return;
        }

        byte[]? pixels = GlRaw.ReadPixels(gl, 0, 0, width, height);
        if (pixels is null)
        {
            return;
        }

        // 读了才算一次。预算花在真读回上，而不是花在「判了一下但跳过了」上——
        // 后者一次同步都不产生，却会把名额吃掉，于是真正换相机的那些帧反而没验。
        _cameraVerifications++;
        _skipLogs = 0;
        DebugCube.CheckRendered(
            GlCubeRenderer.Vertices,
            pixels,
            width,
            height,
            _camera,
            ClearColor,
            _pedestalVisible);
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
            Point position = e.GetPosition(this);
            if (_pointerMoves == 1 || _pointerMoves % 60 == 0)
            {
                Debug.WriteLine(
                    $"[PREVIEWER][input.probe] pointer.moved count={_pointerMoves} " +
                    $"pos=({position.X:F0},{position.Y:F0})");
            }

        };
    }


    // 这里原本还有 ReadFrameBuffer 与 ReadGlError：GlInterface 没有包 glReadPixels 与 glGetError，
    // 那两条自己从上下文取函数地址、自己配委托。轴线渲染器也要用同一条路（混合与线宽），
    // 于是这类入口统一挪到了 Gpu/GlRaw.cs——同一件事有两个写法时，签名写错的那一份
    // 要等到运行期把栈搅乱才暴露，而它长得跟另一份一模一样。
#endif


    // 拾取链路的入口事件（Release 同样要发），所以装在 DEBUG 探针区之外：
    // [Conditional("DEBUG")] 的丢弃发生在语义分析之后，但 #if DEBUG 是预处理期的事——
    // 区内的订阅在 Release 下根本不存在，事件就成了「声明了但从没人用」。
    // 静态构造一次订阅，永远发。PointerExited 报 NaN 让宿主知道指针已经离开视口。
    // 移动一秒几百条，宿主自己节流：存最新位置、Tick 里每帧算一次拾取即可。
    internal void AttachHoverEvents()
    {
        PointerMoved += (_, e) =>
        {
            Point position = e.GetPosition(this);
            HoverMoved?.Invoke(new Avalonia.Vector(position.X, position.Y));
        };

        PointerExited += (_, _) => HoverMoved?.Invoke(new Avalonia.Vector(double.NaN, double.NaN));
    }
}
