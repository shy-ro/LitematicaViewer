# 05 相机导航审计（Phase F）

Phase F：相机能走、能转。拆成三件事——指针移动转视角、W/A/S/D 沿视线平移、
以及把缩放的距离上下界从写死的常量改成可选入参。

不碰 Core，不碰 Previewer 的 GL 路径。动的三处：`CameraModel`（加两个动作）、
`Previewer` 的输入公开面（加看向手势）、以及 Debug 侧的画面校验。

**中途返工过一次，返工的内容也记在这里。** 第一版按 orbit 相机写的（拖拽绕 Target 公转），
拿给人看之后暴露了两个问题：

- **手感**：平移会把 Target 一起带走，于是「走两步再转视角」变成绕着脚边某个点公转。
  改成了**第一视角**：转视角只改朝向、不动位置。
- **崩**：转视角才让相机转到「某个面几乎正对着镜头侧面」的姿态，那里的像素断言直接红了，
  `Debug.Assert` 失败会杀掉进程。那是判据本身的前提不成立，不是画面画错——见「画面校验的两处失效」。

还顺手改成**指针一动就转**（第一版要按住左键），并把走动速度从 3 单位/秒降到 1.5。

## 文件与职责

    Controllers/CameraModel.cs             相机状态的权威 + 缩放 + 边界（可选）+ DEBUG 自检
    Controllers/CameraModel.Navigation.cs  转视角、沿自己的视角平移
    Controllers/MouseLookController.cs     指针位移 -> 转角度 -> SetCamera
    Controllers/WasdCameraController.cs    按键状态 -> 按时间积分的平移 -> SetCamera
    Controllers/ScrollZoomController.cs    滚轮 -> 缩放（Phase E，未改）
    Previewer.Events.cs                    新增 LookStarted / LookMoved / LookEnded
    PreviewerInputAdapter.cs               指针移动 -> 看向手势（首移捕获、离开或丢捕获收尾）
    MainWindow.axaml.cs                    三个控制器的装配与生命周期；失活时清输入状态
    Debug/DebugCube.cs                     IsVerifiable 的姿态前提 + 逐面的几何期望
    Previewer.cs                           VerifyFrame 的预算与跳过日志

模型拆成两个 partial 文件，理由是驱动方式不同：缩放一格一格地来（滚轮），
转视角与平移被连续输入推着走（指针一秒几百条、按住键每帧一次）。
打桩与节流的落点因此不同——`Zoom` 自己打，另两个由控制器按段落打。

## 公开面

`CameraModel`（`internal sealed partial class`）

    CameraModel(CameraState initial, Vector3 target, float? minDistance = null, float? maxDistance = null)
    CameraState Camera { get; }
    Vector3 Target { get; }                             // 派生：Position + Forward * Distance
    float Distance { get; }
    void Reset(CameraState camera)
    void Zoom(float steps)
    void Look(float deltaYaw, float deltaPitch)         // 度
    void Pan(float forwardUnits, float rightUnits)      // 世界单位

    ZoomRatioPerStep = 0.8f
    static void VerifyCameraMath()                      // #if DEBUG

`MouseLookController` / `WasdCameraController`（`internal sealed class`，`IDisposable`）

    MouseLookController(Previewer previewer, CameraModel camera)

    WasdCameraController(Previewer previewer, CameraModel camera)
    ReleaseKeys()

    DegreesPerDip = 0.25f     // 指针移过一个 DIP 转多少度
    UnitsPerSecond = 1.5f     // 按住一秒走多少世界单位（＝方块）

`Previewer` 新增的三个事件（位置是控件坐标，DIP）：

    event Action<Point>? LookStarted
    event Action<Point>? LookMoved
    event Action? LookEnded

`PreviewerInputAdapter` 新增一个公开方法，给宿主在窗口失活时用：

    public void ReleaseLook()

## 为什么是「第一视角」，而不是 orbit

