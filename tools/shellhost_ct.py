import ctypes, uuid, time, sys, ctypes.wintypes as wt

# 冒烟宿主（原生进程，与 prevhost 同构——宿主 .NET 进程会与 AOT runtime 冲突）。
# 用 ctypes 走 explorer 的完整调用序列：LoadLibrary → DllGetClassObject →
# CreateInstance → QI 两接口 → Initialize → SetWindow → DoPreview → Show →
# 消息泵 10 秒（渲染/加载在 dll 内部完成）→ Unload。

class GUID(ctypes.Structure):
    _fields_ = [('a', ctypes.c_uint32), ('b', ctypes.c_uint16), ('c', ctypes.c_uint16), ('d', ctypes.c_uint8 * 8)]

def guid(s):
    u = uuid.UUID(s)
    return GUID(u.time_low, u.time_mid, u.time_hi_version, (ctypes.c_uint8 * 8).from_buffer_copy(u.bytes[8:16]))

CLSID = guid('8E5B1A47-3F2E-4C6D-9A1B-7C2D5E8F0A31')
IID_CF = guid('00000001-0000-0000-C000-000000000046')
IID_IU = guid('00000000-0000-0000-C000-000000000046')
IID_INITFILE = guid('b7d14566-0509-4cce-a71f-0a554233bd9b')
IID_PREVIEW = guid('8895b1c6-b41f-4c1c-a562-0d564250836f')

class RECT(ctypes.Structure):
    _fields_ = [('l', ctypes.c_int32), ('t', ctypes.c_int32), ('r', ctypes.c_int32), ('b', ctypes.c_int32)]

user32 = ctypes.windll.user32
user32.CreateWindowExW.restype = wt.HWND
user32.CreateWindowExW.argtypes = [wt.DWORD, wt.LPCWSTR, wt.LPCWSTR, wt.DWORD,
                                   ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int,
                                   wt.HWND, wt.HMENU, wt.HINSTANCE, wt.LPVOID]
user32.GetMessageW.argtypes = [ctypes.POINTER(wt.MSG), wt.HWND, wt.UINT, wt.UINT]

def vtable(unknown, index, restype, *args):
    tbl = ctypes.cast(unknown, ctypes.POINTER(ctypes.c_void_p)).contents
    entries = ctypes.cast(tbl, ctypes.POINTER(ctypes.c_void_p * 16)).contents
    return ctypes.WINFUNCTYPE(restype, ctypes.c_void_p, *args)(entries[index])

def qi(unknown, iid):
    QI = vtable(unknown, 0, ctypes.c_int32, ctypes.POINTER(GUID), ctypes.POINTER(ctypes.c_void_p))
    out = ctypes.c_void_p()
    hr = QI(unknown, ctypes.byref(iid), ctypes.byref(out))
    return hr, out

dll = ctypes.CDLL(r'C:\Users\ssp47\WorkBuddy\LitematicaViewer\dist\ShellPreview\LitematicaViewer.ShellPreview.dll')
dll.DllGetClassObject.restype = ctypes.c_int32
dll.DllGetClassObject.argtypes = [ctypes.POINTER(GUID), ctypes.POINTER(GUID), ctypes.POINTER(ctypes.c_void_p)]

cf = ctypes.c_void_p()
hr = dll.DllGetClassObject(ctypes.byref(CLSID), ctypes.byref(IID_CF), ctypes.byref(cf))
print('DllGetClassObject hr=0x%08X' % (hr & 0xFFFFFFFF))
assert hr == 0, 'DGC failed'

CreateInstance = vtable(cf.value, 3, ctypes.c_int32, ctypes.c_void_p, ctypes.POINTER(GUID), ctypes.POINTER(ctypes.c_void_p))
unk = ctypes.c_void_p()
hr = CreateInstance(cf.value, None, ctypes.byref(IID_IU), ctypes.byref(unk))
print('CreateInstance hr=0x%08X' % (hr & 0xFFFFFFFF))
assert hr == 0, 'CreateInstance failed'

hr, init = qi(unk.value, IID_INITFILE)
print('QI IInitializeWithFile hr=0x%08X' % (hr & 0xFFFFFFFF))
assert hr == 0, 'no IInitializeWithFile'

hr, preview = qi(unk.value, IID_PREVIEW)
print('QI IPreviewHandler hr=0x%08X' % (hr & 0xFFFFFFFF))
assert hr == 0, 'no IPreviewHandler'

Initialize = vtable(init.value, 3, ctypes.c_int32, ctypes.c_wchar_p, wt.DWORD)  # QI AddRef Release 之后
args = [a for a in sys.argv[1:] if not a.startswith('--')]
shot = '--shot' in sys.argv
path = args[0] if args else r'C:\Users\ssp47\WorkBuddy\LitematicaViewer\mecha_iron.litematic'
hr = Initialize(init.value, path, 0)
print('Initialize hr=0x%08X' % (hr & 0xFFFFFFFF))

