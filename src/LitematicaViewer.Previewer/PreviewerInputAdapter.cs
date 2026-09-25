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
    private IPointer? _pointer;
    private bool _looking;
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
        previewer.PointerMoved += OnPointerMoved;
        previewer.PointerExited += OnPointerExited;
        previewer.PointerCaptureLost += OnPointerCaptureLost;
        previewer.KeyDown += OnKeyDown;
        previewer.KeyUp += OnKeyUp;

        // 不再订阅 PointerPressed / PointerReleased：转视角不需要按键，指针在控件上移动就转。
        // 早先那条「左键按下才算起拖」的绑定整个去掉了，于是 PointerPressed 在这里没有任何职责。

#if DEBUG
        // 原始输入探针挂这儿：「有人打算处理输入了」是它唯一有意义的挂载时机。
        // 它只记录控件收到了什么，转发仍然由上面几个订阅负责——两段分开才查得动。
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
        _previewer.PointerMoved -= OnPointerMoved;
        _previewer.PointerExited -= OnPointerExited;
        _previewer.PointerCaptureLost -= OnPointerCaptureLost;
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

    // 指针一动就转视角。参照点（手势的起点）在第一次移动时才定下来：只有一次位置没有「差」，
    // 那一次只用来把「上一次位置」写成当前这个点。
    //
    // 顺手把指针捕获过来：捕获之后指针移出控件、甚至移出窗口时移动事件仍然送得到，
    // 转动不会在边界上突然停住。不捕获的话指针一出界画面就不动了，手感像卡死——
    // 而那是「没了后续事件」，不是「控制器没处理」，从日志上两者分不开。
    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_looking)
        {
            _looking = true;
            _pointer = e.Pointer;
            e.Pointer.Capture(_previewer);
            _previewer.RaiseLookStarted(e.GetPosition(_previewer));
            return;
        }

        _previewer.RaiseLookMoved(e.GetPosition(_previewer));
    }

    // 指针离开控件就结束这一段。没有抬起事件可以用来收尾了，所以「结束」只能由位置本身给出。
    private void OnPointerExited(object? sender, PointerEventArgs e) => EndLook();

    // 捕获被抢走（点到别的窗口、被别的元素抢了捕获）时手势到此为止。
    // 少了这一条，参照点会一直停在旧位置，下次移进来的第一帧就是一段凭空的跳转。
    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndLook();

    // 宿主在窗口失活时调它。Alt+Tab 之后指针的进出与捕获丢失都送到别的窗口去了，
    // 而挂着的捕获与参照点会让光标回来时的第一帧跳一下。
    //
    // 它是 public：宿主在另一个程序集里（Sample），而这件事只有宿主知道该在什么时候做。
    public void ReleaseLook() => EndLook();

    private void EndLook()
    {
        if (!_looking)
        {
            return;
        }

        // 先把状态清掉再放捕获：Capture(null) 会同步回调 OnPointerCaptureLost，
        // 那时 _looking 已经是 false，不会绕回来再结束一次。
        _looking = false;
        _pointer?.Capture(null);
        _pointer = null;
        _previewer.RaiseLookEnded();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e) => _previewer.RaiseKeyChanged(e.Key, isDown: true);

    private void OnKeyUp(object? sender, KeyEventArgs e) => _previewer.RaiseKeyChanged(e.Key, isDown: false);
}