第一版是 orbit：相机始终看向 Target，转视角时相机绕着 Target 公转。
撑住那个模型的理由是「轮廓质心落在画面中心」——那条像素断言只在看向中心时成立。

它在纸面上自洽，拿在手里不对。根因是平移：Target 是「在看哪儿」，平移必然把它一起带走，
于是走动之后再转视角，转的是脚下那个被带走的点。表现是画面绕着某个看不见的球心公转，
而单看一次转视角的方向、灵敏度、增量全都正确，只有连着转上一圈才看得出来。

改成第一视角之后：

- `Look` 只改 `Yaw` / `Pitch`，`Position` 一个 bit 都不动。老实现在做同一件事时要先把
  offset 反解成仰角、再用 `back` 与水平半径重新摆一遍位置，整条链上都是三角函数；
  而那一堆计算存在的唯一理由，是「位置动了、朝向却要保持看向 Target」。位置不动，
  朝向就**是** yaw/pitch 两个数，那一路三角函数整个不需要存在。
- `Target` 从「被绕的轴心」降级成**派生量**：`Position + Forward * Distance`，
  也就是「视线正前方 Distance 处那个点」。于是它自动跟着平移走、自动跟着视线扫，
  不存在「两份状态分叉」这条路可走——而分叉正是 orbit 那个版本的毛病来源。
- 那条像素断言没有丢：它现在有更直白的判据——**相机看向原点时才验**（见「画面校验的姿态前提」）。
  orbit 的存在理由本来就只有这一条。

代价是**没有上下移动**。真实建筑的剖面上要飞起来看时，缺的是「沿世界 Y 平移」，
到时候加的是 `Pan` 的一个纵向分量。

## 转视角：只改朝向

    _camera = _camera with
    {
        Yaw = Wrap(_camera.Yaw + deltaYaw),
        Pitch = Clamp(_camera.Pitch + deltaPitch, -MaxPitch, MaxPitch),
    };

没有别的了。三个点值得记：

- **yaw 要归到 `(-180,180]`。** 不归的话连续移动会把它累到几万度，那时 `sin/cos` 的精度开始掉，
  表现是「转了很久之后视角开始抖」。
- **pitch 在模型里夹一次**，取用时（`CameraState.ForwardOf`）还会夹一次。两处是同一个界，
  夹不夹结果一样；在模型里夹是为了让控制器手里的状态就是实际生效的那个——
  一个 `pitch=500` 的状态读出去比较、打日志都对不上现实。
- **方向约定落在这里**：往右移 `yaw` 增大（往右转），往下移 `pitch` 增大（往下看）。
  反过来的话平移用的还是视线方向，两处就是两套约定，手感会打架。

第一版里的那个真 bug 记一笔：仰角由 `atan2` 得到的是**弧度**，而增量、`MaxPitch`、`Pitch` 全是度。
混着用不会编译失败、不会抛异常，画面也照常出，只是差 57 倍。模型自检第一条抓住了它：

    [SAMPLE][camera] 只转 yaw 时的 pitch 不对 actual=0.437122 expected=25.045238 tolerance=0.05

第一视角的写法整段消掉了这个坑（不再有 `atan2`），但 `Wrap` 与角度单位仍然是同一类风险。

## 平移：四条

**Target 由 Position 派生**，所以它自动跟着走。它要是留在原地，等同于相机在看空处——
而在派生写法下这件事不可能发生。

**只取水平分量。** 低头看地时 W 还应该往前走，而不是往地里钻。
`pitch` 被夹在 ±89.9，水平分量至少 `cos(89.9°) ≈ 1.7e-3`，归一化不会退化。

**`right = forward × up`，不是 `up × forward`。** 后者得到的是左边，症状是 A/D 反向——
从画面上看只是「手感奇怪」，不报错，所以只能靠算：模型自检里有一条断言
`dot(位移, right)` 必须是 `+1`。

