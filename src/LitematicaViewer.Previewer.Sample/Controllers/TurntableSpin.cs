using System.Diagnostics;
using System.Numerics;
using LitematicaViewer.Previewer;

namespace LitematicaViewer.Previewer.Sample;

// 展台的自转与惯性。全是标量算术，所以是静态纯函数：可以单元式地验，也不必碰 GL、窗口、事件。
//
// 它只算「角速度该是多少」，不碰相机——把速度换成绕圆心的角度是 CameraModel.Orbit 的事。
// 分开的理由是这两件事的验法完全不同：这里的判据是收敛性与帧率无关，
// 那里是几何不变量（半径不变、视线指着圆心）。
internal static class TurntableSpin
{
    // 自转方向：逆时针，从上往下看。
    //
    // 按相机约定推，而不是试出正负：yaw=0 朝 +Z、yaw=90 朝 -X，所以 yaw 增大时视线从 +Z 扫向 -X。
    // 俯视图里取 +X 向右、+Z 向下，那么 +Z 是屏幕下方、−X 是屏幕左方——下扫到左，是顺时针。
    // 相机绕着模型顺时针走，模型在画面里看起来就是逆时针转；反过来要让**模型**看着逆时针，
    // 相机就得逆时针走，也就是 yaw 减小。所以方向取负。
    internal const float IdleDirection = -1f;

    // 阻尼的兜底下界。滑块给 0 时整个衰减在一帧里完成，那仍然是有限的行为（瞬间到位）；
    // 只有负数才是不合法的。
    private const float MinDamping = 1e-3f;

    // 自转速度的绝对值。方向由 IdleDirection 定，取用时相乘。
    internal static float IdleSpeed(float degreesPerSecond) => IdleDirection * degreesPerSecond;

    // 一阶滞后：每帧走掉剩余差的 (1 − exp(−dt/τ))。
    //
    // 用 exp 而不是 dt/τ 的线性近似，是为了**帧率无关**：60fps 与 144fps 下同一段时间之后
    // 速度必须一样。线性近似下不同帧率的结果不同，表现是「换了台机器惯性就不一样」，
    // 而那看起来像手感问题，不像公式问题。
    internal static float Decay(float velocity, float target, double seconds, float damping)
    {
        if (seconds <= 0d || !float.IsFinite(velocity) || !float.IsFinite(target))
        {
            return velocity;
        }

        float tau = MathF.Max(damping, MinDamping);
        float blend = 1f - MathF.Exp((float)(-seconds / tau));

        // blend 落在 [0,1) 里，所以结果必然在速度与目标之间——不会冲过头。
        // 冲过头的样子是「甩一下转半圈，停下之前先往回倒一点」，而那看起来像回弹。
        return velocity + ((target - velocity) * blend);
    }

