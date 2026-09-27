using System.Diagnostics;
using System.Numerics;
using LitematicaViewer.Previewer;
using LitematicaViewer.Previewer.Sample;

namespace LitematicaViewer.ShellPreview;

// 展台：拖动绕模型公转（带手速估计）、松手惯性衰减到缓慢自转、滚轮沿视线推拉。
// 语义与 Sample 的 TurntableController + CameraModel(FrameTurntable/Orbit/Zoom) 组合一致，
// 事件源从 Avalonia 手势换成 Win32 鼠标消息；参数沿用 Sample 的默认值
// （DragSensitivity 0.20 / SpinDamping 0.35 / IdleDelay 1.5s / IdleSpeed 8°/s）。
// 时间只从 Tick 来：移动事件没有时间戳，惯性与自转都是时间的函数。
internal sealed class Turntable
{
    // 进模式的展台角、俯仰区间、取景距离系数、公转阻尼——数值抄 Sample，注释在那边。
    private const float DefaultPitch = 25f;
    private const float MinPitch = -60f;
    private const float MaxPitch = 60f;
    private const float FrameFactor = 6.6f;
    private const float DragSensitivity = 0.20f;
    private const float SpinDamping = 0.35f;
    private const float SpinIdleDelay = 1.5f;
    private const float SpinIdleSpeed = 8f;
    private const float MeasureBlend = 0.5f;
    private const float StillVelocity = 0.01f;

    private readonly CameraModel _camera;

    private bool _dragging;
    private float _dragYaw;
    private float _velocity;
    private double _idleSeconds;

    internal Turntable(Scene scene)
    {
        // 远/近平面按模型尺寸：CameraState 默认 far=100 只够看一个单位立方体，
        // 真实模型的取景距离（radius×6.6）远超它——整台模型连同光环都在裁剪面
        // 后面，画面只剩清屏蓝（第一版 GL 化只出蓝底的根因）。near 跟着缩放，
        // 保住 24 位深度的精度比；far 给足放大倍数（zoom 无界，拉远 8 倍才裁）。
        var distance = scene.Radius * FrameFactor;
        var initial = CameraState.LookAt(new Vector3(2.6f, 2f, 3.4f), Vector3.Zero,
            near: distance * 0.005f, far: distance * 8f);
        _camera = new CameraModel(initial, Vector3.Zero);
        _camera.FrameTurntable(scene.Centre, DefaultPitch, distance);
    }

    internal CameraState Camera => _camera.Camera;

    internal void BeginDrag()
    {
        // 手一按住就停：抓着模型时自转还叠上来，手感是「拖不动」。
        _dragging = true;
        _velocity = 0f;
        _dragYaw = 0f;
        _idleSeconds = 0d;
    }

    internal void Drag(float deltaX, float deltaY)
    {
        if (!_dragging) return;

        // 往右拖 yaw 增大（相机往右绕）、往下拖 pitch 增大（更俯视），与 Sample 一致。
        var yaw = deltaX * DragSensitivity;
        _dragYaw += yaw;
        _camera.Orbit(yaw, deltaY * DragSensitivity, MinPitch, MaxPitch);
    }

    internal void EndDrag()
    {
        _dragging = false;
        _idleSeconds = 0d;
    }

    internal void Zoom(float steps) => _camera.Zoom(steps);

    // 推进一帧。返回「相机动过」，调用方据此决定要不要画。
    internal bool Tick(double seconds)
    {
        if (_dragging)
        {
            // 拖动中不积分：转了多少是手直接给的，这一帧攒的角度只用来估手速。
            _velocity = TurntableSpin.Measure(_dragYaw, seconds, _velocity, MeasureBlend);
            _dragYaw = 0f;
            return false;
        }

        if (seconds <= 0d) return false;

        _idleSeconds += seconds;

        // 自转不是到点突然起步：衰减目标从 0 换成自转速度，余速顺滑接上自转。
        var target = _idleSeconds >= SpinIdleDelay ? TurntableSpin.IdleSpeed(SpinIdleSpeed) : 0f;
        _velocity = TurntableSpin.Decay(_velocity, target, seconds, SpinDamping);

        if (MathF.Abs(_velocity) < StillVelocity)
        {
            _velocity = 0f;
            return false;
        }

        _camera.Orbit(_velocity * (float)seconds, 0f, MinPitch, MaxPitch);
        return true;
    }
}
