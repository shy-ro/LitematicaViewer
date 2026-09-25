using System.Diagnostics;
using Avalonia.Controls;

namespace LitematicaViewer.Previewer.Sample;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // 客户端尺寸与 RenderScaling 是「视口算得对不对」的参照系：
        // 视口错了的时候，第一眼要拿来的对的就是这两个数，而不是去猜 DPI。
        Opened += static (sender, _) =>
        {
            Window window = (Window)sender!;
            Debug.WriteLine($"[SAMPLE][window.opened] client={window.ClientSize} scaling={window.RenderScaling}");
        };
        Closed += static (sender, _) =>
        {
            Window window = (Window)sender!;
            Debug.WriteLine($"[SAMPLE][window.closed] client={window.ClientSize} scaling={window.RenderScaling}");
        };
    }
}
