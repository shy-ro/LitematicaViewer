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
        previewer.KeyDown += OnKeyDown;
        previewer.KeyUp += OnKeyUp;

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

    private void OnKeyDown(object? sender, KeyEventArgs e) => _previewer.RaiseKeyChanged(e.Key, isDown: true);

    private void OnKeyUp(object? sender, KeyEventArgs e) => _previewer.RaiseKeyChanged(e.Key, isDown: false);
}
