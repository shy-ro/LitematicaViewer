using System.Diagnostics;
using System.Numerics;

namespace LitematicaViewer.Previewer.Sample;

// CameraModel 的两个连续动作：转视角、沿自己的视角平移。与缩放分在两个文件里，
// 是因为它们的驱动方式不同——缩放一格一格地来（滚轮），这两个是被连续输入推着的
// （指针移动一秒几百条、按住方向键每帧一次），探针与节流也就落在不同的人身上。
internal sealed partial class CameraModel
{
    // 转视角。增量以度为单位，方向照抄鼠标：往右拖 yaw 增大（往右转），
    // 往下拖 pitch 增大（往下看）。正负号只在这一处落地，控制器只管把手势的像素数喂进来。
    //
    // 只改朝向、不动位置——这是第一视角与 orbit 的分界。orbit 版本做同一件事要先把 offset
    // 反解成仰角、再用 back 与水平半径把位置重新摆一遍，整条链上都是三角函数；
    // 而那一堆计算存在的唯一理由，是「位置动了、朝向却要保持看向 Target」。
    // 第一视角反过来：位置不动，朝向直接就是 yaw/pitch 两个数，那一路三角函数整个不需要存在。
    internal void Look(float deltaYaw, float deltaPitch)
    {
        Debug.Assert(
            float.IsFinite(deltaYaw) && float.IsFinite(deltaPitch),
            $"[SAMPLE][camera.look] 增量不是有限值 dyaw={deltaYaw} dpitch={deltaPitch}");

        _camera = _camera with
        {
            Yaw = Wrap(_camera.Yaw + deltaYaw),

            // pitch 在这里夹一次，取用时（CameraState.ForwardOf）还会夹一次。
            // 两处都是同一个界，所以夹不夹结果一样；在模型里夹的理由是让控制器手里的状态
            // 就是实际生效的那个——一个 pitch=500 的状态读出去比较、打日志都对不上现实。
            Pitch = Math.Clamp(_camera.Pitch + deltaPitch, -CameraState.MaxPitch, CameraState.MaxPitch),
        };
    }

    // 沿自己的视角平移。
    //
    // Target 是派生的（Position + Forward * Distance），所以它自动跟着走、也自动跟着视线扫，
    // 这里不需要维护第二份状态，也就没有「两份状态分叉」这条路可走。
    //
    // 前进方向只取视线的水平分量：低头看地的时候 W 还应该往前走，而不是往地里钻。
    // 上下移动（沿世界 Y 飞）留给将来真实剖面的需求，现在加只是猜。
    internal void Pan(float forwardUnits, float rightUnits)
    {
        Debug.Assert(
            float.IsFinite(forwardUnits) && float.IsFinite(rightUnits),
            $"[SAMPLE][camera.pan] 位移不是有限值 forward={forwardUnits} right={rightUnits}");

        Vector3 forward = _camera.Forward;

        Vector3 groundForward = new(forward.X, 0f, forward.Z);

        // pitch 被夹在 ±89.9 度，水平分量至少有 cos(89.9°)≈1.7e-3，归一化不会退化；
        // 真退化到 0 就说明俯角顶到了 ±90，那时「正前方」本来就没有水平定义。
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

        // 平移不改朝向。位置挪了而朝向跟着转（比如顺手又 LookAt 了一次某个点）时，
        // 走起来画面一边前进一边自转，那是「走两步就晕」的来源。
        Debug.Assert(
            _camera.Forward == forward,
            $"[SAMPLE][camera.pan] 平移把朝向改了 before={forward} after={_camera.Forward}");

        // 参考距离不该被平移碰到。「到 Target 的距离不变」现在是派生关系的直接结论，
        // 所以这条钉住的是「别在平移里顺手改缩放的尺度」——那会让滚轮的手感
        // 随你走过多少路而悄悄变化，而这很难归因到一个位移函数上。
        Debug.Assert(
            MathF.Abs(Distance - distanceBefore) < 1e-4f * MathF.Max(1f, distanceBefore),
            $"[SAMPLE][camera.pan] 平移把距离改了 before={distanceBefore:F6} after={Distance:F6}");
    }

