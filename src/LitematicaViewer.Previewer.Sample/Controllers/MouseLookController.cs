using System.Diagnostics;
using Avalonia;

namespace LitematicaViewer.Previewer.Sample;

// 拖拽 -> 转视角。它只做三件事：订阅、把两次位置的差交给模型、把模型的结果推给 Previewer。
//
// 位置的差在这里算，而不是让 Previewer 给出「移动了多少」：差值是手势级的概念，
// 而「拖多远算转了多少度」是这一层的决定。Previewer 只负责说指针在哪。
internal sealed class MouseLookController : IDisposable
{
    // 每拖过一个 DIP 转多少度。0.25 度/DIP 下横着拖满 1024 宽的窗口约 256 度，
    // 转一圈要拖一个半屏宽——不至于一碰就天旋地转。
    //
    // 用 DIP 而不是物理像素：在 150% 缩放的显示器上，同样的手部动作应该转同样的角度，
    // 而按物理像素算的话那台机器上会快 1.5 倍，表现为「换了台显示器手感就变了」。
    //
    // internal 是给验收剧本用的：它拿这个值算期望的角度。脚本引用它意味着改这个数不会红，
    // 那是有意的——钉的是「拖多少像素转多少度」这条换算与方向，不是这个手感值本身。
    internal const float DegreesPerDip = 0.25f;

    // 一次拖拽里逐条打桩会把终端冲掉（移动事件一秒几百条），所以只打起点、每 40 条一条、
    // 以及终点的汇总。要回答的是「拖拽有没有作用到相机上、总共转了多少」。
    private const int MoveLogInterval = 40;

    private readonly Previewer _previewer;
    private readonly CameraModel _camera;

    private Point _last;
    private bool _dragging;
    private int _moves;
    private float _totalYaw;
    private float _totalPitch;
    private bool _disposed;

    internal MouseLookController(Previewer previewer, CameraModel camera)
    {
        _previewer = previewer;
        _camera = camera;

        _previewer.DragStarted += OnDragStarted;
        _previewer.DragMoved += OnDragMoved;
        _previewer.DragEnded += OnDragEnded;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _previewer.DragStarted -= OnDragStarted;
        _previewer.DragMoved -= OnDragMoved;
        _previewer.DragEnded -= OnDragEnded;
    }

    // 拖拽作废。宿主在窗口失活时调它：Alt+Tab 之后那个抬起事件不会来，
    // 而挂着的拖拽会让画面跟着光标乱转——看起来像鼠标坏了，不像接线问题。
    internal void CancelDrag()
    {
        if (!_dragging)
        {
            return;
        }

        Debug.WriteLine($"[SAMPLE][look.cancel] 拖拽被作废 moves={_moves} note=抬起事件不会来了");
        EndDrag();
    }

    private void OnDragStarted(Point position)
    {
        _dragging = true;
        _moves = 0;
        _totalYaw = 0f;
        _totalPitch = 0f;

        // 起点本身不产生旋转：角度是两次位置之差，只有一次位置时没有「差」可言。
        // 这里把上一次位置定下来，第一次移动才算得出来。
        _last = position;
        Debug.WriteLine(
            $"[SAMPLE][look.start] pos=({position.X:F0},{position.Y:F0}) " +
            $"yaw={_camera.Camera.Yaw:F2} pitch={_camera.Camera.Pitch:F2}");
    }

    private void OnDragMoved(Point position)
    {
        // 没有起点就没有可比的上一帧，硬算出来的差值是一段凭空的跳转。
        // 适配器那边已经拦了一道，这里再判一次是因为「拖拽中」是这一层自己的状态：
        // 它被 CancelDrag 清掉之后，迟到的移动事件还会照常送进来。
        if (!_dragging)
        {
            return;
        }

        Point delta = position - _last;
        _last = position;
        _moves++;

        // 往右拖 yaw 增大（往右转），往下拖 pitch 增大（往下看），与键鼠游戏里的鼠标一致。
        // 反过来的话平移用的还是视线方向，两处就是两套约定，手感会打架。
        float yaw = (float)delta.X * DegreesPerDip;
        float pitch = (float)delta.Y * DegreesPerDip;
        _totalYaw += yaw;
        _totalPitch += pitch;

        _camera.Look(yaw, pitch);
        _previewer.SetCamera(_camera.Camera);

        if (_moves == 1 || _moves % MoveLogInterval == 0)
        {
            Debug.WriteLine(
                $"[SAMPLE][look.move] moves={_moves} delta=({delta.X:F1},{delta.Y:F1}) " +
                $"total=({_totalYaw:F2},{_totalPitch:F2}) yaw={_camera.Camera.Yaw:F2} " +
                $"pitch={_camera.Camera.Pitch:F2}");
        }
    }

    private void OnDragEnded()
    {
        // 汇总打一条：逐次的值都被节流掉了，而「这一拖到底转了多少」正是要看的那个数。
        Debug.WriteLine(
            $"[SAMPLE][look.end] moves={_moves} total=({_totalYaw:F2},{_totalPitch:F2}) " +
            $"yaw={_camera.Camera.Yaw:F2} pitch={_camera.Camera.Pitch:F2} distance={_camera.Distance:F4}");
        EndDrag();
    }

    private void EndDrag()
    {
        _dragging = false;
        _moves = 0;
    }
}
