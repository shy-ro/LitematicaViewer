using System.Diagnostics;
using Avalonia;

namespace LitematicaViewer.Previewer.Sample;

// 指针增量 -> 转视角。它只做三件事：订阅、把增量乘上灵敏度交给模型、把模型的结果推给 Previewer。
//
// 增量是适配器算好的（事件给的就是增量），这里不再自己记住上一次位置。
// 固定鼠标把光标钉在视口中心并每次拽回去，参照点因此归适配器所有——只有它知道光标被挪到哪儿；
// 这一层要做的只是「多少 DIP 算多少度」，而那个换算的比例（灵敏度）是控件属性，不是这里的常量：
// 它是个手感参数，取决于手、鼠标 DPI、屏多大，由侧边栏调。
//
// 手势的起止也不由它管：什么时候开始算「在转」、右键按住时怎么让开，是适配器的事
// （它才拿得到指针、捕获与光标）。所以这里没有「取消」的口子：
// 宿主失活时该做的是让适配器结束手势，那个 LookEnded 会一路走到这里来收尾。
internal sealed class MouseLookController : IDisposable
{
    // 一次手势里逐条打桩会把终端冲掉（移动事件一秒几百条），所以只打起点、每 40 条一条、
    // 以及终点的汇总。要回答的是「增量有没有作用到相机上、这一程总共转了多少」。
    private const int MoveLogInterval = 40;
    private readonly CameraModel _camera;

    private readonly Previewer _previewer;
    private bool _disposed;

    private int _moves;
    private float _totalPitch;
    private float _totalYaw;

    internal MouseLookController(Previewer previewer, CameraModel camera)
    {
        _previewer = previewer;
        _camera = camera;

        _previewer.LookStarted += OnLookStarted;
        _previewer.LookMoved += OnLookMoved;
        _previewer.LookEnded += OnLookEnded;
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _previewer.LookStarted -= OnLookStarted;
        _previewer.LookMoved -= OnLookMoved;
        _previewer.LookEnded -= OnLookEnded;
    }

    private void OnLookStarted()
    {
        // 起点本身不产生旋转：这一段之前攒下的计数在这里归零，所以「这一程转了多少」
        // 只算这一段里的增量。参照点不在这里——它跟着光标，在适配器手里。
        _moves = 0;
        _totalYaw = 0f;
        _totalPitch = 0f;

        Debug.WriteLine(
            $"[SAMPLE][look.start] yaw={_camera.Camera.Yaw:F2} pitch={_camera.Camera.Pitch:F2} " +
            $"lookSensitivity={_previewer.LookSensitivity}");
    }

    // 增量直接换成角度，没有「第一次不算」这一说：起手势那一次不产生增量，
    // 所以到这里来的每一条都是真的手部移动。
    private void OnLookMoved(Vector delta)
    {
        _moves++;

        // 灵敏度每条都重新读：拖滑块的手感要立刻感觉得到，而手势可以持续几秒——
        // 在起手势时取一份存着的话，改值要等下一次起手势才生效，那看起来像卡了。
        var sensitivity = _previewer.LookSensitivity;

        // 往右移 yaw 增大（往右转），往下移 pitch 增大（往下看），与键鼠游戏里的鼠标一致。
        // 反过来的话平移用的还是视线方向，两处就是两套约定，手感会打架。
        var yaw = (float)delta.X * sensitivity;
        var pitch = (float)delta.Y * sensitivity;
        _totalYaw += yaw;
        _totalPitch += pitch;

        _camera.Look(yaw, pitch);
        _previewer.SetCamera(_camera.Camera);

        if (_moves == 1 || _moves % MoveLogInterval == 0)
            Debug.WriteLine(
                $"[SAMPLE][look.move] moves={_moves} delta=({delta.X:F1},{delta.Y:F1}) " +
                $"total=({_totalYaw:F2},{_totalPitch:F2}) yaw={_camera.Camera.Yaw:F2} " +
                $"pitch={_camera.Camera.Pitch:F2}");
    }

    private void OnLookEnded()
    {
        // 汇总打一条：逐次的值都被节流掉了，而「这一程到底转了多少」正是要看的那个数。
        // 灵敏度也打：一程里在侧边栏上改过它的话，total 与起点那一行会对不上——
        // 而有这一栏就能一眼看出是改过了，不是换算错了。
        Debug.WriteLine(
            $"[SAMPLE][look.end] moves={_moves} total=({_totalYaw:F2},{_totalPitch:F2}) " +
            $"yaw={_camera.Camera.Yaw:F2} pitch={_camera.Camera.Pitch:F2} distance={_camera.Distance:F4} " +
            $"lookSensitivity={_previewer.LookSensitivity}");

        _moves = 0;
    }
}
