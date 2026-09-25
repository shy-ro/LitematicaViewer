using System.Diagnostics;
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
    // 清屏色不取纯黑：纯黑与「这一帧什么都没画出来」在截图里分不开，
    // 而「背景是不是这个颜色」正是本阶段的验收项。
    private const float ClearR = 0.10f;
    private const float ClearG = 0.12f;
    private const float ClearB = 0.16f;

    // 逐帧打桩会把终端冲掉，而上下文稳不稳定只要抽样看就够。
    private const int ProbeFrameInterval = 60;

    // R4 的守卫。控件在 UI 线程上构造，之后每次 GL 回调都要求落在同一线程。
    // GL 上下文不跨线程，而这个错误通常不抛异常，只在某些机器上偶发黑屏或花屏。
    private readonly int _uiThreadId = Environment.CurrentManagedThreadId;

    private int _framesRendered;

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
            $"[PREVIEWER][gl.init] clearColor=({ClearR},{ClearG},{ClearB},1) expected=(0.1,0.12,0.16,1)");

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
    }

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
