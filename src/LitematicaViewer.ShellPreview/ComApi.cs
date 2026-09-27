using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace LitematicaViewer.ShellPreview;

// 预览处理器要实现的两个 shell 契约 + 激活入口 IClassFactory。
// 签名全部按 HRESULT 形态声明（int 返回值在 GeneratedComInterface 里就是 HRESULT）；
// GUID 抄自 Windows SDK 的 shobjidl_core.idl，一个字符都不能改。

[StructLayout(LayoutKind.Sequential)]
public struct RECT
{
    public int Left, Top, Right, Bottom;
}

[StructLayout(LayoutKind.Sequential)]
public struct MSG
{
    public nint Hwnd;
    public uint Message;
    public nint WParam;
    public nint LParam;
    public uint Time;
    public int PtX, PtY;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BITMAPINFOHEADER
{
    public uint Size;
    public int Width;
    public int Height; // 负值 = top-down，第一行是图像顶行
    public ushort Planes;
    public ushort BitCount;
    public uint Compression; // 0 = BI_RGB
    public uint SizeImage;
    public int XPelsPerMeter;
    public int YPelsPerMeter;
    public uint ClrUsed;
    public uint ClrImportant;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BITMAPINFO
{
    public BITMAPINFOHEADER Header;
    public uint FirstColor; // BI_RGB 32bpp 不需要调色板，占一个位对齐即可
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("8895b1c6-b41f-4c1c-a562-0d564250836f")]
public unsafe partial interface IPreviewHandler
{
    // 顺序即 vtable 顺序。SetWindow 给的是预览窗格里属于我们的一块真实 HWND 的父窗口。
    [PreserveSig]
    int SetWindow(nint hwnd, RECT* rect);

    [PreserveSig]
    int SetRect(RECT* rect);

    [PreserveSig]
    int Show();

    [PreserveSig]
    int Hide();

    // 焦点四件套不实现好，Tab 会在预览窗格里卡死。
    [PreserveSig]
    int HandleFocus();

    [PreserveSig]
    int QueryFocus(nint* phwnd);

    // S_FALSE = 不处理快捷键，交还宿主。
    [PreserveSig]
    int TranslateAccelerator(MSG* msg);

    [PreserveSig]
    int SetFocus();

    [PreserveSig]
    int GetPalette(nint* hPalette);

    [PreserveSig]
    int DoPreview();

    [PreserveSig]
    int Unload();
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("b7d14566-0509-4cce-a71f-0a554233bd9b")]
public partial interface IInitializeWithFile
{
    [PreserveSig]
    int Initialize(string filePath, uint grfMode);
}

[GeneratedComInterface]
[Guid("00000001-0000-0000-C000-000000000046")]
public unsafe partial interface IClassFactory
{
    [PreserveSig]
    int CreateInstance(nint outer, Guid* riid, nint* ppvObject);

    [PreserveSig]
    int LockServer(int fLock);
}
