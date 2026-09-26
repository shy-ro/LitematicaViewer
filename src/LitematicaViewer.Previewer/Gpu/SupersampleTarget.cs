using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.OpenGL;

namespace LitematicaViewer.Previewer.Gpu;

// 超采样离屏目标：场景先画到 Scale 倍分辨率的 FBO，再线性 blit 回 Avalonia 的
// 交换链表面。一个目标同时治三种糊——几何边锯齿、移动中的纹理闪烁（无 AA 时
// 逐帧的采样抖动）、以及 mip 采样偏糊（footprint 减半后自动选到更清晰的 mip 层）。
// 代价是像素量乘 4，对本项目的几何量（万级方块、几万面）可以忽略。
//
// R1/R5：GL 对象由本类持有并 IDisposable；重建只发生在视口尺寸变化时（与
// GlMeshRenderer.RebuildGpu 的「数据变了重建」同一语义），缺入口时整体降级直画。
internal sealed class SupersampleTarget : IDisposable
{
    // 2x 已把锯齿压到不可见；再高收益趋零而像素量按平方涨。
    public const int Scale = 2;

    // GlConsts 没有的 GLES 常量（数值来自规范）：DEPTH_COMPONENT24、attachment 组。
    private const int GlRenderbuffer = GlRaw.GL_RENDERBUFFER;
    private const int GlColorAttachment0 = GlRaw.GL_COLOR_ATTACHMENT0;
    private const int GlDepthAttachment = GlRaw.GL_DEPTH_ATTACHMENT;
    private const int GlDepthComponent24 = GlRaw.GL_DEPTH_COMPONENT24;
    private const int GlFramebufferComplete = GlRaw.GL_FRAMEBUFFER_COMPLETE;

    // 缺任何一个入口都玩不转整套 FBO，逐个探测，缺了整体降级——上层拿 null 就走原路径。
    private static readonly string[] RequiredEntries =
    [
        "glGenFramebuffers", "glBindFramebuffer", "glFramebufferTexture2D",
        "glGenRenderbuffers", "glBindRenderbuffer", "glRenderbufferStorage",
        "glFramebufferRenderbuffer", "glDeleteFramebuffers", "glDeleteRenderbuffers",
        "glCheckFramebufferStatus", "glBlitFramebuffer"
    ];

    private readonly GlInterface _gl;
    private int _color;
    private int _depth;
    private bool _disposed;
    private int _fbo;

    private SupersampleTarget(GlInterface gl)
    {
        _gl = gl;
    }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        ReleaseGpu();
    }

    public static SupersampleTarget? Create(GlInterface gl)
    {
        var extensions = gl.ContextInfo?.Extensions is { } list ? string.Join(" ", list) : null;
        foreach (var name in RequiredEntries)
            if (gl.GetProcAddress(name) == IntPtr.Zero)
            {
                Debug.WriteLine($"[PREVIEWER][ssaa] 入口缺失 {name}，超采样降级为直画");
                return null;
            }

        Debug.WriteLine($"[PREVIEWER][ssaa] 可用 scale={Scale} ext={extensions is not null}");
        return new SupersampleTarget(gl);
    }

    // 尺寸没变就复用：拖窗口时每帧变，重建以帧率发生是预期行为，几何才几百字节。
    public void EnsureSize(int width, int height)
    {
        if (Width == width && Height == height && _fbo != 0) return;

        ReleaseGpu();

        Width = width;
        Height = height;

        var ids = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            // 颜色附件：纹素由 blit 原样取走，filter 用 NEAREST/无 mip 都不影响结果。
            _color = _gl.GenTexture();
            _gl.BindTexture(GlConsts.GL_TEXTURE_2D, _color);
            _gl.TexImage2D(
                GlConsts.GL_TEXTURE_2D, 0, GlConsts.GL_RGBA8, width, height, 0,
                GlConsts.GL_RGBA, GlConsts.GL_UNSIGNED_BYTE, IntPtr.Zero);
            _gl.BindTexture(GlConsts.GL_TEXTURE_2D, 0);

            // 深度附件用 renderbuffer：不采样，只作深度测试的存储。
            GlRaw.GenRenderbuffers(_gl, 1, ids);
            _depth = Marshal.ReadInt32(ids);
            GlRaw.BindRenderbuffer(_gl, GlRenderbuffer, _depth);
            GlRaw.RenderbufferStorage(_gl, GlRenderbuffer, GlDepthComponent24, width, height);
            GlRaw.BindRenderbuffer(_gl, GlRenderbuffer, 0);

            GlRaw.GenFramebuffers(_gl, 1, ids);
            _fbo = Marshal.ReadInt32(ids);
            GlRaw.BindFramebuffer(_gl, GlConsts.GL_FRAMEBUFFER, _fbo);
            GlRaw.FramebufferTexture2D(
                _gl, GlConsts.GL_FRAMEBUFFER, GlColorAttachment0, GlConsts.GL_TEXTURE_2D, _color, 0);
            GlRaw.FramebufferRenderbuffer(
                _gl, GlConsts.GL_FRAMEBUFFER, GlDepthAttachment, GlRenderbuffer, _depth);

            var status = GlRaw.CheckFramebufferStatus(_gl, GlConsts.GL_FRAMEBUFFER);
            Debug.Assert(
                status == GlFramebufferComplete,
                $"[PREVIEWER][ssaa] FBO 不完整 code=0x{status:X}");
        }
        finally
        {
            Marshal.FreeHGlobal(ids);
        }

        GlRaw.BindFramebuffer(_gl, GlConsts.GL_FRAMEBUFFER, 0);
        Debug.WriteLine($"[PREVIEWER][ssaa] target {width}x{height}");
    }

    public void BindForRender()
    {
        GlRaw.BindFramebuffer(_gl, GlConsts.GL_FRAMEBUFFER, _fbo);
    }

    // 画完降采样回交换链表面，并把 GL_FRAMEBUFFER 绑回表面——后续的读回（截帧/校验）
    // 都假定当前绑着 Avalonia 给的那个 fb。
    public void ResolveTo(int defaultFramebuffer, int width, int height)
    {
        GlRaw.BindFramebuffer(_gl, GlRaw.GL_READ_FRAMEBUFFER, _fbo);
        GlRaw.BindFramebuffer(_gl, GlRaw.GL_DRAW_FRAMEBUFFER, defaultFramebuffer);
        GlRaw.BlitFramebuffer(
            _gl, 0, 0, Width, Height, 0, 0, width, height,
            GlConsts.GL_COLOR_BUFFER_BIT, GlConsts.GL_LINEAR);
        GlRaw.BindFramebuffer(_gl, GlConsts.GL_FRAMEBUFFER, defaultFramebuffer);
    }

    private void ReleaseGpu()
    {
        if (_fbo != 0)
        {
            var ids = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                Marshal.WriteInt32(ids, _fbo);
                GlRaw.DeleteFramebuffers(_gl, 1, ids);
                Marshal.WriteInt32(ids, _depth);
                GlRaw.DeleteRenderbuffers(_gl, 1, ids);
            }
            finally
            {
                Marshal.FreeHGlobal(ids);
            }

            _fbo = 0;
        }

        if (_color != 0)
        {
            _gl.DeleteTexture(_color);
            _color = 0;
        }

        _depth = 0;
    }
}
