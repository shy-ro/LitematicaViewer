using System.Diagnostics;
using System.Globalization;
using Avalonia;

namespace LitematicaViewer.Previewer.Sample;

internal static class Program
{
    // 不传参数就正常开窗口；`--selftest <秒>` 打开后自动关掉。
    // 存在的理由：上下文建没建起来、画了几帧，是没法靠「盯着屏幕」验的，
    // 而探针走 Debug.WriteLine，它只在这类监听器挂上时才输出。
    private const string SelfTestSwitch = "--selftest";

    internal static double SelfTestSeconds { get; private set; }

    // 命令行带上 .litematic 路径时开窗即载入（验收用的通道，与拖拽走同一条路）。
    // 自检模式下宿主会拒绝载入，所以这里照收不误、由调用方把关。
    internal static string? InitialLitematic { get; private set; }

    // --shot <路径>：载入 InitialLitematic 渲染几帧后读回像素存盘退出。
    // 存在的理由：用户报「画面不对」时口述 + 截图猜不出根因，
    // 同一个文件、同一条渲染路径自己跑一遍，产物就是证据。
    internal static string? ShotPath { get; private set; }

    // --shot-dist <倍数>：截帧取景距离 = 默认取景 × 倍数。复现「凑近看」的画面用。
    internal static float ShotDistanceFactor { get; private set; } = 1f;

    [STAThread]
    public static int Main(string[] args)
    {
        // Debug 与 Trace 共用同一个监听器集合，挂这一个就够。
        Trace.Listeners.Add(new TextWriterTraceListener(Console.Out));
        Trace.AutoFlush = true;

        SelfTestSeconds = ParseSelfTestSeconds(args);
        InitialLitematic = args.FirstOrDefault(a =>
            a.EndsWith(".litematic", StringComparison.OrdinalIgnoreCase) && File.Exists(a));
        ShotPath = ParseShotPath(args);
        ParseShotDistance(args);

        Debug.WriteLine(
            $"[SAMPLE][app.start] args=[{string.Join(' ', args)}] baseDir='{AppContext.BaseDirectory}'");
        Debug.WriteLine(
            $"[SAMPLE][app.start] selftest={SelfTestSeconds}s initial={InitialLitematic ?? "无"} " +
            "expected=0 表示窗口一直开着");

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // 显式挑 ANGLE/EGL，而不是让平台自己挑：Win32 下能提供 GL 上下文的后端不止一个，
            // OpenGlControlBase 需要的是能拿来直接跑 GL 的那一个。
            .With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.AngleEgl] })
            .LogToTrace();
    }

    private static double ParseSelfTestSeconds(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == SelfTestSwitch &&
                double.TryParse(args[i + 1], CultureInfo.InvariantCulture, out var seconds))
                return seconds;

        return 0;
    }

    private static string? ParseShotPath(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == "--shot")
                return args[i + 1];

        return null;
    }

    private static void ParseShotDistance(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == "--shot-dist" &&
                float.TryParse(args[i + 1], CultureInfo.InvariantCulture, out var factor))
                ShotDistanceFactor = factor;
    }
}
