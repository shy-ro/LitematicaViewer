using Avalonia.OpenGL;
using System.Runtime.InteropServices;

namespace LitematicaViewer.ShellPreview;

// 在自己的子窗口上建 ANGLE(EGL) 上下文，包成 Avalonia 的 GlInterface 交给
// Previewer 那套 GPU 渲染零件（GlMeshRenderer/GlPedestalRenderer/SupersampleTarget）。
//
// 为什么是 ANGLE 而不是 WGL：那套零件的着色器是 `#version 300 es` + 精度限定符，
// 按预览器的 ANGLE GLES 3.0 环境写的；桌面 GL 的预编译主版本对不上，改着色器
// 就是让两处共享源分叉。Avalonia 12 的 ANGLE 打成了单文件 av_libglesv2.dll
// （egl 与 gles 导出在同一个 dll），装机时它就在我们的 dll 旁边，显式 LoadLibrary。
//
// 生命周期（R1/R5）：display/surface/context 由本类持有并 IDisposable；
// GL 对象由各渲染器持有，只在 Dispose 路径销毁。所有 EGL/GL 调用都在
// 创建它的 STA 线程上发生（R4）——调用方保证。
internal sealed class EglHost : IDisposable
{
    private const int EglAlphaSize = 0x3021;
    private const int EglBlueSize = 0x3022;
    private const int EglGreenSize = 0x3023;
    private const int EglRedSize = 0x3024;
    private const int EglDepthSize = 0x3025;
    private const int EglSurfaceType = 0x3033;
    private const int EglWindowBit = 0x0004;
    private const int EglRenderableType = 0x3040;
    private const int EglOpenGlEs2Bit = 0x0040; // ES3 的位与 ES2 相同
    private const int EglNone = 0x3038;
    private const int EglOpenGlEsApi = 0x30A0;
    private const int EglContextMajorVersion = 0x3098;
    private const int EglContextMinorVersion = 0x30FB;

    private nint _lib;
    private nint _display;
    private nint _surface;
    private nint _context;
    private SwapBuffersDelegate? _swap;

    public GlInterface? Gl { get; private set; }

    // 上下文就绪后GL 渲染可用。surface/context 是窗口死了一起作废的东西，
    // 窗格每次 Show 都重建（Unload 销毁），不处理上下文丢失（prevhost 生命周期短）。
    public bool Init(nint hwnd)
    {
        var moduleDirectory = SceneLoader.SelfModuleDirectory();
        var libraryPath = Path.Combine(moduleDirectory, "av_libglesv2.dll");
        _lib = LoadLibraryW(File.Exists(libraryPath) ? libraryPath : "av_libglesv2.dll");
        if (_lib == 0)
        {
            Exports.Log($"egl: av_libglesv2.dll not found at {libraryPath}");
            return false;
        }

        // Avalonia 的 ANGLE 单文件构建把 EGL 入口改名成 EGL_Xxx（eglGetDisplay →
        // EGL_GetDisplay），gl* 保持原名——导出表实测。所以 egl 入口查表要换名。
        var getDisplay = GetExport<GetDisplayDelegate>(GetEglName("eglGetDisplay"));
        var initialize = GetExport<InitializeDelegate>(GetEglName("eglInitialize"));
        var chooseConfig = GetExport<ChooseConfigDelegate>(GetEglName("eglChooseConfig"));
        var bindApi = GetExport<BindApiDelegate>(GetEglName("eglBindAPI"));
        var createContext = GetExport<CreateContextDelegate>(GetEglName("eglCreateContext"));
        var createWindowSurface = GetExport<CreateWindowSurfaceDelegate>(GetEglName("eglCreateWindowSurface"));
        var makeCurrent = GetExport<MakeCurrentDelegate>(GetEglName("eglMakeCurrent"));
        _swap = GetExport<SwapBuffersDelegate>(GetEglName("eglSwapBuffers"));
        var swapInterval = GetExport<SwapIntervalDelegate>(GetEglName("eglSwapInterval"));
        var destroySurface = GetExport<DestroySurfaceDelegate>(GetEglName("eglDestroySurface"));
        var destroyContext = GetExport<DestroyContextDelegate>(GetEglName("eglDestroyContext"));
        var getError = GetExport<GetErrorDelegate>(GetEglName("eglGetError"));
        _destroySurface = destroySurface;
        _destroyContext = destroyContext;

        _display = getDisplay(0); // EGL_DEFAULT_DISPLAY
        if (_display == 0 || initialize(_display, out var major, out var minor) == 0)
        {
            Exports.Log($"egl: display/init failed display={_display} err=0x{getError():X}");
            return false;
        }

        Exports.Log($"egl: display ok version={major}.{minor}");

        int[] attribs =
        [
            EglRedSize, 8,
            EglGreenSize, 8,
            EglBlueSize, 8,
            EglAlphaSize, 8,
            EglDepthSize, 24,
            EglSurfaceType, EglWindowBit,
            EglRenderableType, EglOpenGlEs2Bit,
            EglNone
        ];
        if (chooseConfig(_display, attribs, out var config, 1, out var count) == 0 || count < 1)
        {
            Exports.Log($"egl: chooseConfig failed err=0x{getError():X}");
            return false;
        }

        bindApi(EglOpenGlEsApi);
        int[] contextAttribs = [EglContextMajorVersion, 3, EglContextMinorVersion, 0, EglNone];
        _context = createContext(_display, config, 0, contextAttribs);
        _surface = createWindowSurface(_display, config, hwnd, 0);
        if (_context == 0 || _surface == 0)
        {
            Exports.Log($"egl: context/surface failed ctx={_context} surface={_surface} err=0x{getError():X}");
            return false;
        }

        if (makeCurrent(_display, _surface, _surface, _context) == 0)
        {
            Exports.Log($"egl: makeCurrent failed err=0x{getError():X}");
            return false;
        }

        // timer 驱动的帧率本来就低于刷新率，关掉 vsync 等待省一截延迟。
        swapInterval(_display, 0);

        try
        {
        // GlInterface 的入口解析：核心函数直接问模块导出表（gl* 原名，查不到再试
        // GL_ 前缀与 eglGetProcAddress），扩展函数走 eglGetProcAddress。
        // 注意这里的 GetProcAddress 是 GL 语义（名字不带 W 后缀问题——那是 kernel32
        // 那条 import 的坑，与本处无关）。
        var eglGetProcAddress = GetExport<GetProcAddressDelegate>(GetEglName("eglGetProcAddress"));
        _eglGetProcAddress = eglGetProcAddress;
        Gl = new GlInterface(
            new GlVersion(GlProfileType.OpenGLES, 3, 0),
            ResolveProc);
        }
        catch (Exception ex)
        {
            Exports.Log($"egl: GlInterface init failed {ex.GetType().Name} {ex.Message}");
            return false;
        }

        Exports.Log($"egl: ready version='{Gl.Version}' renderer='{Gl.Renderer}'");
        return true;
    }

