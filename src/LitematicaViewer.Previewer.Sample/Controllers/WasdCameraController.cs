using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Avalonia.Input;

namespace LitematicaViewer.Previewer.Sample;

// 按键 -> 平移。W/S 沿视线在水平面上的投影前后走，A/D 左右走。
//
// 它记的是「哪些键现在按着」，不是「发生了一次按键」：按键会被平台自动重复
// （按住 W 一秒能来几十条 KeyDown），按状态处理天然幂等，按边沿处理就得自己去重，
// 而漏掉一条重复的表现是「按住不动」——很难归因。
//
// 位移在 Tick 里按时间积分，不在按键事件里按次累加：一次按键事件不携带时间，
// 而「按住一秒该走多远」只能由时间来定。这也顺带让走动快慢与键盘重复率无关。
internal sealed class WasdCameraController : IDisposable
{
    // 每秒走多少世界单位。渲染器里那个单位立方体就是按「一个 MC 方块 = 1×1×1 世界单位」
    // 画的（MC 里一方块也是一米），所以 1.5 就是每秒走过一个半方块。
    //
    // 材质包的分辨率（16x16 / 256x256 / 2048x2048）不影响这个数：它决定的是一个方块贴多少纹素，
    // 也就是纹素密度，而方块的世界尺寸始终是 1。要按材质包配的是贴图采样（mipmap、过滤），不是速度。
    //
    // internal 是给验收剧本用的：它拿这个值算期望的位移。脚本引用它意味着改这个数不会红，
    // 那是有意的——钉的是「按了多久走多远」这条积分关系，不是这个速度值本身。
    internal const float UnitsPerSecond = 1.5f;

    private readonly Previewer _previewer;
    private readonly CameraModel _camera;
    private readonly HashSet<Key> _pressed = [];

    // 上一帧有没有在动，用来给日志划段：逐帧打会把终端冲掉，而一次走动的汇总
    // （走了多久、多远、往哪个方向）才是要看的东西。
    private bool _moving;
    private double _movingSeconds;
    private float _movedUnits;
    private bool _disposed;

    internal WasdCameraController(Previewer previewer, CameraModel camera)
    {
        _previewer = previewer;
        _camera = camera;

        _previewer.KeyChanged += OnKeyChanged;
        _previewer.Tick += OnTick;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _previewer.KeyChanged -= OnKeyChanged;
        _previewer.Tick -= OnTick;
    }

    // 把所有键当作已放开。宿主在窗口失活时调它：Alt+Tab 走了之后抬起事件不会来，
    // 而还按着的 W 会让相机一直往前走——回来时看到的是镜头自己飞走了，
    // 而不是「有个键卡住了」。
    internal void ReleaseKeys()
    {
        if (_pressed.Count == 0)
        {
            return;
        }

        Debug.WriteLine(
            $"[SAMPLE][camera.wasd] 释放全部按键 keys=[{string.Join(',', _pressed)}] note=抬起事件不会来了");
        _pressed.Clear();
    }

    private void OnKeyChanged(Key key, bool isDown)
    {
        if (!IsMovementKey(key))
        {
            // 别的键（包括系统塞进来的）不归它管，只记一笔。
            Debug.WriteLine($"[SAMPLE][camera.wasd] 无关按键 key={key} down={isDown}");
            return;
        }

        // 平台自动重复会让同一个键的 KeyDown 一秒来几十条，而它们在集合上是幂等的。
        // 只在这个键真的改了状态时才打：日志因此天然只剩真实的状态变化，
        // 不必再套一层节流，也不会把「按住了」这件事打上几十遍。
        bool changed = isDown ? _pressed.Add(key) : _pressed.Remove(key);
        if (changed)
        {
            Debug.WriteLine(
                $"[SAMPLE][camera.wasd] key={key} down={isDown} pressed=[{string.Join(',', _pressed)}]");
        }
    }

    private void OnTick(double delta)
    {
        // 一正一负同时按着是 0：两个键都按下去不该抵消成「往前走一半」，也不该两边都算。
        float forward = Axis(_pressed.Contains(Key.W), _pressed.Contains(Key.S));
        float right = Axis(_pressed.Contains(Key.D), _pressed.Contains(Key.A));

        if (forward == 0f && right == 0f)
        {
            EndMovement();
            return;
        }

        // 斜着走不能比直着走快。两个方向键同时按下时向量长度是 √2，
        // 直接乘上去的话斜向会快 41%——而这只在同时按两个键的时候出现，很难注意到。
        Vector2 axis = new(right, forward);
        if (axis.LengthSquared() > 1f)
        {
            axis = Vector2.Normalize(axis);
        }

        // delta 已被 Previewer 夹在 0.25 秒以内，所以卡顿时最坏是走得慢一点，
        // 而不是一步跨出去很远。
        float step = UnitsPerSecond * (float)delta;
        if (step <= 0f)
        {
            // 首帧的 delta 是 0，也就没有位移要推出去。白推一次会让相机版本号 +
            // 而画面一模一样，那是 Debug 侧的画面校验里凭空多出来的一次。
            return;
        }

        _camera.Pan(axis.Y * step, axis.X * step);
        _previewer.SetCamera(_camera.Camera);

        if (!_moving)
        {
            _moving = true;
            _movingSeconds = 0;
            _movedUnits = 0f;
            Debug.WriteLine(
                $"[SAMPLE][camera.wasd] 开始移动 axis=({axis.X:F2},{axis.Y:F2}) " +
                $"pos=({_camera.Camera.Position}) target=({_camera.Target})");
        }

        _movingSeconds += delta;
        _movedUnits += step;
    }

    private void EndMovement()
    {
        if (!_moving)
        {
            return;
        }

        _moving = false;
        Debug.WriteLine(
            $"[SAMPLE][camera.wasd] 移动结束 seconds={_movingSeconds:F3} units={_movedUnits:F4} " +
            $"expected={UnitsPerSecond * _movingSeconds:F4} pos=({_camera.Camera.Position}) " +
            $"target=({_camera.Target})");
    }

    private static float Axis(bool positive, bool negative) =>
        positive == negative ? 0f : positive ? 1f : -1f;

    private static bool IsMovementKey(Key key) => key is Key.W or Key.A or Key.S or Key.D;
}
