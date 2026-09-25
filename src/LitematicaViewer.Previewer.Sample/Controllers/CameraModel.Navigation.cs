using System.Diagnostics;
using System.Numerics;

namespace LitematicaViewer.Previewer.Sample;

// CameraModel 的两个连续动作：绕 Target 转视角、沿自己的视角平移。与缩放分在两个文件里，
// 是因为它们的驱动方式不同——缩放一格一格地来（滚轮），这两个是被连续输入推着的
// （拖拽一秒几百条、按住方向键每帧一次），探针与节流也就落在不同的人身上。
internal sealed partial class CameraModel
{
    // 绕 Target 转视角。增量以度为单位，方向照抄鼠标：往右拖 yaw 增大（往右转），
    // 往下拖 pitch 增大（往下看）。正负号只在这一处落地，控制器只管把手势的像素数喂进来。
    internal void Orbit(float deltaYaw, float deltaPitch)
    {
        Debug.Assert(
            float.IsFinite(deltaYaw) && float.IsFinite(deltaPitch),
            $"[SAMPLE][camera.orbit] 增量不是有限值 dyaw={deltaYaw} dpitch={deltaPitch}");

        Vector3 offset = _camera.Position - _target;
        float distance = offset.Length();

        // 相机压在 Target 上时「绕它转」没有定义，视图矩阵也早就退化了。
        Debug.Assert(
            distance > 1e-4f,
            $"[SAMPLE][camera.orbit] 相机贴在 Target 上，无法绕转 distance={distance} " +
            $"target=({_target}) pos=({_camera.Position})");

        // 仰角从 offset 反解，水平方向则取自 yaw。
        // 两个都从 offset 反解是不行的：俯角顶到 ±90 度时 offset 的水平分量是 0，
        // 方向无从谈起——而那不是病态输入，滚轮一路推近、鼠标一路往上拖都会到那儿。
        // yaw 在任何姿态下都有定义，所以由它给出水平方位。
        //
        // 立刻换成度：atan2 给的是弧度，而增量、夹取边界、CameraState.Pitch 全是度。
        // 混着用不会编译失败，只是角度差 57 倍，症状是「拖一下视角歪了一点点」——
        // 单位是这条链路上最容易漏、也最难看出来的一处。
        float elevation = float.RadiansToDegrees(
            MathF.Atan2(offset.Y, new Vector2(offset.X, offset.Z).Length()));
        float nextElevation = Math.Clamp(
            elevation + deltaPitch, -CameraState.MaxPitch, CameraState.MaxPitch);

        // back 是「从 Target 指向相机」的那个水平单位向量，也就是 -Forward。
        // 取 -deltaYaw：约定里 yaw 增大是往右转，而相机本身要往左绕——视线往右摆，
        // 眼就得往左站。两者反向，符号漏掉的表现是整个拖拽方向反过来。
        Vector3 back = -CameraState.ForwardOf(_camera.Yaw + deltaYaw, 0f);

        float radians = float.DegreesToRadians(nextElevation);
        float horizontal = distance * MathF.Cos(radians);
        float vertical = distance * MathF.Sin(radians);
        Vector3 position = _target + (back * horizontal) + (Vector3.UnitY * vertical);

        // 走 LookAt 而不是自己拼 yaw/pitch：pitch 的夹取、yaw 的取值域只有这一条路实现过，
        // 再写一份等于把「两套三角函数慢慢错开」这件事请回来。
        // 顺带一个想要的副作用：yaw 每次都被重新归到 (-180,180]，不随拖拽无界累积——
        // 累积到几万度之后 sin/cos 的精度会掉，而那是「转了很久之后视角开始抖」。
        _camera = CameraState.LookAt(position, _target, _camera.Fov, _camera.Near, _camera.Far);
    }

    // 沿自己的视角平移，Target 一起走。
    //
    // Target 跟着走而不是只挪相机：Target 是「在看哪儿」，把它留在原地的话平移之后
    // 相机看向的是空处，下一次绕转、推拉都会以那个空处为轴——表现是平移完再转视角，
    // 画面整个甩出去，而单看平移那一步完全正常。
    //
    // 刚体平移还保住一条不变量：到 Target 的距离不变。滚轮的夹取、绕转的半径、
    // 以及画面校验里「离得太近就跳过」的判据，用的都是这个距离。
    internal void Pan(float forwardUnits, float rightUnits)
    {
        Debug.Assert(
            float.IsFinite(forwardUnits) && float.IsFinite(rightUnits),
            $"[SAMPLE][camera.pan] 位移不是有限值 forward={forwardUnits} right={rightUnits}");

        Vector3 forward = _camera.Forward;

        // 只取水平分量：低头看地的时候 W 还应该往前走，而不是往地里钻。
        // pitch 被夹在 ±89.9 度，水平分量至少有 cos(89.9°)≈1.7e-3，归一化不会退化；
        // 真退化到 0 就说明俯角顶到了 ±90，那时「正前方」本来就没有水平定义。
        Vector3 groundForward = new(forward.X, 0f, forward.Z);
        Debug.Assert(
            groundForward.LengthSquared() > 1e-6f,
            $"[SAMPLE][camera.pan] 视线水平分量退化，定不出前进方向 forward=({forward}) pitch={_camera.Pitch}");

        groundForward = Vector3.Normalize(groundForward);

        // right = forward × up。y 轴朝上时这个叉积指向画面右边；
        // 写成 up × forward 得到的是左边，症状是 A/D 反了——两种写法单看都像对的。
        Vector3 right = Vector3.Normalize(Vector3.Cross(groundForward, Vector3.UnitY));

        Vector3 delta = (groundForward * forwardUnits) + (right * rightUnits);
        float distanceBefore = Distance;

        _camera = _camera with { Position = _camera.Position + delta };
        _target += delta;

        // 平移是刚体的：距离一点不该变。位置挪了而 Target 没挪（或挪的量不一样）时这条会红，
        // 而那种错的表现是「走两步之后镜头自己往前凑」，很难归因。
        Debug.Assert(
            MathF.Abs(Distance - distanceBefore) < 1e-4f * MathF.Max(1f, distanceBefore),
            $"[SAMPLE][camera.pan] 平移把距离改了 before={distanceBefore:F6} after={Distance:F6}");
    }