    // GlInterface 的入口解析：核心 gl* 直接问模块导出表，查不到落到
    // eglGetProcAddress（扩展入口只从这条路来）。
    private nint ResolveProc(string name)
    {
        var proc = GetProcAddress(_lib, name);
        if (proc != 0) return proc;
        proc = GetProcAddress(_lib, "GL_" + name);
        if (proc != 0) return proc;
        return _eglGetProcAddress?.Invoke(name) ?? 0;
    }

    // eglXxx → EGL_Xxx：Avalonia 的 ANGLE 构建给 EGL 入口换过导出名（实测导出表）。
    private static string GetEglName(string eglName) => "EGL_" + eglName[3..];

    public bool Swap()
    {
        return _swap is { } swap && swap(_display, _surface) != 0;
    }

    public void Dispose()
    {
        if (_display != 0)
        {
            if (_surface != 0) _destroySurface?.Invoke(_display, _surface);
            if (_context != 0) _destroyContext?.Invoke(_display, _context);
        }

        // 库不卸载：进程还活着时 GL 的线程状态（TLS）指着它，FreeLibrary 会踩空。
        Gl = null;
        _surface = 0;
        _context = 0;
        _display = 0;
    }

    private DestroySurfaceDelegate? _destroySurface;
    private DestroyContextDelegate? _destroyContext;
    private GetProcAddressDelegate? _eglGetProcAddress;

    private T GetExport<T>(string name) where T : Delegate
    {
        var proc = GetProcAddress(_lib, name);
        return proc != 0
            ? Marshal.GetDelegateForFunctionPointer<T>(proc)
            : throw new InvalidOperationException($"egl export missing: {name}");
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint GetDisplayDelegate(nint nativeDisplay);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int InitializeDelegate(nint display, out int major, out int minor);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ChooseConfigDelegate(
        nint display, int[] attribs, out nint config, int maxConfigs, out int numConfigs);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int BindApiDelegate(int api);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint CreateContextDelegate(nint display, nint config, nint share, int[] attribs);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint CreateWindowSurfaceDelegate(nint display, nint config, nint hwnd, nint attribs);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MakeCurrentDelegate(nint display, nint draw, nint read, nint context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SwapBuffersDelegate(nint display, nint surface);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SwapIntervalDelegate(nint display, int interval);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DestroySurfaceDelegate(nint display, nint surface);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DestroyContextDelegate(nint display, nint context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetErrorDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint GetProcAddressDelegate(string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadLibraryW(string path);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern nint GetProcAddress(nint module, string name);
}
