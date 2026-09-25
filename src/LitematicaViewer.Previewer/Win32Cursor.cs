using System.Runtime.InteropServices;

namespace LitematicaViewer.Previewer;

// 把光标挪到一个点上，以及把光标读回来。这两件事 Avalonia 都没给口子：
// 它的公开面里只有「换一个光标形状」（包括 None），没有任何移动光标位置的 API——
// Win32 后端自己倒是用了 GetCursorPos / SetCursor，但那几个 P/Invoke 是 internal 的。
//
// 所以只能自己来。范围只到 Windows：别的系统上要另找一套（X11 的 XWarpPointer 之类），
// 而调用方需要的是「知道能不能做」而不是「静默地什么都不做」——所以有 IsSupported，
// 钉不住时会退化成「只捕获指针」那条路并在日志里说清楚。
internal static class Win32Cursor
{
    internal static bool IsSupported => OperatingSystem.IsWindows();

    // BOOL SetCursorPos(int X, int Y)：屏幕物理像素坐标，原点是主显示器的左上角。
    //
    // 目标点落在可见区域之外时系统会把它挪到最近的可见点上——也就是说**它可能不落在你要的地方**，
    // 而返回值仍然是非零。所以调用方必须用 TryRead 核对，不能只看这里的返回值。
    internal static bool MoveTo(int x, int y) => IsSupported && SetCursorPos(x, y) != 0;

    // BOOL GetCursorPos(LPPOINT)：拿回来的是同一个坐标系里的点。
    internal static bool TryRead(out (int X, int Y) position)
    {
        position = default;

        if (!IsSupported || GetCursorPos(out Win32Point point) == 0)
        {
            return false;
        }

        position = (point.X, point.Y);
        return true;
    }

    // POINT 是两个 int，没有别的成员。用 StructLayout 明确顺序：
    // 默认布局对「两个 int」恰好也对，但那是巧合，而这里赌错的代价是把 Y 读成垃圾。
    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetCursorPos(out Win32Point point);
}