    // 手速估计：这一帧攒下的角度除以这一帧的时长，再做一次低通。
    //
    // 手速只能这样估：移动事件没有时间戳（事件只报增量），而 Tick 有。
    // 低通是为了滤掉单帧的抖动——松手那一刻的瞬时值本来就抖，直接用会让「轻轻一甩」和
    // 「猛一甩」在随机的一帧上取到同一个数。
    internal static float Measure(float accumulatedDegrees, double seconds, float previous, float blend)
    {
        if (seconds <= 0d || !float.IsFinite(accumulatedDegrees))
        {
            return previous;
        }

        float measured = accumulatedDegrees / (float)seconds;
        if (!float.IsFinite(measured))
        {
            return previous;
        }

        return previous + ((measured - previous) * Math.Clamp(blend, 0f, 1f));
    }

#if DEBUG
    // 由宿主在建控制器之前跑一次，参数就是侧边栏上那两个滑块的当前值（启动时是默认值）。
    // 用实际值而不是自己写一组数：这样验的是「现在这套手感参数下公式成立」，而不是
    // 「某个曾经写死的数下公式成立」。默认值本身合不合理由这里的两条范围断言守着。
    internal static void Verify(float damping, float idleDelay, float idleSpeed)
    {
        Debug.Assert(damping > 0f, $"[SAMPLE][showcase] 阻尼不是正数 damping={damping}");
        Debug.Assert(idleDelay >= 0f, $"[SAMPLE][showcase] 自转延迟是负数 delay={idleDelay}");
        Debug.Assert(idleSpeed > 0f, $"[SAMPLE][showcase] 自转速度不是正数 speed={idleSpeed}");

        // 1. 「逆时针 = yaw 减小」这条要按相机约定推一遍，而不是靠代码里那个负号自证。
        //    约定改了（谁把 ForwardOf 的方向反了）而这里没改，症状是自转方向反了——
        //    而自转方向反了没人会当成 bug 报上来。
        Vector3 atZero = CameraState.ForwardOf(0f, 0f);
        Vector3 atNinety = CameraState.ForwardOf(90f, 0f);
        Debug.Assert(
            (atZero - Vector3.UnitZ).Length() < 1e-5f && (atNinety + Vector3.UnitX).Length() < 1e-5f,
            $"[SAMPLE][showcase] 相机约定变了 yaw=0→({atZero}) expected=(0,0,1) yaw=90→({atNinety})");
        Debug.Assert(
            IdleDirection < 0f,
            $"[SAMPLE][showcase] 自转不是逆时针（yaw 减小）direction={IdleDirection}");
        Debug.Assert(
            IdleSpeed(idleSpeed) < 0f,
            $"[SAMPLE][showcase] 自转速度取出来不是负的 speed={IdleSpeed(idleSpeed)}");

        // 2. 衰减朝目标去，而且不冲过头。
        float velocity = 100f;
        for (int i = 0; i < 200; i++)
        {
            float next = Decay(velocity, 0f, 1d / 60d, damping);
            Debug.Assert(
                next <= velocity && next >= 0f,
                $"[SAMPLE][showcase] 衰减冲过头或反了 before={velocity:F4} after={next:F4}");
            velocity = next;
        }

        Debug.Assert(
            velocity < 1f,
            $"[SAMPLE][showcase] 衰减过慢，200 帧（约 3.3 秒）之后还剩 {velocity:F4} 度/秒 " +
            $"damping={damping}");

        // 3. 帧率无关：一步一秒与两步半秒必须落在同一个地方（误差在浮点量级）。
        //    这一条是选 exp 而不是线性近似的全部理由，所以必须钉住。
        float oneStep = Decay(100f, 0f, 1d, damping);
        float twoSteps = Decay(Decay(100f, 0f, 0.5d, damping), 0f, 0.5d, damping);
        Debug.Assert(
            MathF.Abs(oneStep - twoSteps) < 1e-3f,
            $"[SAMPLE][showcase] 衰减与帧率有关 one={oneStep:F6} two={twoSteps:F6} damping={damping}");

        // 4. 非正时长不动：Tick 的第一帧给的就是 0（没有上一帧），那时候不该有任何变化。
        Debug.Assert(
            Decay(42f, 0f, 0d, damping) == 42f,
            $"[SAMPLE][showcase] 时长为零时改了速度 got={Decay(42f, 0f, 0d, damping)}");
        Debug.Assert(
            Measure(10f, 0d, 42f, 0.5f) == 42f,
            "[SAMPLE][showcase] 时长为零时改了手速估计");

        // 5. 手速估计：攒了角度就是那个角度除以时长；没攒角度（手停住了）就该往下走。
        //    手停住还留着速度的话，松手时模型会接着转——而人以为自己是「停住再松开」。
        Expect(Measure(0.5f, 0.5d, 0f, 1f), 1f, 1e-4f, "攒 0.5 度、半秒");
        Expect(Measure(0f, 0.1d, 10f, 1f), 0f, 1e-4f, "手停住一帧");
        Expect(Measure(6f, 0.1d, 0f, 0.5f), 30f, 1e-4f, "低通取一半");

        // 6. 自转起来之后的稳态速度就是自转速度本身（衰减的目标到了就到头了）。
        float drift = 0f;
        for (int i = 0; i < 600; i++)
        {
            drift = Decay(drift, IdleSpeed(idleSpeed), 1d / 60d, damping);
        }

        Expect(drift, IdleSpeed(idleSpeed), 0.05f, "自转的稳态速度");

        Debug.WriteLine(
            $"[SAMPLE][showcase] 方向/收敛/帧率无关/零时长/手速估计/自转稳态全通过 " +
            $"damping={damping} idleDelay={idleDelay} idleSpeed={idleSpeed} direction=-1(逆时针)");
    }

    private static void Expect(float actual, float expected, float tolerance, string what)
    {
        Debug.Assert(
            MathF.Abs(actual - expected) < tolerance,
            $"[SAMPLE][showcase] {what} 不对 actual={actual:F6} expected={expected:F6} tolerance={tolerance}");
    }
#endif
}
