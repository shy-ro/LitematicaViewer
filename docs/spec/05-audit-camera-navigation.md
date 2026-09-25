# 05 相机导航审计（Phase F）

Phase F：相机移动（W/A/S/D）、鼠标拖拽转视角，以及把缩放的距离上下界改成可选。
Phase E 里相机只能沿一条视线推拉，这一相位起它可以走、可以转。

不碰 Core，不碰 Previewer 的 GL 路径。动的三处：`CameraModel`（加两个动作）、
`Previewer` 的输入公开面（加拖拽手势）、以及 Debug 侧的画面校验（加姿态前提）。

## 文件与职责

    Controllers/CameraModel.cs             相机状态的权威 + 缩放 + 边界（可选）+ DEBUG 自检
    Controllers/CameraModel.Navigation.cs  绕 Target 转视角、沿自己的视角平移
    Controllers/MouseLookController.cs     拖拽 -> 绕转 -> SetCamera
    Controllers/WasdCameraController.cs    按键状态 -> 按时间积分的平移 -> SetCamera
    Controllers/ScrollZoomController.cs    滚轮 -> 缩放（Phase E，未改）
    Previewer.Events.cs                    新增 DragStarted / DragMoved / DragEnded
    PreviewerInputAdapter.cs               指针事件 -> 拖拽手势（左键起拖、指针捕获、丢抬起兜底）
    MainWindow.axaml.cs                    三个控制器的装配与生命周期；失活时清输入状态
    Debug/DebugCube.cs                     IsVerifiable：像素校验的姿态前提
    Previewer.cs                           VerifyFrame 先问前提；读回预算只算真读的那些

模型拆成两个 partial 文件，理由是驱动方式不同：缩放一格一格地来（滚轮），
绕转与平移被连续输入推着走（拖拽一秒几百条、按住键每帧一次）。
打桩与节流的落点因此不同——`Zoom` 自己打，另两个由控制器按段落打。

## 公开面

`CameraModel`（`internal sealed partial class`）

    CameraModel(CameraState initial, Vector3 target, float? minDistance = null, float? maxDistance = null)
    CameraState Camera { get; }
    Vector3 Target { get; }
    float Distance { get; }
    void Reset(CameraState camera)
    void Zoom(float steps)
    void Orbit(float deltaYaw, float deltaPitch)        // 度
    void Pan(float forwardUnits, float rightUnits)      // 世界单位

    ZoomRatioPerStep = 0.8f
    static void VerifyCameraMath()                      // #if DEBUG（Phase E 时叫 VerifyZoom）

`MouseLookController` / `WasdCameraController`（`internal sealed class`，`IDisposable`）

    MouseLookController(Previewer previewer, CameraModel camera)
    CancelDrag()

    WasdCameraController(Previewer previewer, CameraModel camera)
    ReleaseKeys()

    DegreesPerDip = 0.25f     // 拖过一个 DIP 转多少度
    UnitsPerSecond = 3f       // 按住一秒走多少世界单位

`Previewer` 新增的三个事件（位置是控件坐标，DIP）：

    event Action<Point>? DragStarted
    event Action<Point>? DragMoved
    event Action? DragEnded

## 为什么是 orbit，而不是自由飞行

三个动作都保持同一条不变式：**相机始终看向 Target**。绕转以它为轴心，平移把它一起拖走。

这条不变式不是美学选择，它撑着两处已有的判断：

- 画面校验里「轮廓质心落在画面中心」。自由飞行相机一旦俯仰与朝向解耦，
  这条立刻失效，剩下能验的就只有「画了东西」——而那连投影矩阵转置都发现不了。
- 滚轮夹取与绕转半径读的都是 `Distance = |Position - Target|`。平移若不是刚体的，
  这个数会在走动时慢慢漂，而症状是「走两步之后镜头自己往前凑」。

代价是**没有上下移动**。真实建筑的剖面上要飞起来看时，缺的是「沿世界 Y 平移」，
到时候加的是 `Pan` 的一个纵向分量，不是把模型改成自由飞行。

## 绕转：三个坑

**一、弧度与度。** 仰角由 `atan2(offset.Y, |offset.XZ|)` 得到，那是弧度；
而增量、`CameraState.MaxPitch`、`CameraState.Pitch` 全是度。第一版直接把
`elevation + deltaPitch` 交给 `DegreesToRadians`，于是「拖 90 度」实际转了 1.57 度。

这个错不会编译失败、不会抛异常，画面也照常出，只是转得「不太对」。
模型自检第一条就抓住了它：

    [SAMPLE][camera] 只转 yaw 时的 pitch 不对 actual=0.437122 expected=25.045238 tolerance=0.05