# 顶层宿主窗口充当预览窗格父窗口。
parent = user32.CreateWindowExW(0x00080000, 'STATIC', 'ShellHost', 0x10000000 | 0x00C00000,
                                100, 100, 480, 420, None, None, None, None)
SetWindow = vtable(preview.value, 3, ctypes.c_int32, wt.HWND, ctypes.POINTER(RECT))  # IUnknown 3 项之后
hr = SetWindow(preview.value, parent, None)
print('SetWindow hr=0x%08X' % (hr & 0xFFFFFFFF))

rect = RECT(0, 0, 460, 380)
SetRect = vtable(preview.value, 4, ctypes.c_int32, ctypes.POINTER(RECT))
hr = SetRect(preview.value, ctypes.byref(rect))
print('SetRect hr=0x%08X' % (hr & 0xFFFFFFFF))

DoPreview = vtable(preview.value, 12, ctypes.c_int32)
hr = DoPreview(preview.value)
print('DoPreview hr=0x%08X' % (hr & 0xFFFFFFFF))

Show = vtable(preview.value, 5, ctypes.c_int32)
hr = Show(preview.value)
print('Show hr=0x%08X' % (hr & 0xFFFFFFFF))

# 消息泵 10 秒；PeekMessage 轮询（GetMessage 等不到 WM_QUIT 会永久阻塞）。
deadline = time.time() + 10
resized = False
shot = '--shot' in sys.argv
msg = wt.MSG()
while time.time() < deadline:
    while user32.PeekMessageW(ctypes.byref(msg), None, 0, 0, 1):
        user32.TranslateMessage(ctypes.byref(msg))
        user32.DispatchMessageW(ctypes.byref(msg))
        if msg.message == 0x0010:
            user32.DestroyWindow(msg.hwnd)
    if not resized and time.time() > deadline - 5:
        resized = True
        small = RECT(0, 0, 240, 200)
        SetRect(preview.value, ctypes.byref(small))
    time.sleep(0.03)

# --shot：泵结束后按窗口屏幕坐标直接抓屏（PrintWindow 对 STATIC 宿主不合成子窗格）。
if shot:
    import PIL.Image
    HWND_TOPMOST = ctypes.c_void_p(-1)
    SWP_NOMOVE_NOSIZE = 0x0001 | 0x0002
    user32.SetWindowPos(parent, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE_NOSIZE)
    time.sleep(0.3)
    r = wt.RECT()
    user32.GetWindowRect(parent, ctypes.byref(r))
    w, h = r.right - r.left, r.bottom - r.top
    hdcScreen = user32.GetDC(None)
    gdi32 = ctypes.windll.gdi32
    hdcMem = gdi32.CreateCompatibleDC(hdcScreen)
    hbmp = gdi32.CreateCompatibleBitmap(hdcScreen, w, h)
    gdi32.SelectObject(hdcMem, hbmp)
    gdi32.BitBlt(hdcMem, 0, 0, w, h, hdcScreen, r.left, r.top, 0x00CC0020)
    class BMIH(ctypes.Structure):
        _fields_ = [('size', wt.DWORD), ('w', ctypes.c_long), ('h', ctypes.c_long),
                    ('planes', wt.WORD), ('bits', wt.WORD), ('comp', wt.DWORD),
                    ('szimg', wt.DWORD), ('xppm', ctypes.c_long), ('yppm', ctypes.c_long),
                    ('used', wt.DWORD), ('imp', wt.DWORD)]
    bi = BMIH(ctypes.sizeof(BMIH), w, -h, 1, 32, 0, w * h * 4)
    buf = ctypes.create_string_buffer(w * h * 4)
    gdi32.GetDIBits(hdcMem, hbmp, 0, h, buf, ctypes.byref(bi), 0)
    img = PIL.Image.frombuffer('RGBA', (w, h), buf.raw, 'raw', 'BGRA', 0, 1)
    out = r'C:\Users\ssp47\WorkBuddy\LitematicaViewer\dist\smoke_shot.png'
    img.convert('RGB').save(out)
    print('shot saved', out)
    gdi32.DeleteObject(hbmp)
    gdi32.DeleteDC(hdcMem)
    user32.ReleaseDC(None, hdcScreen)

Unload = vtable(preview.value, 13, ctypes.c_int32)
hr = Unload(preview.value)
user32.DestroyWindow(parent)
print('Unload hr=0x%08X' % (hr & 0xFFFFFFFF))
print('smoke done')
