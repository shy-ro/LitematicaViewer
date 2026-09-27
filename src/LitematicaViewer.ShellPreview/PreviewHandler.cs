using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using LitematicaViewer.Meshing;

namespace LitematicaViewer.ShellPreview;
// 预览处理器本体：一个塞进预览窗格的子窗口 + 软件光栅化循环。
// 交互 = 按住左键拖动旋转，松手后带惯性衰减；静止时零 CPU。
// 渲染在 STA 线程同步做（timer 里驱动、一次一帧防堆积）；重活的
// 加载/建网格在后台线程，完成后用消息交棒。
[GeneratedComClass]
internal sealed unsafe partial class PreviewHandler : IPreviewHandler, IInitializeWithFile
{
    private const uint WmAppLoaded = 0x8000; // WM_APP
    private const uint WmTimer = 0x0113;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmMouseMove = 0x0200;
    private const uint WmSize = 0x0005;
    private const uint WmDestroy = 0x0002;
    private const uint WmPaint = 0x000F;
    private const uint WmEraseBkgnd = 0x0014;

    private const string ClassName = "LitematicaPreviewPane";
    private static bool _classRegistered;
    private static readonly Dictionary<nint, PreviewHandler> Handlers = new(); // 仅 STA 线程访问

    private string? _path;
    private nint _parentHwnd;
    private nint _hwnd;
    private RECT _rect;
    private int _width = 32, _height = 32;
    private byte[] _bgra = new byte[32 * 32 * 4];
    private BITMAPINFO _bmi;

    // 后台构建的场景经 Interlocked 交棒，STA 线程取走后只读。
    private Scene? _pendingScene;
    private string? _pendingError;
    private Scene? _scene;
    private bool _ready;
    private string _status = "加载中…";

    private bool _dirty = true;
    private bool _rendering;
    private bool _firstFrameLogged;
    private long _lastFrameTick;

    private float _yaw = MathF.PI * 0.25f;
    private float _pitch = 0.58f;
    private float _yawVel, _pitchVel;
    private bool _dragging;
    private int _lastX, _lastY;

    public int Initialize(string filePath, uint grfMode)
    {
        _path = filePath;
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
        if (_hwnd == 0)
        {
            CreateChildWindow();
            StartLoad();
            SetTimer(_hwnd, 1, 33, 0);
        }

        ShowWindow(_hwnd, 5); // SW_SHOW
        return 0;
    }

