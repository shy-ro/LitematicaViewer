using System.Diagnostics;
using System.Numerics;
using Avalonia.Controls;

namespace LitematicaViewer.Previewer.Sample;

public partial class MainWindow : Window
{
    private readonly PreviewerInputAdapter _input;
    private readonly CameraModel _camera;
    private readonly ScrollZoomController _scroll;
    private readonly InputSelfTest? _selfTest;

    public MainWindow()
    {
        InitializeComponent();

        // 输入适配器接上，但窗口自己不订阅 Scrolled / KeyChanged：没人订阅时事件照常触发，
        // 只是什么都不发生。谁在意它们是控制器的事，窗口只负责把零件装到一起。
        _input = new PreviewerInputAdapter(Viewport);

        // 相机的权威从这一行开始就在模型手里。锚点取原点，因为立方体在原点——
        // 而 Phase C 起「轮廓质心落在画面中心」那条校验，正是靠「相机看向原点」才成立的。
        _camera = new CameraModel(CameraState.Default, Vector3.Zero);

#if DEBUG
        CameraModel.VerifyZoom();
#endif

        // 先把模型的相机推给控件一次。这一步不是多余的：Previewer 内部那份默认值只是占位，
        // 不推的话开窗第一帧用它、第一次滚轮用模型那份，中间会跳一下——
        // 而那是两处各自都「对」的默认值，从任何一处都看不出问题。
        Viewport.SetCamera(_camera.Camera);

        _scroll = new ScrollZoomController(Viewport, _camera);

        _selfTest = Program.SelfTestSeconds > 0 ? new InputSelfTest(this, Viewport, _camera) : null;
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

        // 先出验收结论再拆零件：拆完事件就不触发了，
        // 而验收要的正是「到这一刻为止，每个事件都至少走通过一次」。
        _selfTest?.Report();
        _scroll.Dispose();
        _input.Dispose();
    }
}