**二、水平方向取自 yaw，不取自 offset。** 俯角顶到 ±90 度时 `offset` 的水平分量是 0，
方向无从谈起。那不是病态输入——滚轮一路推近、鼠标一路往上拖都会到那儿。
`yaw` 在任何姿态下都有定义，所以由它给出水平方位，只从 `offset` 取仰角。

**三、符号。** 约定里 `yaw` 增大是往右转，而相机本身要往左绕（视线往右摆，眼就得往左站）。
所以水平方向是 `-ForwardOf(yaw + deltaYaw, 0)`。符号漏掉的表现是整个拖拽方向反过来，
而正着拖、反着拖各自看起来都「像那么回事」。

新位置走 `CameraState.LookAt` 而不是自己拼 yaw/pitch：夹取与取值域只有那一处实现过，
再写一份就是把「两套三角函数慢慢错开」请回来。顺带一个想要的副作用：`yaw` 每次都被
重新归到 `(-180,180]`，不随拖拽无界累积——累积到几万度之后 `sin/cos` 的精度会掉。

## 平移：四条

**Target 跟着走。** 只挪相机的话，平移之后相机看向的是空处，下一次绕转、
推拉都以那个空处为轴——表现是平移完再转视角，画面整个甩出去，而单看平移那一步完全正常。

**只取水平分量。** 低头看地时 W 还应该往前走，而不是往地里钻。
`pitch` 被夹在 ±89.9，水平分量至少 `cos(89.9°) ≈ 1.7e-3`，归一化不会退化。

**`right = forward × up`，不是 `up × forward`。** 后者得到的是左边，症状是 A/D 反向——
从画面上看只是「手感奇怪」，不报错，所以只能靠算：模型自检里有一条断言
`dot(位移, right)` 必须是 `+1`。

**斜向归一化。** W 与 D 同时按下时轴向量长度是 √2，不处理的话斜着走快 41%。
只在两个键同按时出现，玩的时候注意不到，量的时候一眼就看见。

## 缩放边界改成可选

Phase E 里 `MinDistance = 2.4` / `MaxDistance = 7.0` 是写死的常量，取值依据是
「单位立方体外接球恰好塞进画面」。Phase F 起这个前提没了：能走能转的相机没有
唯一的被看对象，而写死的界会在模型里替人做决定——把相机卡在离 Target 2.4 的地方，
于是「凑近看一块砖」成了做不到的操作，那表现成「滚轮没反应」。

现在它们是构造函数的两个可选入参，**默认两个都不给**。只设一头是合法的
（比如「别推近到穿模」但不管多远）。`min > max` 有断言：夹取会退化成一个恒定值，
表现同样是「滚轮不动」，而那时离出错的地方已经很远。

`Sample` 侧一个界都不设。要设的位置就在 `MainWindow` 里那一行构造：

    _camera = new CameraModel(CameraState.Default, Vector3.Zero, minDistance: 2.4f, maxDistance: 7.0f);

无界之后连推上千档会把距离推出浮点范围（`0.8^-6000` 溢出成 `Inf`），位置跟着变成
`Inf`，视图矩阵整块失效而 GL 一声不吭。这不是尺度限制从后门回来，是数值兜底：
算不出一个能画的相机就原地不动，并把这件事打出来。自检两头各推一次
（+6000 / -6000 档），断言距离**精确不变**且位置仍是有限值。

## 画面校验的姿态前提

Phase C 立起来的那套像素校验有两条隐含前提：

1. 相机看向立方体中心——轮廓关于投影中心对称只在此时成立，那是「质心落在画面中心」
   那条断言的全部依据。
2. 立方体完整落在视口里，且不能小到只剩几十个像素——覆盖率断言的上下界（2%、50%）
   就是按这个前提定的。

Phase F 之前这两条自动成立（相机只能在一条视线上推拉，而距离被夹在 2.4~7.0 之间）。
能平移之后不再成立：平移把 Target 拖走，立方体本来就会离开画面中心。

现在前提由 `DebugCube.IsVerifiable` 显式算出来，判据只用相机自己的参数
（位置、朝向、fov、宽高比），**不碰投影矩阵**——投影矩阵漏了转置正是质心那条断言要抓的东西，
把它算进前提里等于把那条检查自己关掉。具体是三条：视线与「指向原点」的夹角余弦 ≥ 0.9999、
外接球在画面里的占比落在 [0.30, 0.85]。

不满足时**打印明说跳过**，不静默放行：

    [PREVIEWER][gl.readback] 跳过画面校验 cameraVersion=43 原因=相机没看向立方体中心 alignment=0.857674（平移之后属于预期）

「通过」和「不适用」必须是两句不同的话。Phase F 起有相当一部分姿态本来就验不了，
把两者混成一片安静等于把这条检查整个作废。