    // 这两个动作本身不打桩：它们由连续输入驱动，逐次打会把终端冲掉（指针移动一秒几百条、
    // 按住 W 时每帧一次），而「这一拖转了多少、这一段走了多远」这类汇总只有控制器知道
    // 该怎么划段，由它们打。缩放是离散的（一格滚轮一次），所以它自己打。
#if DEBUG
    private static void VerifyLookAndPan()
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

        // 2. 平移不改朝向、不改参考距离。朝向这里用精确相等——平移根本没碰 yaw/pitch。
        Expect(camera.Distance, distance, 1e-5f, "平移后的参考距离");
        Debug.Assert(
            camera.Camera.Forward == forward,
            $"[SAMPLE][camera.pan] 平移把朝向改了 before={forward} after={camera.Camera.Forward}");

        // 3. Target 是派生的，平移必然把它一起带走。它要是留在原地，等同于相机在看空处——
        //    而「留在原地」在派生写法下不可能发生，所以这条顺手把派生关系本身钉住：
        //    往原点前方走一个单位之后，Target 离原点的距离恰好是 1。
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

        // 6. 转视角只改朝向，位置一动不动——这是第一视角的核心。
        //    位置跟着动就是 orbit 那个老实现，表现是「拖鼠标时画面绕着某个点公转」；
        //    而单看一次转视角的方向和灵敏度都是对的，只有连着转上一圈才看得出来。
        CameraModel look = new(CameraState.Default, Vector3.Zero);
        float lookDistance = look.Distance;
        float lookPitch = look.Camera.Pitch;
        Vector3 lookPosition = look.Camera.Position;
        look.Look(90f, 0f);
        Expect(Wrap(look.Camera.Yaw - CameraState.Default.Yaw), 90f, 0.05f, "转视角 90 度的 yaw 增量");
        Expect(look.Camera.Pitch, lookPitch, 0.05f, "只转 yaw 时的 pitch");
        Expect(look.Distance, lookDistance, 1e-4f, "转视角后的参考距离");
        Expect((look.Camera.Position - lookPosition).Length(), 0f, 1e-6f, "转视角后的相机位置");

        // 7. 俯仰增量同样是喂进去的那个数，而且两头都顶得住。
        look.Look(0f, 10f);
        Expect(look.Camera.Pitch, lookPitch + 10f, 0.05f, "低头 10 度");
        look.Look(0f, 500f);
        Expect(look.Camera.Pitch, CameraState.MaxPitch, 0.05f, "一直往下拖");
        Debug.Assert(
            IsFinite(look.Camera.Position),
            $"[SAMPLE][camera.look] 顶到俯仰边界后位置不是有限值 pos=({look.Camera.Position})");
        look.Look(0f, -1000f);
        Expect(look.Camera.Pitch, -CameraState.MaxPitch, 0.05f, "一直往上拖");

        // 8. 反着拖回去应该回到原处。方向对不上时单次转视角看起来都对，只有往返才暴露——
        //    和滚轮那条一样，用户看到的是「拖过去再拖回来，画面没回原样」。
        //    这里比 yaw/pitch 而不是比位置：位置本来就不动，比它等于什么都没验。
        CameraModel roundTrip = new(CameraState.Default, Vector3.Zero);
        roundTrip.Look(37f, 21f);
        roundTrip.Look(-37f, -21f);
        Expect(Wrap(roundTrip.Camera.Yaw - CameraState.Default.Yaw), 0f, 1e-3f, "转视角一来一回的 yaw");
        Expect(roundTrip.Camera.Pitch, CameraState.Default.Pitch, 1e-3f, "转视角一来一回的 pitch");

        Debug.WriteLine(
            $"[SAMPLE][camera.look] yaw/pitch 增量、位置不动、俯仰两端夹取、一来一回全通过 " +
            $"distance={lookDistance:F4}");
        Debug.WriteLine(
            $"[SAMPLE][camera.pan] 方向/水平面/刚体性/Target 派生全通过 distance={distance:F4}");
    }
#endif
}
