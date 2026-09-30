using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Avalonia.OpenGL;
using LitematicaViewer.Previewer;
using LitematicaViewer.Previewer.Gpu;

namespace LitematicaViewer.ShellPreview;

// 预览处理器本体：一个塞进预览窗格的子窗口 + GPU 渲染循环。
// 渲染零件（GlMeshRenderer/GlPedestalRenderer/SupersampleTarget）与展台相机
// （CameraModel + Turntable）都是从 Sample 抄的同一套，GL 上下文由 EglHost 在
// 子窗口上用 ANGLE 建立——与预览器同为 GLES 3.0，着色器零改动。
// 交互 = 左键拖动绕模型公转（带惯性，静置后缓慢自转）、滚轮沿视线推拉。
// 渲染在 STA 线程同步做（timer 里驱动）；重活的加载/建网格在后台线程，
// 完成后用消息交棒。
[GeneratedComClass]
internal sealed unsafe partial class PreviewHandler : IPreviewHandler, IInitializeWithFile
{
    private const uint WmAppLoaded = 0x8000; // WM_APP
    private const uint WmTimer = 0x0113;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmMouseMove = 0x0200;
    private const uint WmMouseWheel = 0x020A;
    private const uint WmSize = 0x0005;
    private const uint WmDestroy = 0x0002;
    private const uint WmPaint = 0x000F;
    private const uint WmEraseBkgnd = 0x0014;

    // 背景蓝与预览器同一个颜色：浅色模型在白底上糊成一片，纯黑与「没画出来」分不开。
    private static readonly Vector3 ClearColor = new(0.12f, 0.30f, 0.55f);

    private const string ClassName = "LitematicaPreviewPane";
    private static bool _classRegistered;
    private static readonly Dictionary<nint, PreviewHandler> Handlers = new(); // 仅 STA 线程访问

    private string? _path;
    private nint _parentHwnd;
    private nint _hwnd;
    private RECT _rect;
    private int _width = 32, _height = 32;

    // GL 侧资产（R1：各自持有，Unload 统一释放；只在 STA 线程碰）。
    private EglHost? _egl;
    private GlMeshRenderer? _meshRenderer;
    private GlPedestalRenderer? _pedestal;
    private SupersampleTarget? _ssaa;
    private Turntable? _turntable;
    private bool _glReady;

    // 展台光环的落位（来自场景包围盒，装填时一并给）。
    private System.Numerics.Vector3 _centre;
    private float _radius, _baseY;

    // 后台加载结果的交棒槽（Interlocked 只能换引用类型，包一层）。
    // Generation 用来丢弃迟到的结果：结果在路上时用户又换了文件，
    // 旧场景绝不能装进新画面。
    private sealed class LoadResult
    {
        public Scene? Scene;
        public string? Error;
        public long Generation;
    }

    private LoadResult? _pendingResult;
    private string? _loadedPath;   // 已经在装或装完的文件（重入判定）
    private long _loadGeneration;
    private bool _ready;
    private string _status = "加载中…";

    private bool _needsRedraw = true;
    private bool _rendering;
    private bool _firstFrameLogged;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _lastFrameTick;
    private bool _dragging;
    private int _lastX, _lastY;

    public int Initialize(string filePath, uint grfMode)
    {
        _path = filePath;
        Exports.Log($"Initialize path={filePath} mode=0x{grfMode:X}");
        // 宿主切换文件时复用同一实例（不 Unload 不重建窗口）：路径变了就当场重装，
        // 等后面的 Show/DoPreview 再动就晚了——它们看到窗口活着什么都不做。
        EnsureCurrentFileLoaded();
        return 0; // S_OK
    }

    public int SetWindow(nint hwnd, RECT* rect)
    {
        _parentHwnd = hwnd;
        if (rect is not null)
        {
            _rect = *rect;
        }

        return 0;
    }

    public int SetRect(RECT* rect)
    {
        // SetRect 在 Show 之前来（那时子窗口还没建）：rect 必须先存着，
        // CreateChildWindow 用它定尺寸；窗口已存在时才顺势 MoveWindow。
        if (rect is not null)
        {
            _rect = *rect;
            if (_hwnd != 0)
            {
                MoveWindow(_hwnd, _rect.Left, _rect.Top,
                    _rect.Right - _rect.Left, _rect.Bottom - _rect.Top, 0);
            }
        }

        return 0;
    }

