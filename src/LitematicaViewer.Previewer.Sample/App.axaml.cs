using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace LitematicaViewer.Previewer.Sample;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            MainWindow window = new();
            desktop.MainWindow = window;

            if (Program.SelfTestSeconds > 0)
            {
                // 关窗口而不是直接 Shutdown：走正常关闭路径才会触发 GL 的 Deinit，
                // 而那正是帧数断言所在的地方。直接 Shutdown 会把它整个跳过去。
                DispatcherTimer.RunOnce(
                    () =>
                    {
                        Debug.WriteLine("[SAMPLE][app.selftest] 到时关闭，等 gl.deinit 把帧数打出来");
                        window.Close();
                    },
                    TimeSpan.FromSeconds(Program.SelfTestSeconds));
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