**斜向归一化。** W 与 D 同时按下时轴向量长度是 √2，不处理的话斜着走快 41%。
只在两个键同按时出现，玩的时候注意不到，量的时候一眼就看见。

## 缩放：边界可选，推拉不移动那个点

Phase E 里 `MinDistance = 2.4` / `MaxDistance = 7.0` 是写死的常量，取值依据是
「单位立方体外接球恰好塞进画面」。这个前提没了：能走能转的相机没有唯一的被看对象，
而写死的界会在模型里替人做决定——把相机卡在离目标点 2.4 的地方，
于是「凑近看一块砖」成了做不到的操作，那表现成「滚轮没反应」。

现在它们是构造函数的两个可选入参，**默认两个都不给**。只设一头是合法的
（比如「别推近到穿模」但不管多远）。`min > max` 有断言：夹取会退化成一个恒定值，
表现同样是「滚轮不动」，而那时离出错的地方已经很远。

`Sample` 侧一个界都不设：

    _camera = new CameraModel(CameraState.Default, Vector3.Zero);

构造函数的第二个参数是**一个点**而不是一个数：调用方手里本来就有那个点（立方体在原点），
而参考距离的初值就是「一开始看得有多远」。让调用方自己量一遍反而多一次出错的机会。

滚轮的动作是「沿视线把自己朝那个看着的点送过去」，位移取 `(旧距离 - 新距离)`：

    Target 前后 = Position + Forward * 旧距离
               = (Position + Forward * (旧 - 新)) + Forward * 新

两者相等，所以**推拉前后 Target 是同一个世界坐标**。这是「凑近看某块砖」时目光落在砖上
不动的全部依据；写成「位置沿视线挪、参考距离按比例缩」但两者不联动的话，表现是
「滚一下，看着的那块砖自己跑了」——而单看距离变化完全正常。

无界之后连推上千档会把距离推出浮点范围（`0.8^-6000` 溢出成 `Inf`），位置跟着变成
`Inf`，视图矩阵整块失效而 GL 一声不吭。这不是尺度限制从后门回来，是数值兜底：
算不出一个能画的相机就原地不动，并把这件事打出来。自检两头各推一次
（+6000 / -6000 档），断言距离**精确不变**且位置仍是有限值。

## 输入：指针一动就转

第一版要按住左键才算「起拖」。改掉了：**指针在控件上移动就是在转视角**，
不需要按任何键——这是第一人称的默认操作，多一个按键就多一道「为什么不动」的可能。

手势的起止在**适配器**里，不在控制器里：

- 第一次 `PointerMoved` 定下参照点并发 `LookStarted`，同时把指针**捕获**过来。
  捕获之后指针移出控件、甚至移出窗口时移动事件仍然送得到，转动不会在边界上突然停住；
  不捕获的话指针一出界画面就停，手感像卡死——而那是「没了后续事件」，不是「控制器没处理」。
- `PointerExited` 或 `PointerCaptureLost` 收尾。没有抬起事件可以用了，
  「这一段结束」只能由位置本身给出。
- 宿主在窗口失活时调 `ReleaseLook()`。

事件名从 `DragStarted/DragMoved/DragEnded` 改成 `LookStarted/LookMoved/LookEnded`：
没有键被按着的时候「拖拽」是假话，而事件名是给人读的。

`MouseLookController` 因此也**没有**「取消」那个口子了（原来有 `CancelDrag`）：
它只管按事件做增量，起止归适配器——而适配器才是拿得到指针与捕获的那一层。

## 画面校验的两处失效

转视角才让相机走到那些姿态，于是两条一直没暴露的问题同时现形。两条都不是画面画错。

### 一、读回预算声明了但从没被判断过