    // 这两个动作本身不打桩：它们由连续输入驱动，逐次打会把终端冲掉（拖拽一秒几百条、
    // 按住 W 时每帧一次），而「这一拖转了多少、这一段走了多远」这类汇总只有控制器知道
    // 该怎么划段，由它们打。缩放是离散的（一格滚轮一次），所以它自己打。
#if DEBUG
    private static void VerifyPanAndOrbit()
    {
        CameraModel camera = new(CameraState.Default, Vector3.Zero);
        float distance = camera.Distance;
        Vector3 forward = camera.Camera.Forward;
        Vector3 position = camera.Camera.Position;

        // 1. 沿视线前进：位移必须落在水平面上，方向正是视线在水平面上的投影。
        camera.Pan(1f, 0f);
        Vector3 moved = camera.Camera.Position - position;
        Expect(moved.Y, 0f, 1e-6f, "前进的垂直分量");

        Vector3 groundForward = Vector3.Normalize(new Vector3(forward.X, 0f, forward.Z));
        Expect(Vector3.Dot(Vector3.Normalize(moved), groundForward), 1f, 1e-5f, "前进方向");

        // 2. 刚体平移：距离不变、朝向不变。朝向这里用精确相等——平移根本没碰 yaw/pitch。
        Expect(camera.Distance, distance, 1e-5f, "平移后的距离");
        Debug.Assert(
            camera.Camera.Forward == forward,
            $"[SAMPLE][camera.pan] 平移把朝向改了 before={forward} after={camera.Camera.Forward}");

        // 3. Target 一起走。留在原地的话相机就在看空处，下一次绕转会以那一点为轴把画面甩出去。
        Expect(camera.Target.Length(), 1f, 1e-5f, "平移后的 Target 距离");

        // 4. 右移是右移。叉积写反的话这一条会得到 -1，而 A/D 反向从画面上看只是「手感奇怪」，
        //    不报错，所以只能靠算。
        Vector3 beforeRight = camera.Camera.Position;
        camera.Pan(0f, 1f);
        Vector3 rightMoved = camera.Camera.Position - beforeRight;
        Vector3 right = Vector3.Normalize(Vector3.Cross(groundForward, Vector3.UnitY));
        Expect(Vector3.Dot(Vector3.Normalize(rightMoved), right), 1f, 1e-5f, "右移方向");

        // 5. 低头看地时前进仍走水平面，不往地里钻。
        CameraModel lookingDown = new(CameraState.LookAt(new Vector3(0f, 3f, 0f), Vector3.Zero), Vector3.Zero);
        Vector3 downPosition = lookingDown.Camera.Position;
        lookingDown.Pan(1f, 0f);
        Expect((lookingDown.Camera.Position - downPosition).Y, 0f, 1e-6f, "俯视时前进的垂直分量");

        // 6. 绕转喂多少就转多少，距离与俯仰不变。
        CameraModel orbit = new(CameraState.Default, Vector3.Zero);
        float orbitDistance = orbit.Distance;
        float orbitPitch = orbit.Camera.Pitch;
        orbit.Orbit(90f, 0f);
        Expect(Wrap(orbit.Camera.Yaw - CameraState.Default.Yaw), 90f, 0.05f, "绕转 90 度的 yaw 增量");
        Expect(orbit.Camera.Pitch, orbitPitch, 0.05f, "只转 yaw 时的 pitch");
        Expect(orbit.Distance, orbitDistance, 1e-4f, "绕转后的距离");

        // 7. 俯仰增量同样是喂进去的那个数，而且两头都顶得住。
        orbit.Orbit(0f, 10f);
        Expect(orbit.Camera.Pitch, orbitPitch + 10f, 0.05f, "低头 10 度");
        orbit.Orbit(0f, 500f);
        Expect(orbit.Camera.Pitch, CameraState.MaxPitch, 0.05f, "一直往下拖");
        Debug.Assert(
            IsFinite(orbit.Camera.Position),
            $"[SAMPLE][camera.orbit] 顶到俯仰边界后位置不是有限值 pos=({orbit.Camera.Position})");
        orbit.Orbit(0f, -1000f);
        Expect(orbit.Camera.Pitch, -CameraState.MaxPitch, 0.05f, "一直往上拖");

        // 8. 反着拖回去应该回到原处。方向对不上时单次绕转看起来都对，只有往返才暴露——
        //    和滚轮那条一样，用户看到的是「拖过去再拖回来，画面没回原样」。
        CameraModel roundTrip = new(CameraState.Default, Vector3.Zero);
        Vector3 startPosition = roundTrip.Camera.Position;
        roundTrip.Orbit(37f, 21f);
        roundTrip.Orbit(-37f, -21f);
        Expect((roundTrip.Camera.Position - startPosition).Length(), 0f, 1e-4f, "绕转一来一回的位置");

        Debug.WriteLine(
            $"[SAMPLE][camera.orbit] yaw/pitch 增量、俯仰两端夹取、一来一回全通过 distance={orbitDistance:F4}");
        Debug.WriteLine(
            $"[SAMPLE][camera.pan] 方向/水平面/刚体性/Target 跟随全通过 distance={distance:F4}");
    }
#endif
}
