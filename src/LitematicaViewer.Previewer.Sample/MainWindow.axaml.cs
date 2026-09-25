using System.Diagnostics;
using Avalonia.Controls;

namespace LitematicaViewer.Previewer.Sample;

public partial class MainWindow : Window
{
    private readonly PreviewerInputAdapter _input;
    private readonly InputSelfTest? _selfTest;

    public MainWindow()
    {
        InitializeComponent();

        // 输入适配器接上，但这里不订阅 Scrolled / KeyChanged：没人订阅时事件照常触发，
        // 只是什么都不发生。谁在意它们是控制器的事（Phase E/F 的 CameraModel 与两个控制器），
        // Sample 只负责把输入接进控件。
        _input = new PreviewerInputAdapter(Viewport);

        _selfTest = Program.SelfTestSeconds > 0 ? new InputSelfTest(this, Viewport) : null;
        _selfTest?.Attach();

        // 客户端尺寸与 RenderScaling 是「视口算得对不对」的参照系：
        // 视口错了的时候，第一眼要拿来的对的就是这两个数，而不是去猜 DPI。
        Opened += OnOpened;
        Closed += OnClosed;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        Debug.WriteLine($"[SAMPLE][window.opened] client={ClientSize} scaling={RenderScaling}");

        // 键盘事件只发给有焦点的元素。窗口一开就把焦点给视口，否则「打开就跑」的人
        // 先按几下键发现没反应，再去猜是代码的问题——而焦点不在时的表现和事件没接上一模一样。
        bool focused = Viewport.Focus();
        Debug.WriteLine($"[SAMPLE][window.opened] viewportFocus={focused} expected=True");
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Debug.WriteLine($"[SAMPLE][window.closed] client={ClientSize} scaling={RenderScaling}");

        // 先出验收结论再拆输入：拆了之后事件就不触发了，
        // 而验收要的正是「到这一刻为止，每个事件都至少走通过一次」。
        _selfTest?.Report();
        _input.Dispose();
    }
}