`MaxCameraVerifications = 24` 有常量、有注释、`_cameraVerifications++` 也在自增，
但**没有一处拿它做判断**。于是「一次会话封顶 24 次」实际是「相机每变一次读回一帧」——
转视角和按住 W 时相机每帧都在变，那就是每秒六十次 `glReadPixels`：每次强制 GPU 同步、
拷几 MB 回来，后面还跟着每帧五百多万次像素距离计算。

症状是「一动鼠标就卡」，而它看起来像渲染慢，不像校验慢。现在预算真的拦在**读回之前**
（读回才是贵的那一步），用尽后只说一次。跳过日志也节流了：跳过在自由导航里是连续发生的，
帧帧都打会盖过真正有用的那几行。

### 二、掠射姿态下「可见面至少占万分之一」不成立

`Debug.Assert` 失败是**直接终止进程**（exit 35），所以这一条的现场是「Debug 下转着转着就崩了」。

崩溃那一帧相机在 `(0.508, 3.760, 4.525)`，而 +X 面的平面是 `x = 0.5`——
相机几乎正贴在那张面的平面上，只出去了 0.008。这张面按判据**确实朝向相机**
（`0.508 > 0.5`），但它被看到的角度是 89.92°，投影成一条极扁的细片。
几何上它该有多少像素是能算的：

    相机到面心 5.8836，法线方向上只出去 0.00804，入射角余弦 = 0.00804 / 5.8836 = 0.001367
    半高 = 5.8836 × tan(22.5°) = 2.4372，4:3 下半宽 = 3.2496
    期望像素 = 1024×768 × 0.001367 / (4 × 2.4372 × 3.2496) = 33.9

实测 **34**。分毫不差——画得一点没错，错的是那个门槛：它写的是「至少占画面万分之一」，
1024×768 下就是 78 个像素。**这个门槛隐含了「相机在固定体对角线上」这个前提**，
而 Phase F 之前相机只能沿那条线推拉，永远碰不到掠射姿态。

现在每一面的期望像素按几何算（面面积 1、投影因子 `incidence / |toFace|`、
该深度处可见的世界面积 `4·halfHeight·halfWidth`），判据是实测不低于期望的 0.35 倍；
期望低于 8 个像素时不断言——几个像素的差别里光栅化的量化误差比信号还大。
判据只用相机自己的参数（位置、朝向、fov、宽高比），**不碰投影矩阵**：
投影矩阵漏了转置正是质心那条断言要抓的东西，把它算进期望里等于把那条检查自己关掉。

同一条前提还藏在另一处，「最小的可见面至少占固体的 5%」——相机能自由转之后，
一个掠射面的合法面积可以比旁边正对着的面小三个数量级。这条删掉了，
逐面的几何期望已经覆盖了它想抓的东西。

`Sample` 把崩溃那个姿态本身写进了剧本（`GrazingPosition`），它现在是回归用例：
摆在那里是为了让那条判据再也不能退回去。

## 画面校验的姿态前提

Phase C 立起来的那套像素校验有两条隐含前提：

1. 相机看向立方体中心——轮廓关于投影中心对称只在此时成立，那是「质心落在画面中心」
   那条断言的全部依据。
2. 立方体完整落在视口里，且不能小到只剩几十个像素——覆盖率断言的上下界（2%、50%）
   就是按这个前提定的。

第一视角之后第 1 条经常不成立：转头、走动都会让立方体离开画面中央，那不是 bug。
现在前提由 `DebugCube.IsVerifiable` 显式算出来，判据只用相机自己的参数，
具体是两条：视线与「指向原点」的夹角余弦 ≥ 0.9999、外接球在画面里的占比落在 [0.30, 0.85]。

不满足时**打印明说跳过**，不静默放行：

    [PREVIEWER][gl.readback] 跳过画面校验 cameraVersion=8 原因=相机没看向立方体中心 alignment=0.873007（自由转头之后属于预期）

「通过」和「不适用」必须是两句不同的话。有相当一部分姿态本来就验不了，
把两者混成一片安静等于把这条检查整个作废。

