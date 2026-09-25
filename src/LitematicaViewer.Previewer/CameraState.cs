using System.Diagnostics;
using System.Numerics;

namespace LitematicaViewer.Previewer;

// 相机的全部状态。不可变，没有 setter——状态的权威在控制器侧（CameraModel），
// Previewer 只是它的消费者。
//
// 约定照抄 Minecraft：yaw 0 朝 +Z，yaw 增大绕 +Y 转向 -X（俯视图里顺时针），
// pitch 增大是向下看，单位是度。
//
// 这条约定必须只有一份实现。画面是 (Position, Yaw, Pitch) 喂给视图矩阵算出来的，
// 将来的屏幕射线拾取是同一个状态喂给另一套三角函数算出来的；两边但凡有一点不一致
// （yaw 转哪个方向、pitch 正负是不是向下、右手还是左手），症状是准星压着 A 却拾到 B，
// 不抛异常、不报错，距离永远差一点——而且只有一边存在的时候根本测不出来。
// 所以 ForwardOf 是唯一的换算入口，别处一个三角函数都不许再推。
//
// record struct 而不是 record class：每帧都要读、还要按值比较（相机没动就不必做某些事），
// 值语义正好，且不产生垃圾。
public readonly record struct CameraState(
    Vector3 Position,
    float Yaw,
    float Pitch,
    float Fov,
    float Near,
    float Far)
{
    // pitch 夹在 ±89 度。到 ±90 度时 forward 与 up 共线，CreateLookAt 退化，
    // 画面整块消失而 GL 一声不吭——相机控制里最经典的那个 bug。
    //
    // 取 89 而不是贴着 90 的 89.9：留 1 度是为了「方向感」。顶到 89.9 时画面上已经
    // 完全没有地平线的迹象，抬头低头都只剩一片颜色，而那正是自由转头之后最容易迷路的地方；
    // 差这 0.9 度对视图矩阵没有任何区别（cos(1°)=0.99985），对手感却是「还知道哪边是上」。
    //
    // 夹在取用点（ForwardOf）而不是构造点：CameraState 是纯数据，控制器往里塞什么都不该被悄悄改掉；
    // 而夹在取用点意味着读 Forward 的每一处（视图矩阵、将来的屏幕射线）拿到的是同一个方向。
    public const float MaxPitch = 89f;

    private const float DefaultFov = 45f;
    private const float DefaultNear = 0.1f;
    private const float DefaultFar = 100f;

    // 默认视角就是 Phase C 写死在渲染器里的那三个数，朝向立方体中心。
    // 保留同一组数值是有意的：Phase C 审计里记的三个可见面的像素数由它算出来，
    // 换个数那些记录就全部作废，得重测一遍。
    public static CameraState Default { get; } = LookAt(new Vector3(2.6f, 2.0f, 3.4f), Vector3.Zero);

    public Vector3 Forward => ForwardOf(Yaw, Pitch);

    public static Vector3 ForwardOf(float yawDegrees, float pitchDegrees)
    {
        float yaw = float.DegreesToRadians(yawDegrees);
        float pitch = float.DegreesToRadians(Math.Clamp(pitchDegrees, -MaxPitch, MaxPitch));
        float cosPitch = MathF.Cos(pitch);

        return new Vector3(-MathF.Sin(yaw) * cosPitch, -MathF.Sin(pitch), MathF.Cos(yaw) * cosPitch);
    }

    // LookAt 是 ForwardOf 的逆。来回一趟必须回到原方向，这条往返由样例自检里的断言守着：
    // 两套三角函数一旦不同步，症状是准星和画面错开，而不是编译错误。
    public static CameraState LookAt(
        Vector3 eye,
        Vector3 target,
        float fov = DefaultFov,
        float near = DefaultNear,
        float far = DefaultFar)
    {
        Vector3 direction = Vector3.Normalize(target - eye);

        // pitch 前面那个负号：约定里 pitch 增大是向下看，而 direction.Y 向下是负的。
        float pitch = float.RadiansToDegrees(MathF.Asin(-direction.Y));

        // atan2(-x, z) 是 -sin(yaw) 与 cos(yaw) 的逆。yaw 因此落在 (-180,180]，正负 180 附近会跳变;
        // 不归一化到 [0,360)：sin/cos 本来就周期，归一化只会让控制器里累积的 yaw 在某处被莫名重置，
        // 而那表现成相机突然转身——比跳变难查得多，因为只有转过几圈之后才出现。
        float yaw = float.RadiansToDegrees(MathF.Atan2(-direction.X, direction.Z));

        return new CameraState(eye, yaw, pitch, fov, near, far);
    }

    public Matrix4x4 GetViewMatrix() => Matrix4x4.CreateLookAt(Position, Position + Forward, Vector3.UnitY);

    public Matrix4x4 GetProjectionMatrix(float aspectRatio) =>
        Matrix4x4.CreatePerspectiveFieldOfView(float.DegreesToRadians(Fov), aspectRatio, Near, Far);

    // 相机版本号之外还给一个「这个状态是不是能拿来画」的判断，供 SetCamera 的断言用。
    // 放在类型上而不是 Previewer 里：将来控制器自建 CameraState 时也能就地校验，
    // 不必等到 SetCamera 被调用才发现自己算出了 near=0。
    internal bool IsValid(out string reason)
    {
        if (!float.IsFinite(Position.X) || !float.IsFinite(Position.Y) || !float.IsFinite(Position.Z))
        {
            reason = $"position 不是有限值 pos={Position}";
            return false;
        }

        if (!float.IsFinite(Yaw) || !float.IsFinite(Pitch))
        {
            reason = $"yaw/pitch 不是有限值 yaw={Yaw} pitch={Pitch}";
            return false;
        }

        if (Near <= 0f)
        {
            reason = $"near 必须为正 near={Near}";
            return false;
        }

        if (Far <= Near)
        {
            reason = $"far 必须大于 near far={Far} near={Near}";
            return false;
        }

        if (Fov is <= 0f or >= 180f)
        {
            reason = $"fov 必须落在 (0,180) fov={Fov}";
            return false;
        }

        reason = string.Empty;
        return true;
    }

#if DEBUG
    // 约定的契约检查，上下文初始化时跑一次。
    // 约定错了不会编译失败、不会抛异常，只会让画面和将来的拾取慢慢错开，所以把它写成可执行断言：
    // 轴向对不上、LookAt 与 Forward 不互逆、pitch 夹不住，三条里任何一条在这里就炸。
    internal static void VerifyConvention()
    {
        // 1. 把三个方向钉死。这三条就是约定本身，改任何一条都是在改接口。
        Vector3 plusZ = ForwardOf(0f, 0f);
        Vector3 minusX = ForwardOf(90f, 0f);
        Vector3 down = ForwardOf(0f, 90f);

        Debug.Assert(
            IsClose(plusZ, Vector3.UnitZ),
            $"[PREVIEWER][camera.convention] yaw=0 应朝 +Z got={plusZ}");
        Debug.Assert(
            IsClose(minusX, -Vector3.UnitX),
            $"[PREVIEWER][camera.convention] yaw=90 应朝 -X got={minusX}");

        // pitch=90 会被夹到 89，所以不是精确的 -Y，而是偏出 1 度——这正是夹取该有的样子：
        // 差一点点朝下，而不是退化成共线。0.9995 这个门槛等价于「夹取至少留到 88.2 度」，
        // 既是夹取的守卫，也把「视角锁是哪一档」钉在这里：改成 88 度以下立刻红。
        float downward = Vector3.Dot(down, -Vector3.UnitY);
        Debug.Assert(
            downward > 0.9995f,
            $"[PREVIEWER][camera.convention] pitch=90 应该基本朝下 got={down} dot={downward}");

        // 2. 夹住之后仍是单位向量，而不是缩成零向量——零向量的视图矩阵整个失效，而 GL 不报错。
        Debug.Assert(
            MathF.Abs(down.Length() - 1f) < 1e-5f,
            $"[PREVIEWER][camera.convention] pitch 夹住之后不是单位向量 got={down} length={down.Length()}");

        // 3. LookAt 与 Forward 互逆，随机方向各试一遍。
        //    y 分量压在 ±0.9 以内：接近正上/正下的时候 pitch 会顶到夹取边界，往返本来就不闭合，
        //    那是夹取的必然结果而不是 bug，拿它当反例会掩盖真正的不一致。
        Random random = new(20260925);
        for (int i = 0; i < 64; i++)
        {
            Vector3 unit = RandomUnitVector(random);
            Vector3 direction = new(unit.X, unit.Y * 0.9f, unit.Z);
            direction = Vector3.Normalize(direction);

            CameraState camera = LookAt(direction * 12f, Vector3.Zero, fov: 45f, near: 0.1f, far: 100f);
            Vector3 expected = -direction;

            Debug.Assert(
                IsClose(camera.Forward, expected, 1e-5f),
                $"[PREVIEWER][camera.convention] LookAt 往返不一致 dir={expected} forward={camera.Forward} " +
                $"yaw={camera.Yaw} pitch={camera.Pitch}");
        }

        Debug.WriteLine(
            "[PREVIEWER][camera.convention] yaw=0→+Z yaw=90→-X pitch=90→近似 -Y（夹到 89）；" +
            "LookAt/Forward 往返 64 次全通过");
    }

    private static Vector3 RandomUnitVector(Random random)
    {
        while (true)
        {
            Vector3 candidate = new(
                (float)((random.NextDouble() * 2) - 1),
                (float)((random.NextDouble() * 2) - 1),
                (float)((random.NextDouble() * 2) - 1));

            float length = candidate.Length();
            if (length > 0.1f)
            {
                return candidate / length;
            }
        }
    }

    private static bool IsClose(Vector3 actual, Vector3 expected, float tolerance = 1e-6f) =>
        (actual - expected).Length() <= tolerance;
#endif
}
