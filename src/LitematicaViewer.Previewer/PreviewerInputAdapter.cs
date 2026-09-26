using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace LitematicaViewer.Previewer;

// 把 Avalonia 的输入事件翻成 Previewer 自己的事件。存在的理由是分层：
// Previewer 只说「滚轮动了多少」「某个键按下了」，不知道键位怎么绑、也不知道谁在意这些——
// 那是控制器（Phase E/F）和宿主的决定。宿主不想接输入，不构造这个适配器就行。
//
// 它不碰 GL，也不碰相机：R2 要求使用层函数不创建、不销毁 GPU 资源，这里连 GL 上下文都拿不到。
//
// 转视角这一条有两种手势（见 LookGesture）：自由视角下指针一动就转，同时把光标钉在视口中心
// （游戏里的做法），于是手可以一直朝一个方向划、视角一直转，撞不到屏幕边——代价是光标被夺走了，
// 所以按住右键把它们还回去（见 OnPointerPressed）。展台下按住左键拖才转，不钉不藏，
// 因为拖动是有头有尾的手势，而且拖的时候得看得见光标。
public sealed class PreviewerInputAdapter : IDisposable
{
    private readonly Previewer _previewer;

    // 能不能钉光标。宿主可以关掉（验收剧本就要关），平台不支持时也自动是关的。
    private bool _confine = true;

    private Cursor? _cursorBeforeHide;
    private bool _cursorHidden;

    private bool _disposed;

    // 转视角的手势。见 LookGesture：自由视角与展台只差这一件事。
    private LookGesture _gesture = LookGesture.FollowPointer;

    // 手势是否进行中。它同时是「光标现在被钉着、藏起来了」的同一个状态，所以两个必须一起变。
    private bool _looking;

    // 钉不住之后的降级标志。窗口有一部分在屏幕外时 SetCursorPos 会落到别处，
    // 而那时每次都按请求点算增量会让画面自己转起来——所以一旦发现钉不住就整段放弃，
    // 退回「只捕获指针」。它不再复位：一次钉不住之后，这个窗口的位置没变，下一次也不会成功。
    private bool _pinBroken;
    private bool _pinBrokenLogged;
    private bool _pinLogged;
    private IPointer? _pointer;

    // 参照点：上一次报出去的指针位置。手势的增量全是「当前位置减它」。
    //
    // 它必须和光标的实际位置严格一致，而这个位置现在有两种来源：钉住时是「钉完之后读回来的那个点」，
    // 没钉住时就是事件给的位置。所以这个字段只能由管钉住的那一层维护——
    // 换句话说，差值就该在这里算：控制器不知道光标被挪走过，它算出来的会是「挪动 + 手移动」的混合。
    private Point _reference;

    // 右键按着的时候不接管指针：不转视角、不钉光标、不藏光标。
    private bool _suspended;

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
        previewer.PointerPressed += OnPointerPressed;
        previewer.PointerReleased += OnPointerReleased;
        previewer.KeyDown += OnKeyDown;
        previewer.KeyUp += OnKeyUp;

        // PointerPressed / PointerReleased 只为右键而订阅：左键在这个控件上没有任何职责
        // （转视角不需要按键）。中键、侧键也不管——它们现在没有含义，将来真要用再加。

        // 拾取事件与探针同一个挂载时机（「有人打算处理输入了」），但它在 DEBUG 区外：
        // Release 下探针整个不存在，拾取事件却必须照常发。
        previewer.AttachHoverEvents();

#if DEBUG
        // 原始输入探针挂这儿：「有人打算处理输入了」是它唯一有意义的挂载时机。
        // 它只记录控件收到了什么，转发仍然由上面几个订阅负责——两段分开才查得动。
        previewer.AttachInputProbe();
#endif

