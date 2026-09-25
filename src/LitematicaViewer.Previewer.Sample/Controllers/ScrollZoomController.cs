using System;

namespace LitematicaViewer.Previewer.Sample;

// 滚轮 -> 缩放。它只做三件事：订阅、把增量交给模型、把模型的结果推给 Previewer。
//
// 单独成一个类而不是塞进 MainWindow：滚轮是拉近还是推远、一档滚多远、要不要加惯性，
// 都是这一层的决定；而「谁订阅了什么」集中在一处，将来加第二个控制器时不会漏退订。
//
// 它不碰 GL、也不持有 GL 对象，R2 自然成立；Scrolled 由输入适配器在 UI 线程上投递，
// 所以这里的 SetCamera 也落在 UI 线程上（R4）。
internal sealed class ScrollZoomController : IDisposable
{
    private readonly Previewer _previewer;
    private readonly CameraModel _camera;
    private bool _disposed;

    internal ScrollZoomController(Previewer previewer, CameraModel camera)
    {
        _previewer = previewer;
        _camera = camera;

        // 向上滚是拉近，这个方向由 CameraModel 定义（正 steps 减小距离）；
        // 这里只转发原始增量。将来若要反过来，改的是模型里的那个比例，不是这儿。
        _previewer.Scrolled += OnScrolled;
    }

    public void Dispose()
    {
        // 重复 Dispose 不炸：宿主的关闭路径不止一条（窗口 Closed、异常退出），
        // 退订两次的第二个 -= 是无害的，但模式写成先判一下更省心。
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _previewer.Scrolled -= OnScrolled;
    }

    private void OnScrolled(float delta)
    {
        _camera.Zoom(delta);
        _previewer.SetCamera(_camera.Camera);
    }
}