读回预算（`MaxCameraVerifications`）改成只算**真读了**的那些：跳过不产生 GPU 同步，
却会把名额花掉，于是真正换相机的帧反倒没验。同时 6 改成 24——相机现在按住 W 时
每帧都在变，6 次在第一秒就用光了。

## 失活时清输入状态

按住 W 的时候 Alt+Tab：抬起事件送到别的窗口去了，这里永远收不到，相机就一直在走。
拖到一半时失活同理，画面跟着光标乱转。两种表现都像鼠标键盘坏了，而不像「有个状态没清」。

清的地方在**窗口**（`OnDeactivated`），不是控件的 `LostFocus`：Win32 下
`WM_KILLFOCUS` 会不会让元素收到 `LostFocus` 由后端决定，而「失活的窗口不持有输入状态」
不该依赖那个细节。顺带记一条同源的：滚轮在 Win32 上发给焦点窗口而不是光标下的窗口，
所以「窗口在后台时滚轮无效」是预期的，与接线无关。

## 验收结果

    dotnet run --project src/LitematicaViewer.Previewer.Sample -- --selftest 6

模型自检（纯计算，早于 GL 上下文）：

    [SAMPLE][camera.zoom] 方向/比例/夹取全通过 start=4.7244 step=0.8 range=[2.4,7]
    [SAMPLE][camera.zoom] 无界：连推 50 档到 6.743E-005（无界时不会停在边界），两头溢出各维持原状一次
    [SAMPLE][camera.orbit] yaw/pitch 增量、俯仰两端夹取、一来一回全通过 distance=4.7244
    [SAMPLE][camera.pan] 方向/水平面/刚体性/Target 跟随全通过 distance=4.7244

拖拽那一段的完整链路（合成指针事件 -> 适配器 -> Drag* -> 控制器 -> 模型 -> SetCamera）：

    [PREVIEWER][event.drag] started pos=(380,300) handlers=2
    [SAMPLE][look.start] pos=(380,300) yaw=-37.41 pitch=25.05
    [SAMPLE][look.move] moves=1 delta=(60.0,30.0) total=(15.00,7.50) yaw=-22.41 pitch=32.55
    [PREVIEWER][event.drag] ended handlers=2
    [SAMPLE][look.end] moves=2 total=(30.00,15.00) yaw=-7.41 pitch=40.05 distance=3.7795

    拖拽的 yaw 增量 30.00（120 DIP × 0.25）  pitch 增量 15.00
    距离 3.7795 -> 3.7795（绕转是刚体的，只改方向）

走动那一段（按住 W 0.8 秒）：

    [SAMPLE][camera.wasd] key=W down=True pressed=[W]
    [SAMPLE][camera.wasd] 开始移动 axis=(0.00,1.00) pos=(-0.36664316, 2.4317157, -2.8209295)
    [SAMPLE][camera.wasd] key=W down=False pressed=[]
    [SAMPLE][camera.wasd] 移动结束 seconds=0.802 units=2.4052 expected=2.4052
    [SAMPLE][selftest.walk] seconds=0.799 expected=2.3962 actual=2.3962 dot=1.000000
    [SAMPLE][selftest.walk] 松开后 5 帧位移为 0，键状态确实清掉了

    位移方向 dot=1.000000：正是按下那一刻视线在水平面上的投影
    松开后位移为 0：键状态清干净了

画面校验：四帧照旧（Phase C/D 的像素数没变），拖拽后的那一帧也是可验姿态，
平移之后转为跳过：

    version=3  yaw=-37.41  pitch=25.05  solid=65537   coverage=8.3%
    version=6  yaw=-37.41  pitch=25.05  solid=102693  coverage=13.1%
    version=8  yaw=-7.41   pitch=40.05  solid=93934   coverage=11.9%  visibleFaces=2
    version=43..63 跳过（相机没看向立方体中心，alignment 0.78~0.98）

    [PREVIEWER][gl.error] code=0x0
    [SAMPLE][selftest.summary] ticks=318 scrolled=3 wheelIn=2 wheelOut=1 key=2 keyDown=True keyUp=True
    resizes=2 lastViewport=1184x768 expected=1184x768 cameraSets=2 drag=1/2/1 distance=4.7244->3.7795 elapsed=5.90s
    [PREVIEWER][gl.deinit] frames=318 expected=>0 elapsed=6.07s avgFps=52.4

`cameraSets=2` 仍然只有剧本自己摆的那两次旋转：滚轮、拖拽、走动三种输入各自走
`ScrollZoomController` / `MouseLookController` / `WasdCameraController`，都不经过剧本。