于是**剧本得自己摆出能验的姿态**：`RotateAroundCube` 转的是相机位置（绕着立方体转，
始终看向中心），掠射那一步也走 `LookAt(..., Vector3.Zero)`。转头那一步之后画面校验会跳过，
这是设计如此——那一步验的是模型的角度增量与位置不动，不是画面。

## 失活时清输入状态

按住 W 的时候 Alt+Tab：抬起事件送到别的窗口去了，这里永远收不到，相机就一直在走。
指针的进出与捕获丢失同理，挂着的参照点会让光标回来的第一帧跳一下。
两种表现都像鼠标键盘坏了，而不像「有个状态没清」。

清的地方在**窗口**（`OnDeactivated`），不是控件的 `LostFocus`：Win32 下
`WM_KILLFOCUS` 会不会让元素收到 `LostFocus` 由后端决定，而「失活的窗口不持有输入状态」
不该依赖那个细节。

清的入口分在两个对象上，因为它们记的状态在谁手里不同：按键集合在 `WasdCameraController`，
指针手势在 `PreviewerInputAdapter`。顺带记一条同源的：滚轮在 Win32 上发给焦点窗口
而不是光标下的窗口，所以「窗口在后台时滚轮无效」是预期的，与接线无关。

## 验收结果

    dotnet run --project src/LitematicaViewer.Previewer.Sample -- --selftest 6

模型自检（纯计算，早于 GL 上下文）：

    [SAMPLE][camera.zoom] 方向/比例/夹取/Target 不动全通过 start=4.7244 step=0.8 range=[2.4,7]
    [SAMPLE][camera.zoom] 无界：连推 50 档到 6.743E-005（无界时不会停在边界），两头溢出各维持原状一次
    [SAMPLE][camera.look] yaw/pitch 增量、位置不动、俯仰两端夹取、一来一回全通过 distance=4.7244
    [SAMPLE][camera.pan] 方向/水平面/刚体性/Target 派生全通过 distance=4.7244

转视角那一段的完整链路（合成指针移动 -> 适配器 -> Look* -> 控制器 -> 模型 -> SetCamera）：

    [PREVIEWER][event.look] started pos=(380,300) handlers=2
    [SAMPLE][look.start] pos=(380,300) yaw=-37.41 pitch=25.05
    [SAMPLE][look.move] moves=1 delta=(60.0,30.0) total=(15.00,7.50) yaw=-22.41 pitch=32.55
    [PREVIEWER][event.look] ended handlers=2
    [SAMPLE][look.end] moves=2 total=(30.00,15.00) yaw=-7.41 pitch=40.05 distance=3.7795

    yaw 增量 30.00（120 DIP × 0.25）  pitch 增量 15.00
    距离 3.7795 -> 3.7795（参考距离不是转视角该碰的东西）
    相机位置一动不动（第一视角；这是这一相位新加的一条断言）

剧本先把适配器手里可能挂着的手势收掉再开始自己的：指针一动就转意味着真实鼠标的每一次移动
都是一段手势，不先收掉的话脚本第一段移动会被当成「那一段真人手势的延续」，
参照点还在真人那个位置上——而那与「换算写错了」在数值上分不开。
这一段之后到断言为止是同步的（没有消息泵），外面插不进来。

走动那一段（按住 W 0.8 秒）：

    [SAMPLE][camera.wasd] key=W down=True pressed=[W]
    [SAMPLE][camera.wasd] 开始移动 axis=(0.00,1.00) pos=(-2.076814, 1.6, -2.695487)
    [SAMPLE][camera.wasd] key=W down=False pressed=[]
    [SAMPLE][camera.wasd] 移动结束 seconds=0.801 units=1.2012 expected=1.2012
    [SAMPLE][selftest.walk] seconds=0.801 expected=1.2012 actual=1.2012 dot=1.000000
    [SAMPLE][selftest.walk] 松开后 5 帧位移为 0，键状态确实清掉了

    位移方向 dot=1.000000：正是按下那一刻视线在水平面上的投影
    松开后位移为 0：键状态清干净了

