using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;

namespace LitematicaViewer.Previewer;

// 用 OpenGlControlBase 而非 NativeControlHost：Avalonia 已封装 GL 上下文生命周期。
// 若日后需要多视口或自定义上下文，再换。
//
// 顺带记一条后面会咬人的事：基类的 GlVersion 只有 getter，没法要求 3.3 core 之类的具体版本。
// 上下文版本由 Avalonia 按平台后端决定，所以 Phase C 的着色器要按拿到的版本来写指令
// （ANGLE 给的是 GLES，指令得是 `#version 300 es` 那一套），不能照抄桌面 GL 的写法。
public class Previewer : OpenGlControlBase
{
    // 背景取蓝不取白：立方体的六个面按法线着色，全是浅色，白底上会糊成一片。
    // 也不取纯黑：纯黑与「这一帧什么都没画出来」在截图里分不开。
    private const float ClearR = 0.12f;
    private const float ClearG = 0.30f;
    private const float ClearB = 0.55f;

    // 逐帧打桩会把终端冲掉，而上下文稳不稳定只要抽样看就够。
    private const int ProbeFrameInterval = 60;

    // R4 的守卫。控件在 UI 线程上构造，之后每次 GL 回调都要求落在同一线程。
    // GL 上下文不跨线程，而这个错误通常不抛异常，只在某些机器上偶发黑屏或花屏。
    private readonly int _uiThreadId = Environment.CurrentManagedThreadId;

    private int _framesRendered;

#if DEBUG
    // 只读一次。ReadPixels 会强制 GPU 同步，逐帧读会把帧率打到地板上，
    // 而「清屏色对不对」只需要答一次。
    private bool _frameBufferChecked;
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
        gl.ClearColor(ClearR, ClearG, ClearB, 1f);

        Debug.WriteLine(
            $"[PREVIEWER][gl.init] version='{gl.Version}' renderer='{gl.Renderer}' vendor='{gl.Vendor}'");
        Debug.WriteLine(
            $"[PREVIEWER][gl.init] clearColor=({ClearR},{ClearG},{ClearB},1) expected=(0.12,0.3,0.55,1)");

        if (gl.ContextInfo is { } info)
        {
            Debug.WriteLine(
                $"[PREVIEWER][gl.init] profile={info.Version.Type} gl={info.Version.Major}.{info.Version.Minor} " +
                $"compatibility={info.Version.IsCompatibilityProfile} extensions={info.Extensions.Count}");
        }

        Debug.WriteLine(
            $"[PREVIEWER][gl.init] cap.vao={gl.IsBindVertexArrayAvailable} cap.blit={gl.IsBlitFramebufferAvailable} " +
            $"cap.drawBuffer={gl.IsDrawBufferAvailable}");
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        _framesRendered++;

        // 视口按物理像素算，Bounds 是 DIP。差一个 RenderScaling 在 100% 缩放的显示器上
        // 完全看不出来，只在缩放显示器上把画面缩进一角——那时已经在查别的地方了。
        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        int width = Math.Max(1, (int)Math.Round(Bounds.Width * scaling));
        int height = Math.Max(1, (int)Math.Round(Bounds.Height * scaling));

        // 显式绑一次 Avalonia 交给我们的那个 framebuffer。省掉这一步不会立刻报错，
        // 症状是画到别处去了，而屏幕上什么都没有。
        gl.BindFramebuffer(GlConsts.GL_FRAMEBUFFER, fb);
        gl.Viewport(0, 0, width, height);
        gl.Clear(GlConsts.GL_COLOR_BUFFER_BIT);

        if (_framesRendered == 1 || _framesRendered % ProbeFrameInterval == 0)
        {
            Debug.WriteLine(
                $"[PREVIEWER][gl.render] frame={_framesRendered} fb={fb} bounds={Bounds.Width}x{Bounds.Height} " +
                $"scaling={scaling} viewport={width}x{height}");
        }

        // 这一段是约定里说的例外：断言本身在 Release 下会消失，但读回那一下 GPU 同步不会，
        // 所以连调用点一起包掉。其余探针都不需要 #if DEBUG。
#if DEBUG
        if (!_frameBufferChecked)
        {
            _frameBufferChecked = true;
            CheckFrameBufferCenter(gl, width, height);
        }
#endif
    }

#if DEBUG
    // 「窗口显示，背景纯色」这条验收项没法靠读日志证明，所以把 framebuffer 中心的像素读回来。
    // 这同时也证明了绑的是 Avalonia 交给我们的那个 framebuffer：绑错的话读回来的是别处的颜色。
    private static void CheckFrameBufferCenter(GlInterface gl, int width, int height)
    {
        IntPtr entry = gl.GetProcAddress("glReadPixels");
        if (entry == IntPtr.Zero)
        {
            Debug.WriteLine("[PREVIEWER][gl.readback] glReadPixels 取不到，跳过");
            return;
        }

        ReadPixels readPixels = Marshal.GetDelegateForFunctionPointer<ReadPixels>(entry);
        IntPtr pixel = Marshal.AllocHGlobal(4);
        try
        {
            readPixels(width / 2, height / 2, 1, 1, GlConsts.GL_RGBA, GlConsts.GL_UNSIGNED_BYTE, pixel);

            int r = Marshal.ReadByte(pixel, 0);
            int g = Marshal.ReadByte(pixel, 1);
            int b = Marshal.ReadByte(pixel, 2);
            int a = Marshal.ReadByte(pixel, 3);

            // 实测读回 (31,76,140,255)。0.12/0.30/0.55 × 255 = 30.6/76.5/140.25，
            // 同一批里 30.6 进位成 31 而 76.5 舍成 76，取整规则从数值上推不出来，
            // 所以留 1 的余量，而不是把某个后端的取整方式当成规范。
            Debug.WriteLine(
                $"[PREVIEWER][gl.readback] center=({r},{g},{b},{a}) expected=(31,76,140,255)±1");
            Debug.Assert(
                Math.Abs(r - 31) <= 1 && Math.Abs(g - 76) <= 1 && Math.Abs(b - 140) <= 1 && a == 255,
                $"[PREVIEWER][gl.readback] 屏上颜色不是设定的清屏色 center=({r},{g},{b},{a})");
        }
        finally
        {
            Marshal.FreeHGlobal(pixel);
        }
    }

    // GlInterface 没有包 glReadPixels，只能自己从上下文里取函数地址。
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void ReadPixels(int x, int y, int width, int height, int format, int type, IntPtr pixels);
#endif

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        Debug.WriteLine($"[PREVIEWER][gl.deinit] frames={_framesRendered} expected=>0");
        Debug.Assert(
            _framesRendered > 0,
            $"[PREVIEWER][gl.deinit] 挂上去了却一帧没画 frames={_framesRendered}");

        base.OnOpenGlDeinit(gl);
    }

    protected override void OnOpenGlLost()
    {
        // 上下文丢失后所有 GPU 资源都作废。本阶段没有资源，只记一笔；
        // 从 Phase C 起，这里必须把 GlResourceManager 持有的东西全部标成待重建。
        Debug.WriteLine($"[PREVIEWER][gl.lost] frames={_framesRendered}");

        base.OnOpenGlLost();
    }
}