**真实输入在这个相位里被验到了**：跑自检的同时有人在窗口上真的拖了鼠标，
日志里出现了一条 `[SAMPLE][look.end] moves=10 total=(-20.00,-17.75)`——那不是剧本合成的
（剧本那一次是 `moves=2`）。合成事件覆盖不了命中测试那一段（见 04），
所以这条真实输入的证据有它自己的价值。

### Release 下的探针

| 程序集 | 探针 | Debug | Release |
|---|---|---|---|
| Sample.dll | `camera.orbit` / `camera.pan` / `camera.wasd` / `look.start` / `selftest.walk` | 有 | 无 |
| Previewer.dll | `camera.convention` / `gl.readback` / `跳过画面校验` | 有 | 无 |
| Sample.dll | `api.probe`（构造签名的临时探针，已删） | 无 | 无 |

前四个来自 `Debug.WriteLine`（`[Conditional("DEBUG")]` 连同插值字符串一起被丢掉）；
`IsVerifiable` 与那段跳过日志整段在 `#if DEBUG` 里。

## 调试桩

    look.start / look.move / look.end   拖拽的起点、节流的过程、以及一次拖拽的汇总
    look.cancel                         拖拽被作废（窗口失活），并说明抬起事件不会来
    camera.wasd                         按键状态、开始移动、一段移动的汇总（走了多久多远）
    camera.wasd 释放全部按键             失活时的清场
    camera.orbit / camera.pan           VerifyCameraMath 的汇总（单发，不逐次打）
    gl.readback 跳过画面校验             姿态不在校验范围内的原因与数值

动作本身（`Orbit` / `Pan`）**不打桩**：拖拽一秒几百条、按住键每帧一次，逐次打会把终端冲掉。
「这一拖转了多少、这一段走了多远」这类汇总只有控制器知道该怎么划段，所以由它们打。
`Zoom` 是离散的（一格滚轮一次），仍然自己打。

代码内的不变量断言（Phase F 新增）：

- `Pan` 是刚体的：平移前后 `Distance` 不变的相对误差 < 1e-4。位置挪了而 Target 没挪会在这里红。
- `Pan` 的视线水平分量不能退化（`|XZ|² > 1e-6`），否则「正前方」没有定义。
- `Orbit` 的两条增量必须有限；相机压在 Target 上时绕转没有定义。
- `Orbit` 喂 90 度就转 90 度，`+500` 度顶在 `MaxPitch`、再 `-1000` 度顶在 `-MaxPitch`，
  且正反一趟回到原处（位置误差 < 1e-4）。
- `CameraModel(…, min, max)` 的 `min > max`。
- 无界缩放的溢出兜底：距离必须精确不变、位置必须仍是有限值。

## 已知取舍

- **动作的方向约定落在模型里**（`Orbit` 的符号、`Pan` 的左右），控制器只把像素数
  与「哪个键按着」喂进来。手感参数（灵敏度、速度）在控制器里。
- **灵敏度与速度是脚本引用得到的常量**（`DegreesPerDip` / `UnitsPerSecond`）。
  脚本引用它们意味着改了值不会红——那是有意的：脚本钉的是「拖多少像素转多少度」、
  「按了多久走多远」这两条换算关系与方向，不是这两个数本身。
- **平移没有上下**，理由见上。**滚轮也没有被改成「按距离比例」以外的任何东西**。
- `CameraModel.Navigation.cs` 与 `CameraModel.cs` 的分法按**驱动方式**（离散 / 连续），
  不按「读 / 写」或「公开 / 内部」。再往这个类里加东西时按同一条线分。
- 画面校验的姿态前提是**保守**的：占比窗口 [0.30, 0.85] 比覆盖率断言的 2%~50% 窄，
  两头都留了余量。放宽它意味着放宽那两条断言，那是另一件事。

## 未解决

- **没有目视确认**。整个 Phase F 的结论仍然全部来自数值与像素统计。
  拖一下、按几下 W/A/S/D，看方向、手感、以及「上下有没有反」——你看一眼就补上了。
  特别是：**灵敏度 0.25 度/DIP 与速度 3 单位/秒是拍的**，只看数值定不出手感。
- 拖拽的方向约定（往右拖 = 往右转）只验了符号与增量，没在真机上确认过手感；
  另一套约定（抓住模型转）只需要把 `MouseLookController` 里两个 `delta` 取反。
- 走动没有碰撞、没有高度约束，可以走到立方体内部。看真实建筑时会需要「别穿进实体」
  或至少一个「按 R 回到原点」。
- 触控板的惯性、触屏的双指手势都没有接。`DragMoved` 是纯位置，接得进来。
- 多个指针（多指、多鼠标）没有区分：`PreviewerInputAdapter` 只认「有没有左键按着」。