画面校验：三次旋转的姿态照旧，新增的掠射姿态是那 34 个像素的回归用例：

    version=3  yaw=-37.41  pitch=25.05  solid=65537   coverage=8.3%
    version=6  yaw=-37.41  pitch=25.05  solid=102693  coverage=13.1%
    version=8  yaw=-7.41   pitch=40.05  跳过（转头之后相机不再看向中心）
    掠射帧     face=0 pixels=33 expected=34 expectedVisible=True   ← 旧判据下这里是 33 > 90，必崩

    [PREVIEWER][gl.error] code=0x0
    [SAMPLE][selftest.summary] ticks=320 scrolled=3 wheelIn=2 wheelOut=1 key=2 keyDown=True keyUp=True
    resizes=2 lastViewport=1184x768 expected=1184x768 cameraSets=3 look=1/2/1 distance=4.7244->3.7795 elapsed=5.89s
    [PREVIEWER][gl.deinit] frames=320 expected=>0 elapsed=6.05s avgFps=52.9

`cameraSets=3` 仍然只有剧本自己摆的那三次（两次旋转 + 一次掠射）：滚轮、转视角、走动三种输入
各自走 `ScrollZoomController` / `MouseLookController` / `WasdCameraController`，都不经过剧本。

连跑四次都是 `look=1/2/1` 且 exit=0。**真实输入也验到了**：跑自检的同时有人在窗口上动鼠标，
日志里会出现第二段手势（`look=2/11/2` 这种计数），相机确实跟着转了——
合成事件覆盖不了命中测试那一段（见 04），所以这条真实输入的证据有它自己的价值。

### Release 下的探针

| 程序集 | 探针 | Debug | Release |
|---|---|---|---|
| Sample.dll | `camera.look` / `camera.pan` / `camera.wasd` / `look.start` / `selftest.walk` | 有 | 无 |
| Previewer.dll | `camera.convention` / `gl.readback` / `跳过画面校验` / `读回预算用尽` | 有 | 无 |
| Sample.dll | `api.probe`（构造签名的临时探针，已删） | 无 | 无 |

前四个来自 `Debug.WriteLine`（`[Conditional("DEBUG")]` 连同插值字符串一起被丢掉）；
`VerifyFrame` 整段（含读回）以及 `IsVerifiable` 的调用点在 `#if DEBUG` 里——
读回那一下 GPU 同步不会因为断言被丢掉而消失，所以连调用点一起包掉。

## 调试桩

    look.start / look.move / look.end   转向手势的起点、节流的过程、以及一程的汇总
    camera.wasd                          按键状态变化、开始移动、一段移动的汇总（走了多久多远）
    camera.wasd 释放全部按键              失活时的清场
    camera.look / camera.pan             VerifyCameraMath 的汇总（单发，不逐次打）
    gl.readback 跳过画面校验              姿态不在校验范围内的原因与数值（每 60 条一条）
    gl.readback 读回预算用尽              预算用尽，之后不再做画面校验（只说一次）

节流的落点（这一相位新增的三处）：

- `camera.set` 每 60 条一条。它一行要格式化五个向量加六个标量，而指针一动就是每秒六十行，
  这是全项目最重的一处 IO。
- 跳过画面校验每 60 条一条，计数在真读回那里清零——于是「上一次校验之后的第一跳」一定打得出来。
- `camera.wasd` 的按键只在**集合真的变了**时才打。平台自动重复一秒几十条 `KeyDown`，
  而它们在集合上幂等，所以这条日志天然只剩真实的状态变化，不必再套一层节流。