    public int Show()
    {
        try
        {
            if (_hwnd == 0)
            {
                CreateChildWindow();
                InitGl();
                StartLoad();
            }

            SetTimer(_hwnd, 1, 16, 0);
            EnsureCurrentFileLoaded();
            ShowWindow(_hwnd, 5); // SW_SHOW
        }
        catch (Exception ex)
        {
            // COM 边界上没人接异常：不拦的话宿主只看到一个 HRESULT，根因全丢。
            Exports.Log($"Show failed {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            _status = $"初始化失败：{ex.Message}";
        }

        return 0;
    }

    public int Hide()
    {
        if (_hwnd != 0)
        {
            ShowWindow(_hwnd, 0); // SW_HIDE
            KillTimer(_hwnd, 1);
        }

        return 0;
    }

    public int HandleFocus()
    {
        SetFocus(_hwnd);
        return 0;
    }

    public int QueryFocus(nint* phwnd)
    {
        *phwnd = _hwnd;
        return 0;
    }

    public int TranslateAccelerator(MSG* msg) => 1; // S_FALSE：快捷键交还宿主

    public int SetFocus()
    {
        SetFocus(_hwnd);
        return 0;
    }

    public int GetPalette(nint* hPalette)
    {
        *hPalette = 0;
        return 0;
    }

    public int DoPreview()
    {
        Exports.Log($"DoPreview path={_path}");
        // 有的宿主调用序里 DoPreview 才是「换文件」的信号（Initialize 早在建窗前
        // 来过了）：这里再兜一次重入判定，两条路谁后到都能触发重装。
        EnsureCurrentFileLoaded();
        return 0;
    }

    // 窗口活着且路径变了（或首次要装）→ 后台重装。幂等：同一路径重复调用是空操作。
    private void EnsureCurrentFileLoaded()
    {
        if (_hwnd == 0 || _path is null)
        {
            return; // 窗口没建：Show 的首次路径负责
        }

        if (string.Equals(_path, _loadedPath, StringComparison.OrdinalIgnoreCase))
        {
            return; // 已经在装或装完
        }

        StartLoad();
    }

    public int Unload()
    {
        // GL 对象先于上下文销毁：Delete* 都需要 context 还是 current 的。
        if (_glReady)
        {
            _meshRenderer?.Dispose();
            _meshRenderer = null;
            _pedestal?.Dispose();
            _pedestal = null;
            _ssaa?.Dispose();
            _ssaa = null;
            _egl?.Dispose();
            _egl = null;
            _glReady = false;
        }

        if (_hwnd != 0)
        {
            Handlers.Remove(_hwnd);
            DestroyWindow(_hwnd);
            _hwnd = 0;
        }

        return 0;
    }

    // ---------- 窗口 ----------

    private static nint GetSelfModule()
    {
        // 用本程序集的一个函数地址反查 dll 模块句柄（COM dll 没有 exe 模块可借）。
        GetModuleHandleExW(
            0x4 /*FROM_ADDRESS*/ | 0x1 /*UNCHANGED_REFCOUNT*/,
            (nint)(delegate* unmanaged<Guid*, Guid*, nint*, int>)&Exports.DllGetClassObject,
            out var module);
        return module;
    }

    private void CreateChildWindow()
    {
        var hInstance = GetSelfModule();
        if (!_classRegistered)
        {
            WNDCLASSEXW wc = new()
            {
                CbSize = (uint)sizeof(WNDCLASSEXW),
                Style = 0x0001 | 0x0002, // CS_HREDRAW | CS_VREDRAW
                WndProc = (nint)(delegate* unmanaged<nint, uint, nint, nint, nint>)&WndProcStatic,
                HInstance = hInstance,
            };
            fixed (char* name = ClassName)
            {
                wc.LpszClassName = (nint)name;
                RegisterClassExW(ref wc);
            }

            _classRegistered = true;
        }

        var w = Math.Max(16, _rect.Right - _rect.Left);
        var h = Math.Max(16, _rect.Bottom - _rect.Top);
        _width = w;
        _height = h;
        _hwnd = CreateWindowExW(
            0, ClassName, null,
            0x40000000 | 0x10000000, // WS_CHILD | WS_VISIBLE
            _rect.Left, _rect.Top, w, h,
            _parentHwnd, 0, hInstance, 0);
        Handlers[_hwnd] = this;
    }

    // GL 上下文与渲染零件只建一次（R5）：EGL surface 挂在子窗口上，
    // 窗口被销毁就整个 Unload，不存在「丢了重建」的场景（prevhost 短命）。
    private void InitGl()
    {
        _egl = new EglHost();
        if (!_egl.Init(_hwnd))
        {
            _status = "OpenGL 初始化失败";
            _egl = null;
            return;
        }

        var gl = _egl.Gl!;
        gl.ClearColor(ClearColor.X, ClearColor.Y, ClearColor.Z, 1f);
        gl.Enable(GlConsts.GL_DEPTH_TEST);

        _meshRenderer = GlMeshRenderer.Create(gl);
        _pedestal = GlPedestalRenderer.Create(gl);
        _ssaa = SupersampleTarget.Create(gl);

#if DEBUG
        CameraState.VerifyConvention();
#endif

        _glReady = true;
        Exports.Log($"gl init ok ssaa={_ssaa is not null} size={_width}x{_height}");
    }

    [UnmanagedCallersOnly]
    private static nint WndProcStatic(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        return Handlers.TryGetValue(hwnd, out var handler)
            ? handler.WndProc(msg, wParam, lParam)
            : DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private nint WndProc(uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WmSize:
                _width = Math.Max(1, (int)(short)lParam);
                _height = Math.Max(1, (int)(long)lParam >> 16);
                _needsRedraw = true;
                // timer 可能已因 Hide 被杀：复活，别让缩放后的窗格停在旧画面。
                SetTimer(_hwnd, 1, 16, 0);
                return 0;

            case WmEraseBkgnd:
                return 1; // GL 自己满幅画，别让 GDI 白刷一遍闪屏

            case WmPaint:
                OnPaint();
                return 0;

            case WmLButtonDown:
                SetFocus(_hwnd);
                _dragging = true;
                _turntable?.BeginDrag();
                _lastX = (int)(short)lParam;
                _lastY = (int)(long)lParam >> 16;
                SetCapture(_hwnd);
                return 0;

            case WmMouseMove when _dragging:
            {
                var x = (int)(short)lParam;
                var y = (int)(long)lParam >> 16;
                _turntable?.Drag(x - _lastX, y - _lastY);
                _lastX = x;
                _lastY = y;
                // Drag 只转相机不画：拖动中 Tick 恒返回 false（不积分），不置脏
                // 的话整个拖动过程渲染循环一帧不出，画面只靠系统零星 PAINT 蹦着走。
                _needsRedraw = true;
                return 0;
            }

            case WmLButtonUp:
                _dragging = false;
                _turntable?.EndDrag();
                ReleaseCapture();
                return 0;

            case WmMouseWheel:
                // 一档 = 120：滚一档沿视线推近一档（正 delta = 推近，与 Sample 一致）。
                _turntable?.Zoom((short)((long)wParam >> 16) / 120f);
                _needsRedraw = true;
                return 0;

            case WmTimer when wParam == 1:
                Tick();
                return 0;

            case WmAppLoaded:
                OnLoaded();
                return 0;

            case WmDestroy:
                Handlers.Remove(_hwnd);
                KillTimer(_hwnd, 1);
                return 0;
        }

        return DefWindowProcW(_hwnd, msg, wParam, lParam);
    }

    private void Tick()
    {
        var now = _clock.ElapsedMilliseconds;
        var dt = Math.Min((now - _lastFrameTick) / 1000.0, 0.25);
        _lastFrameTick = now;

        var moved = _turntable is { } turntable && turntable.Tick(dt);
        if (_ready && _glReady && !_rendering && (moved || _needsRedraw))
        {
            _needsRedraw = false;
            RenderFrame();
        }
    }

    private void RenderFrame()
    {
        var gl = _egl!.Gl!;
        _rendering = true;
        try
        {
            var renderWidth = _width;
            var renderHeight = _height;
            if (_ssaa is { } ssaa)
            {
                renderWidth = _width * SupersampleTarget.Scale;
                renderHeight = _height * SupersampleTarget.Scale;
                ssaa.EnsureSize(renderWidth, renderHeight);
                ssaa.BindForRender();
            }
            else
            {
                gl.BindFramebuffer(GlConsts.GL_FRAMEBUFFER, 0);
            }

            gl.Viewport(0, 0, renderWidth, renderHeight);
            gl.Clear(GlConsts.GL_COLOR_BUFFER_BIT | GlConsts.GL_DEPTH_BUFFER_BIT);

            if (_meshRenderer is { HasMesh: true } mesh)
            {
                mesh.Render(gl, _turntable!.Camera, renderWidth, renderHeight);
            }

            _pedestal?.Render(_turntable!.Camera, renderWidth, renderHeight,
                _centre, _radius, _baseY);

            _ssaa?.ResolveTo(0, _width, _height);
            gl.Viewport(0, 0, _width, _height);

            if (!_firstFrameLogged)
            {
                _firstFrameLogged = true;
                // 帧真值落盘（RGBA 原始字节），供脚本转 PNG 目视——截图会被窗口遮挡污染。
                // 必须在 Swap 之前读：交换后后缓冲的内容未定义（EGL 无 BUFFER_PRESERVED）。
                try
                {
                    var pixels = GlRaw.ReadPixels(gl, 0, 0, _width, _height);
                    if (pixels is not null)
                    {
                        File.WriteAllBytes(
                            Path.Combine(Path.GetTempPath(), "shellpreview_frame.raw"), pixels);
                        Exports.Log($"frame dumped {_width}x{_height} mesh={_meshRenderer?.HasMesh} " +
                                    $"glErr=0x{GlRaw.GetError(gl):X}");
                        var cam = _turntable!.Camera;
                        Exports.Log($"pedestal centre=({_centre.X:F1},{_centre.Y:F1},{_centre.Z:F1}) " +
                                    $"radius={_radius:F1} baseY={_baseY:F1} cam pos=({cam.Position.X:F1},{cam.Position.Y:F1},{cam.Position.Z:F1}) " +
                                    $"yaw={cam.Yaw:F1} pitch={cam.Pitch:F1} near={cam.Near:F2} far={cam.Far:F0}");
                    }
                }
                catch (Exception ex)
                {
                    Exports.Log($"frame dump failed {ex.Message}");
                }
            }

            _egl.Swap();
        }
        finally
        {
            _rendering = false;
        }
    }

    private void OnPaint()
    {
        var ps = default(PAINTSTRUCT);
        var hdc = BeginPaint(_hwnd, ref ps);
        if (!_ready || !_glReady)
        {
            // GL 帧还没得画：深灰底 + 状态文字。字母数字必须可读，字号用系统默认。
            var rect = default(RECT);
            GetClientRect(_hwnd, out rect);
            var brush = CreateSolidBrush(0x00202020); // COLORREF 是 0x00BBGGRR
            FillRect(hdc, ref rect, brush);
            DeleteObject(brush);
            var font = GetStockObject(12); // DEFAULT_GUI_FONT
            SelectObject(hdc, font);
            SetTextColor(hdc, 0x00C8C8C8);
            SetBkMode(hdc, 1); // TRANSPARENT
            TextOutW(hdc, 12, 12, _status, _status.Length);
        }
        else if (!_rendering)
        {
            // GL 内容由交换链保持，DWM 会自己合成；这里补一帧是防宿主硬性失效的。
            RenderFrame();
        }

        EndPaint(_hwnd, ref ps);
    }

    // ---------- 加载 ----------

    private void StartLoad()
    {
        var path = _path;
        if (path is null)
        {
            _status = "没有收到文件路径";
            return;
        }

        // prevhost 每次切换文件都是冷启动，全量建网格要几秒：
        // 窗口先画「加载中」，后台线程建完用消息交棒。
        _loadedPath = path;
        _ready = false;
        _status = "加载中…";
        var generation = ++_loadGeneration;

        // 旧场景先卸掉：切换文件时画面立刻回到「加载中」，不是上一台模型悬着。
        _meshRenderer?.Load(null, null, null, 0, 0);
        _needsRedraw = true;

        Task.Run(() =>
        {
            Exports.Log($"load task started gen={generation}");
            Scene? scene = null;
            string? error = null;
            try
            {
                scene = SceneLoader.Load(path);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                // 状态行只放 Message（窗格窄），但日志要留全栈——载入失败
                // 十有八九是某一层的越界/空引用，只有类型没有栈就得靠人肉二分。
                Exports.Log($"load task failed gen={generation}\n{ex}");
            }

            Exports.Log($"load task done gen={generation} scene={(scene is null ? "null" : "ok")} error={error ?? "-"}");
            if (_hwnd == 0)
            {
                return; // 窗格已被关掉，结果作废
            }

            Volatile.Write(ref _pendingResult, new LoadResult
            {
                Scene = scene,
                Error = error,
                Generation = generation,
            });
            PostMessageW(_hwnd, WmAppLoaded, 0, 0);
        });
    }

    private void OnLoaded()
    {
        Exports.Log("OnLoaded enter");
        var result = Interlocked.Exchange(ref _pendingResult, null);
        if (result is null)
        {
            return;
        }

        if (result.Generation != _loadGeneration)
        {
            // 迟到的结果：结果在路上时用户又换了文件，新加载自己会来交棒。
            Exports.Log($"OnLoaded stale gen={result.Generation} current={_loadGeneration} discarded");
            return;
        }

        if (result.Error is not null)
        {
            _status = $"载入失败：{result.Error}";
            _loadedPath = null; // 失败不占路径：再选一次同一文件给重试机会
        }
        else if (result.Scene is null || result.Scene.Vertices.Length == 0)
        {
            _status = "空场景";
            _loadedPath = null;
        }
        else
        {
            var scene = result.Scene;
            _centre = scene.Centre;
            _radius = scene.Radius;
            _baseY = scene.BaseY;
            _meshRenderer!.Load(scene.Vertices, scene.Indices,
                scene.AtlasLevels, scene.AtlasWidth, scene.AtlasHeight);
            _turntable = new Turntable(scene);
            _ready = true;
            _needsRedraw = true;
        }

        InvalidateRect(_hwnd, 0, 0);
    }

    // ---------- Win32 ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEXW
    {
        public uint CbSize;
        public uint Style;
        public nint WndProc;
        public int ClsExtra;
        public int WndExtra;
        public nint HInstance;
        public nint HIcon;
        public nint HCursor;
        public nint HbrBackground;
        public nint LpszMenuName;
        public nint LpszClassName;
        public nint HIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public nint Hdc;
        public int FErase;
        public RECT Rect;
        public int FRestore;
        public int FUpdate;
        public fixed byte Reserved[32];
    }

    [DllImport("user32.dll")]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(
        uint styleEx, string className, string? windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern int DestroyWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int ShowWindow(nint hwnd, int command);

    [DllImport("user32.dll")]
    private static extern nint SetFocus(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint SetCapture(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern nint SetTimer(nint hwnd, nint id, uint elapsed, nint proc);

    [DllImport("user32.dll")]
    private static extern int KillTimer(nint hwnd, nint id);

    [DllImport("user32.dll")]
    private static extern int InvalidateRect(nint hwnd, nint rect, int erase);

    [DllImport("user32.dll")]
    private static extern int MoveWindow(nint hwnd, int x, int y, int width, int height, int repaint);

    [DllImport("user32.dll")]
    private static extern int GetClientRect(nint hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern nint BeginPaint(nint hwnd, ref PAINTSTRUCT ps);

    [DllImport("user32.dll")]
    private static extern int EndPaint(nint hwnd, ref PAINTSTRUCT ps);

    [DllImport("user32.dll")]
    private static extern int PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern int FillRect(nint hdc, ref RECT rect, nint brush);

    // CharSet.Unicode 必须显式写：默认按 ANSI 封送，TextOutW 把 ANSI 字节当 UTF-16
    // 读出来就是一排假汉字（状态文字全变乱码的那个 bug）。
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern int TextOutW(nint hdc, int x, int y, string text, int length);

    [DllImport("gdi32.dll")]
    private static extern uint SetTextColor(nint hdc, uint color);

    [DllImport("gdi32.dll")]
    private static extern int SetBkMode(nint hdc, int mode);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint hdc, nint obj);

    [DllImport("gdi32.dll")]
    private static extern nint GetStockObject(int index);

    [DllImport("gdi32.dll")]
    private static extern nint CreateSolidBrush(uint color);

    [DllImport("gdi32.dll")]
    private static extern int DeleteObject(nint obj);

    [DllImport("kernel32.dll")]
    private static extern int GetModuleHandleExW(uint flags, nint address, out nint module);
}
