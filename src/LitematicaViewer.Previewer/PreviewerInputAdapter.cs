using System.Diagnostics;
using Avalonia.Input;

namespace LitematicaViewer.Previewer;

// 把 Avalonia 的输入事件翻成 Previewer 自己的事件。存在的理由是分层：
// Previewer 只说「滚轮动了多少」「某个键按下了」，不知道键位怎么绑、也不知道谁在意这些——
// 那是控制器（Phase E/F）和宿主的决定。宿主不想接输入，不构造这个适配器就行。
//
// 它不碰 GL，也不碰相机：R2 要求使用层函数不创建、不销毁 GPU 资源，这里连 GL 上下文都拿不到。
public sealed class PreviewerInputAdapter : IDisposable
{
    private readonly Previewer _previewer;
    private bool _dragging;
    private bool _disposed;

    // 宿主就是控件本身，不再单收一个 host 参数：多一个「事件从哪来」和「往哪发」可以不同的
    // 自由度，只会让人以为它们可以不同。
    public PreviewerInputAdapter(Previewer previewer)
    {
        _previewer = previewer;

        // 键盘事件只发给获得焦点的元素，而控件默认不可聚焦。漏掉这一步的症状是
        // 「滚轮有效、按键毫无反应」——看起来像事件没接上，实际是焦点不在。
        previewer.Focusable = true;

        previewer.PointerWheelChanged += OnPointerWheelChanged;
        previewer.PointerPressed += OnPointerPressed;
        previewer.PointerMoved += OnPointerMoved;
        previewer.PointerReleased += OnPointerReleased;
        previewer.KeyDown += OnKeyDown;
        previewer.KeyUp += OnKeyUp;

#if DEBUG
        // 原始输入探针挂这儿：「有人打算处理输入了」是它唯一有意义的挂载时机。
        // 它只记录控件收到了什么，转发仍然由上面三个订阅负责——两段分开才查得动。
        previewer.AttachInputProbe();
#endif

        Debug.WriteLine(
            $"[PREVIEWER][input.attach] focusable={previewer.Focusable} " +
            $"note=按键还需要控件拿到焦点，宿主在窗口打开后调一次 Focus()");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _previewer.PointerWheelChanged -= OnPointerWheelChanged;
        _previewer.PointerPressed -= OnPointerPressed;
        _previewer.PointerMoved -= OnPointerMoved;
        _previewer.PointerReleased -= OnPointerReleased;
        _previewer.KeyDown -= OnKeyDown;
        _previewer.KeyUp -= OnKeyUp;
    }

    // 只取 Y 分量：横向滚轮是另一件事。真要做横向平移，那是加一个事件，
    // 而不是往这个 delta 里塞两个含义——塞进去之后没人能从签名上看出它到底是哪个。
    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        Debug.WriteLine($"[PREVIEWER][input.wheel] delta=({e.Delta.X},{e.Delta.Y})");
        _previewer.RaiseScrolled((float)e.Delta.Y);
    }

    // 哪个键起拖在这一层定：它是平台绑定（左键 = 主键），而「拖拽」是 Previewer 的说法。
    // 判据用 IsLeftButtonPressed 而不是 PointerUpdateKind：后者在「按下的同时移动」时
    // 报的是移动，两边都得判，而按键状态一个字段就问清了。
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_dragging || !e.GetCurrentPoint(_previewer).Properties.IsLeftButtonPressed)
        {
            // 已经在拖了：第二个键按下不该把参照点挪走，否则那一下会算成一段凭空的位移。
            return;
        }

        _dragging = true;

        // 捕获指针，拖到控件外（甚至窗口外）时移动事件才继续送过来。
        // 不捕获的话指针一出边界画面就停住，手感像卡死——而那是「没了后续事件」，
        // 不是「控制器没处理」。
        e.Pointer.Capture(_previewer);

        _previewer.RaiseDragStarted(e.GetPosition(_previewer));
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        // 抬起事件丢了的话（捕获被抢走、窗口失活），按键状态是唯一还能问出「还在拖吗」的地方。
        // 不问这一句，拖拽会挂在那里，画面跟着光标乱转。
        if (!e.GetCurrentPoint(_previewer).Properties.IsLeftButtonPressed)
        {
            EndDrag();
            return;
        }

        _previewer.RaiseDragMoved(e.GetPosition(_previewer));
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragging && e.InitialPressMouseButton == MouseButton.Left)
        {
            EndDrag();
        }
    }

    private void EndDrag()
    {
        _dragging = false;
        _previewer.RaiseDragEnded();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e) => _previewer.RaiseKeyChanged(e.Key, isDown: true);

    private void OnKeyUp(object? sender, KeyEventArgs e) => _previewer.RaiseKeyChanged(e.Key, isDown: false);
}