动作本身（`Look` / `Pan`）**不打桩**：指针一秒几百条、按住键每帧一次，逐次打会把终端冲掉。
「这一程转了多少、这一段走了多远」这类汇总只有控制器知道该怎么划段，所以由它们打。
`Zoom` 是离散的（一格滚轮一次），仍然自己打。

代码内的不变量断言（Phase F 新增）：

- `Pan` 不改朝向（精确相等）、不改参考距离（相对误差 < 1e-4）。
- `Pan` 的视线水平分量不能退化（`|XZ|² > 1e-6`），否则「正前方」没有定义。
- `Look` 的两条增量必须有限；喂 90 度就转 90 度，`+500` 度顶在 `MaxPitch`、
  再 `-1000` 度顶在 `-MaxPitch`；正反一趟回到原处；**位置一动不动**。
- `Zoom` 推拉前后 `Target` 是同一个世界坐标。
- `CameraModel(…, min, max)` 的 `min > max`。
- 无界缩放的溢出兜底：距离必须精确不变、位置必须仍是有限值。
- 朝向相机的面画得不能比几何期望少太多（0.35 倍，期望 < 8 像素时不断言）。

## 已知取舍

- **动作的方向约定落在模型里**（`Look` 的符号、`Pan` 的左右），控制器只把像素数
  与「哪个键按着」喂进来。手感参数（灵敏度、速度）在控制器里。
- **灵敏度与速度是脚本引用得到的常量**（`DegreesPerDip` / `UnitsPerSecond`）。
  脚本引用它们意味着改了值不会红——那是有意的：脚本钉的是「移多少像素转多少度」、
  「按了多久走多远」这两条换算关系与方向，不是这两个数本身。
- **指针一动就转，不按任何键**。代价是没有「只在想要的时候转」这个开关；
  真要的话加的是一个修饰键判据，不是回退成必须按住。
- **1 世界单位 = 1 个 MC 方块**（渲染器里那个单位立方体就是按这个画的）。
  材质包的分辨率（16x16 / 256x256 / 2048x2048）不影响它：那决定的是一个方块贴多少纹素，
  方块的世界尺寸始终是 1。要按材质包配的是贴图采样（mipmap、过滤），不是速度。
- **平移没有上下**，理由见上。**滚轮也没有被改成「按距离比例」以外的任何东西**。
- `CameraModel.Navigation.cs` 与 `CameraModel.cs` 的分法按**驱动方式**（离散 / 连续），
  不按「读 / 写」或「公开 / 内部」。再往这个类里加东西时按同一条线分。
- 画面校验的姿态前提是**保守**的：占比窗口 [0.30, 0.85] 比覆盖率断言的 2%~50% 窄，
  两头都留了余量。放宽它意味着放宽那两条断言，那是另一件事。
- 逐面期望的容差是 0.35 倍，实测偏差在 2%~5%（共边像素归属让实测略高于期望）。
  这个余量是为「几何期望按面心一个点算」留的，不是为了放行数量级级别的错。

## 未解决

- **手感仍然没有目视确认过**。第一版的手感问题是「拿给人看」才发现的，
  所以这一条不是形式：转视角的方向与灵敏度（0.25 度/DIP）、走动速度（1.5 方块/秒）、
  以及**指针一动就转在真机上会不会太灵敏**，都得在窗口上试。
- **指针没有锁**。窗口比屏幕小的时候，一次扫动最多转「一屏宽 × 0.25 度」；
  指针撞到屏幕边缘之后继续移动不再产生位移，转动就停在那儿。
  真正的无限转动要的是 pointer lock（平台能力），不是这一层能补的。
- 走动没有碰撞、没有高度约束，可以走到立方体内部。看真实建筑时会需要「别穿进实体」
  或至少一个「按 R 回到原点」。
- 触控板的惯性、触屏的双指手势都没有接。`LookMoved` 是纯位置，接得进来。
- 多个指针（多指、多鼠标）没有区分：适配器只记「当前这一段的指针」。
  第二根手指按下会被当成同一段手势的移动。
