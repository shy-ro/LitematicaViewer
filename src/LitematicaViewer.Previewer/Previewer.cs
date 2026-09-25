using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using LitematicaViewer.Previewer.Diagnostics;

namespace LitematicaViewer.Previewer;

// 用 OpenGlControlBase 而非 NativeControlHost：Avalonia 已封装 GL 上下文生命周期。
// 若日后需要多视口或自定义上下文，再换。
//
// 顺带记一条后面会咬人的事：基类的 GlVersion 只有 getter，没法要求 3.3 core 之类的具体版本。
// 上下文版本由 Avalonia 按平台后端决定，所以着色器要按拿到的版本来写指令
// （实测 ANGLE 给的是 GLES，指令得是 `#version 300 es` 那一套），不能照抄桌面 GL 的写法。
public partial class Previewer : OpenGlControlBase
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