    public int Hide()
    {
        if (_hwnd != 0)
        {
            ShowWindow(_hwnd, 0); // SW_HIDE
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
        // 实际加载推迟到 Show（那里才确定窗口活着），这里只标记启动过。
        return 0;
    }

    public int Unload()
    {
        DestroyWindow(_hwnd);
        _hwnd = 0;
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
        _bgra = new byte[_width * _height * 4];
        _hwnd = CreateWindowExW(
            0, ClassName, null,
            0x40000000 | 0x10000000, // WS_CHILD | WS_VISIBLE
            _rect.Left, _rect.Top, w, h,
            _parentHwnd, 0, hInstance, 0);
        Handlers[_hwnd] = this;
        _bmi = new BITMAPINFO();
        _bmi.Header.Size = (uint)sizeof(BITMAPINFOHEADER);
        _bmi.Header.Planes = 1;
        _bmi.Header.BitCount = 32;
        _bmi.Header.Compression = 0; // BI_RGB
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
                _bgra = new byte[_width * _height * 4];
                _dirty = true;
                // timer 可能已因静止被杀：不复活的话，缩放后的窗格永远停在黑 buffer。
                SetTimer(_hwnd, 1, 33, 0);
                Exports.Log($"size {_width}x{_height}");
                return 0;

            case WmEraseBkgnd:
                return 1; // 自己填帧，别让 GDI 白刷一遍闪屏

            case WmPaint:
                OnPaint();
                Exports.Log($"paint blit {_width}x{_height} ready={_ready}");
                return 0;

            case WmLButtonDown:
                _dragging = true;
                _yawVel = 0;
                _pitchVel = 0;
                _lastX = (int)(short)lParam;
                _lastY = (int)(long)lParam >> 16;
                SetCapture(_hwnd);
                return 0;

            case WmMouseMove when _dragging:
            {
                var x = (int)(short)lParam;
                var y = (int)(long)lParam >> 16;
                var dx = x - _lastX;
                var dy = y - _lastY;
                _lastX = x;
                _lastY = y;
                ApplyLookDelta(dx, dy);
                // 惯性速度取移动的指数滑动平均：单帧突变不炸，松手能顺滑接上。
                _yawVel = 0.7f * _yawVel + 0.3f * dx * 0.01f;
                _pitchVel = 0.7f * _pitchVel + 0.3f * dy * 0.01f;
                return 0;
            }

            case WmLButtonUp:
                _dragging = false;
                ReleaseCapture();
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

    private void ApplyLookDelta(int dx, int dy)
    {
        _yaw += dx * 0.01f;
        _pitch = Math.Clamp(_pitch + dy * 0.01f, -1.55f, 1.55f);
        _dirty = true;
    }

    private void Tick()
    {
        // 惯性：松手后按速度滑行，衰减到阈值以下就停——静止时 timer 空转
        // 也要停掉，预览窗格不能白吃 CPU。
        if (!_dragging)
        {
            _yaw += _yawVel;
            _pitch = Math.Clamp(_pitch + _pitchVel, -1.55f, 1.55f);
            _yawVel *= 0.9f;
            _pitchVel *= 0.9f;
            if (MathF.Abs(_yawVel) > 1e-4f || MathF.Abs(_pitchVel) > 1e-4f)
            {
                _dirty = true;
            }
        }

        if (_dirty && !_rendering && _ready)
        {
            var scene = _scene;
            if (scene is not null && scene.Meshes.Count > 0)
            {
                _rendering = true;
                try
                {
                    Raster.Render(scene, _bgra, _width, _height, _yaw, _pitch);
                }
                finally
                {
                    _rendering = false;
                }

                _lastFrameTick = Environment.TickCount64;
                // 取证：首帧统计非零像素占比与采样色，确认不是黑帧/空帧。
                if (!_firstFrameLogged)
                {
                    _firstFrameLogged = true;
                    int nonzero = 0;
                    for (var i = 3; i < _bgra.Length; i += 4)
                        if (_bgra[i] != 0) nonzero++;
                    Exports.Log($"first frame nonzero={nonzero}/{_bgra.Length / 4} " +
                                $"c0={_bgra[0]},{_bgra[1]},{_bgra[2]} c1={_bgra[16]},{_bgra[17]},{_bgra[18]}");
                    // 帧真值落盘（BGRA 原始字节），供脚本转 PNG 目视——截图会被窗口遮挡污染。
                    try
                    {
                        File.WriteAllBytes(
                            Path.Combine(Path.GetTempPath(), "shellpreview_frame.raw"), _bgra);
                        Exports.Log($"frame dumped {_width}x{_height}");
                    }
                    catch (Exception ex)
                    {
                        Exports.Log($"frame dump failed {ex.Message}");
                    }
                }
            }

            _dirty = false;
            InvalidateRect(_hwnd, 0, 0);
        }

        if (!_dirty && !_dragging && !_rendering &&
            MathF.Abs(_yawVel) <= 1e-4f && MathF.Abs(_pitchVel) <= 1e-4f)
        {
            KillTimer(_hwnd, 1);
        }
    }

    private void OnPaint()
    {
        var ps = default(PAINTSTRUCT);
        var hdc = BeginPaint(_hwnd, ref ps);
        if (_ready && _bgra.Length > 0)
        {
            _bmi.Header.Width = _width;
            _bmi.Header.Height = -_height; // top-down
            SetDIBitsToDevice(
                hdc, 0, 0, (uint)_width, (uint)_height, 0, 0, 0, (uint)_height,
                _bgra, ref _bmi, 0);
        }
        else
        {
            var rect = default(RECT);
            GetClientRect(_hwnd, out rect);
            var brush = CreateSolidBrush(0x00202020); // 深灰背景（COLORREF 是 0x00BBGGRR）
            FillRect(hdc, ref rect, brush);
            DeleteObject(brush);
            var font = GetStockObject(12); // DEFAULT_GUI_FONT
            SelectObject(hdc, font);
            SetTextColor(hdc, 0x00C8C8C8);
            SetBkMode(hdc, 1); // TRANSPARENT
            TextOutW(hdc, 12, 12, _status, _status.Length);
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
        Task.Run(() =>
        {
            Exports.Log("load task started");
            Scene? scene = null;
            string? error = null;
            try
            {
                scene = SceneLoader.Load(path);
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            Exports.Log($"load task done scene={(scene is null ? "null" : scene.Meshes.Count.ToString())} error={error ?? "-"}");
            if (_hwnd == 0)
            {
                return; // 窗格已被关掉，结果作废
            }

            Interlocked.Exchange(ref _pendingScene, scene);
            Volatile.Write(ref _pendingError, error);
            PostMessageW(_hwnd, WmAppLoaded, 0, 0);
        });
    }

    private void OnLoaded()
    {
        Exports.Log("OnLoaded enter");
        _scene = Interlocked.Exchange(ref _pendingScene, null);
        var error = Volatile.Read(ref _pendingError);
        if (error is not null)
        {
            _status = $"载入失败：{error}";
        }
        else if (_scene is null || _scene.Meshes.Count == 0)
        {
            _status = "空场景";
        }
        else
        {
            _ready = true;
            _dirty = true;
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

    [DllImport("gdi32.dll")]
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

    [DllImport("gdi32.dll")]
    private static extern int SetDIBitsToDevice(
        nint hdc, int xDest, int yDest, uint width, uint height,
        int xSrc, int ySrc, uint startScan, uint scanLines,
        byte[] bits, ref BITMAPINFO bmi, uint colorUse);

    [DllImport("kernel32.dll")]
    private static extern int GetModuleHandleExW(uint flags, nint address, out nint module);
}
