using System.Globalization;
using System.Diagnostics;
using Avalonia;
using Avalonia.Win32;

namespace LitematicaViewer.SamplePreviewer;

// 预览器宿主的入口。与 Sample 相比只剩三个参数：文件路径、--shot、--shot-dist。
// 没有 --selftest：没有自由视角与设置面板，自检剧本没有可验的对象。
internal static class Program
{
    internal static string? InitialLitematic { get; private set; }

    internal static string? ShotPath { get; private set; }

    internal static float ShotDistanceFactor { get; private set; } = 1f;

    [STAThread]
    public static int Main(string[] args)
    {
        // Debug 与 Trace 共用同一个监听器集合，挂这一个就够。
        Trace.Listeners.Add(new TextWriterTraceListener(Console.Out));
        Trace.AutoFlush = true;

        InitialLitematic = args.FirstOrDefault(a =>
            a.EndsWith(".litematic", StringComparison.OrdinalIgnoreCase) && File.Exists(a));
        ShotPath = ParseShotPath(args);
        ParseShotDistance(args);

        Debug.WriteLine(
            $"[PREVIEW][app.start] args=[{string.Join(' ', args)}] initial={InitialLitematic ?? "无"}");

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // 与 Sample 同一条理由：OpenGlControlBase 需要能直接跑 GL 的后端。
            .With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.AngleEgl] })
            .LogToTrace();
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