        Debug.WriteLine(
            $"[PREVIEWER][input.attach] focusable={previewer.Focusable} " +
            $"confine={_confine} platform={Win32Cursor.IsSupported} " +
            $"note=按键还需要控件拿到焦点，宿主在窗口打开后调一次 Focus()");
    }

    // 能不能把光标钉在视口中心。默认钉住（游戏那样的固定鼠标）。
    //
    // 关掉之后退化成「捕获指针 + 按事件位置算增量」：视角照转，但手划到屏幕边上就停住了。
    // 验收剧本必须关掉它——剧本摆的是合成的指针位置，钉住会让真实光标的位置混进参照点里，
    // 那时脚本算出来的角度就不再是它自己合成的那一段了。
    public bool ConfinePointer
    {
        get => _confine;

        set
        {
            if (_confine == value) return;

            _confine = value;
            Debug.WriteLine($"[PREVIEWER][input.confine] confine={value}");

            if (!value)
                // 关掉的时候光标可能正被藏着。留着它不还，用户会以为鼠标坏了。
                ShowCursor();
        }
    }

    // 转视角的手势。宿主切模式时设它。
    public LookGesture Gesture
    {
        get => _gesture;

        set
        {
            if (_gesture == value) return;

            _gesture = value;

            // 切模式时把进行中的手势收掉：两种手势的起止条件不一样（一个靠指针进出，
            // 一个靠按键），留着会让新模式的第一次移动带着旧模式的参照点走一段凭空的位移。
            ReleaseLook();
            Debug.WriteLine($"[PREVIEWER][input.gesture] gesture={value}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _previewer.PointerWheelChanged -= OnPointerWheelChanged;
        _previewer.PointerMoved -= OnPointerMoved;
        _previewer.PointerExited -= OnPointerExited;
        _previewer.PointerCaptureLost -= OnPointerCaptureLost;
        _previewer.PointerPressed -= OnPointerPressed;
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

    // 指针一动就转视角，并且把光标拽回中心，所以手可以一直朝一个方向划。
    //
    // 起手势那一次只定参照、不产生增量：只有一次位置就没有「差」可言，
    // 硬算出来的会是「从上次留下的位置到这里」那一段凭空的跳转。
    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_gesture == LookGesture.DragPrimaryButton)
        {
            OnDragMoved(e);
            return;
        }

        // 右键按着的那一段完全不接管：转视角、钉光标、藏光标全停，指针归用户。
        if (_suspended)
        {
            // 「抬起」不能只等那个事件：它可能落在别处——右键按住把光标拖到侧边栏上再松开、
            // 或者拖出窗口松开——那时事件不会回到这个控件上，标志会一直挂着。
            // 症状是回来之后鼠标怎么动都不转，而日志里只有一条「按下」，看起来像灵敏度成了 0。
            //
            // 所以这里按移动事件报的**实际按键状态**判断，而不是等一个可能永远不来的事件。
            // 侧边栏出现之前这条路几乎走不到（窗口里只有这一个控件），它现在是最常走的那条。
            if (e.GetCurrentPoint(_previewer).Properties.IsRightButtonPressed) return;

            _suspended = false;
            Debug.WriteLine(
                "[PREVIEWER][input.right] 抬起（从指针状态补上）suspended=False " +
                "note=抬起事件落在别处了，这一次移动重新起手势并钉住光标");
        }

        var position = e.GetPosition(_previewer);

        if (!_looking)
        {
            _looking = true;
            _pointer = e.Pointer;

            // 捕获之后指针移出控件、甚至移出窗口时移动事件仍然送得到。
            // 不捕获的话指针一出界画面就不动了，手感像卡死——而那是「没了后续事件」，
            // 不是「控制器没处理」，从日志上两者分不开。
            e.Pointer.Capture(_previewer);

            _reference = CanPin() ? StartPin() : position;
            _previewer.RaiseLookStarted();
            return;
        }

        Vector delta = position - _reference;

        // 零增量既不是异常也不是错误：钉住光标时，我们自己那一次回中会紧接着产生一个
        // 「指针回到了参照点」的事件，它的增量本来就该是零。放它过去会在控制器里留下
        // 一条没意义的移动记录，而那条记录会让「转了多少次」这类计数对不上。
        if (delta == default) return;

        _reference = CanPin() ? StartPin() : position;
        _previewer.RaiseLookMoved(delta);
    }

    // 指针离开控件就结束这一段。钉住光标时它几乎是不会发生的（指针被钉在控件中心），
    // 所以它管的是「钉不住」那条降级路径。
    //
    // 展台模式下它一次都不能结束手势：拖动是可以拖到控件外面去的（捕获还在，事件照来），
    // 拖出去就断的表现是「甩到一半没了」，而甩本来就是要求之一。
    private void OnPointerExited(object? sender, PointerEventArgs e)
    {
        if (_gesture == LookGesture.FollowPointer) EndLook();
    }

    // 展台：按住左键拖着转。不钉光标、不藏光标——拖动本来就要看得见光标。
    //
    // 起手那一次只定参照、不产生增量（和自由视角同一件道理）：只有一次位置就没有「差」。
    private void BeginDrag(PointerPressedEventArgs e)
    {
        // 只认左键。右键在展台里没有含义（自由视角下它是「把指针还回去」，而这里指针本来就是自由的）。
        if (_looking || !e.GetCurrentPoint(_previewer).Properties.IsLeftButtonPressed) return;

        _looking = true;
        _pointer = e.Pointer;
        _reference = e.GetPosition(_previewer);

        // 捕获：拖出控件、甚至拖出窗口时移动事件仍然送得到。不捕获的话手一出界画面就不动了，
        // 而那看起来像卡死——实际是「没有后续事件」，不是「控制器没处理」。
        e.Pointer.Capture(_previewer);

        Debug.WriteLine($"[PREVIEWER][input.drag] 按下 position=({_reference.X:F0},{_reference.Y:F0})");
        _previewer.RaiseLookStarted();
    }

    // 展台：拖动中的每一次移动。
    private void OnDragMoved(PointerEventArgs e)
    {
        if (!_looking)
            // 没按着左键时的移动只是路过。这里的安静是对的——而「一动就转」那套必须整个不生效，
            // 否则展台里光标一进画面视角就开始转。
            return;

        // 抬起事件可能落在别处（拖出控件再松手），所以这里按指针状态兜一次。
        // 与右键挂起那一段同一个理由：不信一个可能永远不来的事件。
        if (!e.GetCurrentPoint(_previewer).Properties.IsLeftButtonPressed)
        {
            Debug.WriteLine("[PREVIEWER][input.drag] 抬起落在别处，从指针状态补上 looking=False");
            EndLook();
            return;
        }

        var position = e.GetPosition(_previewer);
        Vector delta = position - _reference;
        _reference = position;

        // 零增量放过：拖动中手停一下就会产生零增量，喂给控制器只会留下一条没意义的记录。
        if (delta != default) _previewer.RaiseLookMoved(delta);
    }

    // 捕获被抢走（点到别的窗口、被别的元素抢了捕获）时手势到此为止。
    // 少了这一条，参照点会一直停在旧位置，下次移进来的第一帧就是一段凭空的跳转。
    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        EndLook();
    }

    // 右键按下：把指针还回去。
    //
    // 固定鼠标是「光标归程序」，于是拖不动窗口、点不到别的控件、够不着标题栏。
    // 按住右键就把这一段让出来——转视角停下、光标显出来、位置不再被拽回中心，
    // 抬起右键之后下一次移动会重新起一段手势（重新钉住）。
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_gesture == LookGesture.DragPrimaryButton)
        {
            BeginDrag(e);
            return;
        }

        if (!e.GetCurrentPoint(_previewer).Properties.IsRightButtonPressed) return;

        if (_suspended) return;

        EndLook();
        _suspended = true;
        Debug.WriteLine(
            "[PREVIEWER][input.right] 按下 suspended=True " +
            "note=这一段不转视角、光标自由、不再钉回中心");
    }

    // 松开右键。这是主要那条路——在视口里按住在视口里松开。松开发生在别处（拖到侧边栏上、
    // 拖出窗口）时这个事件不会来，补上它的是 OnPointerMoved 里那次「按键状态已经放开了」的检查。
    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_gesture == LookGesture.DragPrimaryButton)
        {
            if (!_looking ||
                e.GetCurrentPoint(_previewer).Properties.PointerUpdateKind is not PointerUpdateKind.LeftButtonReleased)
                return;

            Debug.WriteLine("[PREVIEWER][input.drag] 抬起（事件）looking=False");
            EndLook();
            return;
        }

        if (e.GetCurrentPoint(_previewer).Properties.PointerUpdateKind is not PointerUpdateKind.RightButtonReleased)
            return;

        if (!_suspended) return;

        _suspended = false;
        Debug.WriteLine(
            "[PREVIEWER][input.right] 抬起 suspended=False note=下一次移动重新起手势并钉住光标");
    }

    // 宿主在窗口失活时调它。Alt+Tab 之后指针的进出、捕获丢失、右键的抬起都送到别的窗口去了，
    // 而挂着的捕获、参照点、以及「右键按着」那位状态会让光标回来时的第一帧跳一下
    // （或者干脆再也不转）。
    //
    // 它是 public：宿主在另一个程序集里（Sample），而这件事只有宿主知道该在什么时候做。
    public void ReleaseLook()
    {
        Debug.WriteLine(
            $"[PREVIEWER][input.release] looking={_looking} suspended={_suspended} " +
            "note=失活时抬起事件不会来，所以右键挂起也要在这里清掉");
        _suspended = false;
        EndLook();
    }

    private void EndLook()
    {
        if (!_looking) return;

        // 先把状态清掉再放捕获：Capture(null) 会同步回调 OnPointerCaptureLost，
        // 那时 _looking 已经是 false，不会绕回来再结束一次。
        _looking = false;
        _pointer?.Capture(null);
        _pointer = null;
        ShowCursor();
        _previewer.RaiseLookEnded();
    }

    // 该不该钉。三条否定：宿主关掉了、平台上没有这个能力、上一次钉的时候发现钉不住。
    //
    // 窗口没激活时也不钉：指针从别的窗口上扫过时把它拽到这个窗口中心是很讨厌的事，
    // 而真要转视角本来就该先点一下让窗口激活。这一条只挡住「钉」，不挡转视角——
    // 挡转视角会让「窗口在后台时鼠标划过画面不动」变成另一件要解释的事。
    //
    // 判据只有 Avalonia.Controls 给的 Window.IsActive：TopLevel 上在 12.1.3 没有这个概念
    // （实测它的公开成员里既没有 IsActive 也没有激活状态），所以拿不到窗口就按激活处理——
    // 嵌入式的顶层本来也没有「激活」这件事。
    private bool CanPin()
    {
        return _confine
               && !_pinBroken
               && Win32Cursor.IsSupported
               && (TopLevel.GetTopLevel(_previewer) is not Window window || window.IsActive);
    }

    // 把光标钉到视口中心，返回「钉完之后指针在控件坐标系里的位置」。
    //
    // 返回的是读回来的那个点，不是请求的那个点：屏幕坐标到 DIP 的换算会把中心舍到整像素上
    // （窗口宽是奇数时差半个 DIP），而 SetCursorPos 在目标点不可达时还会把它挪到最近的可见点上，
    // 返回值却仍然说成功。拿请求值当参照点的话，每一次移动都会把这点偏差再加一遍——
    // 每秒几百次，画面自己就转起来了，而那看起来像灵敏度写爆了。
    private Point StartPin()
    {
        Point centre = new(_previewer.Bounds.Width / 2, _previewer.Bounds.Height / 2);
        var target = _previewer.PointToScreen(centre);

        HideCursor();

        if (!Win32Cursor.MoveTo(target.X, target.Y) || !Win32Cursor.TryRead(out var actual))
        {
            AbandonPin($"移动光标或读回位置失败 target=({target.X},{target.Y})");
            return centre;
        }

        var reference = _previewer.PointToClient(new PixelPoint(actual.X, actual.Y));

        // 判据是「钉完之后光标在不在控件里」，不是「和请求点是不是一模一样」。
        //
        // 后者是这里原来的写法，而它会误杀：手在 MoveTo 与 TryRead 两次系统调用之间本来就会动，
        // 实测有一次 requested=(850,483) actual=(843,476)——差 7 个像素就被当成「钉不住」，
        // 而 _pinBroken 是粘住的，一整个会话的固定鼠标当场作废。表现正是「鼠标没固定在中心、
        // 右键也呼不出鼠标」，可它前面已经连续五轮钉得好好的。
        //
        // 落点有偏差这件事本身不需要判：返回的参照点就是读回来的那个点，下一次的增量按真实位置算，
        // 偏差不累积。真正要拦的只有一种情形——请求点不可达（窗口有一部分在屏幕外）时系统会把
        // 光标挪到最近的可见点上，返回值仍然说成功，而那一点通常已经不在控件里。
        if (!new Rect(_previewer.Bounds.Size).Contains(reference))
        {
            AbandonPin(
                $"钉完之后光标不在控件里 requested=({target.X},{target.Y}) actual=({actual.X},{actual.Y}) " +
                $"reference=({reference.X:F0},{reference.Y:F0}) bounds={_previewer.Bounds.Size}");
            return reference;
        }

        // 成功本来是完全没有声音的，而「固定鼠标到底有没有生效」正是这一整条里唯一看不见的事。
        if (!_pinLogged)
        {
            _pinLogged = true;
            Debug.WriteLine(
                $"[PREVIEWER][input.pin] 固定鼠标已生效 centre=({target.X},{target.Y}) " +
                $"actual=({actual.X},{actual.Y}) reference=({reference.X:F0},{reference.Y:F0}) " +
                $"偏差=({reference.X - centre.X:F1},{reference.Y - centre.Y:F1})px note=只打这一条");
        }

        return reference;
    }

    // 钉不住就整段放弃，并且把光标还回去。
    //
    // 只打一次：这件事一旦发生就会在每一次移动上发生，逐次打会把终端冲掉，
    // 而它要回答的问题（「固定鼠标为什么没生效」）一次就够了。
    private void AbandonPin(string reason)
    {
        _pinBroken = true;
        ShowCursor();

        if (_pinBrokenLogged) return;

        _pinBrokenLogged = true;
        Debug.WriteLine(
            $"[PREVIEWER][input.confine] 钉不住光标，本次会话退回「只捕获指针」 原因={reason} " +
            "note=视角照转，但手划到屏幕边上会停");
    }

    // 藏光标用 Cursor = None。钉住之后屏幕上那个箭头已经不再跟着手动了，
    // 留着它只会在中心闪——而闪烁的位置恰好是画面中心，最碍事的地方。
    private void HideCursor()
    {
        if (_cursorHidden) return;

        _cursorHidden = true;
        _cursorBeforeHide = _previewer.Cursor;
        _previewer.Cursor = new Cursor(StandardCursorType.None);
    }

    private void ShowCursor()
    {
        if (!_cursorHidden) return;

        _cursorHidden = false;
        _previewer.Cursor = _cursorBeforeHide;
        _cursorBeforeHide = null;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        _previewer.RaiseKeyChanged(e.Key, true);
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        _previewer.RaiseKeyChanged(e.Key, false);
    }
}
