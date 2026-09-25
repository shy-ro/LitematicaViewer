# 03 相机控制审计（Phase E）

Phase E：滚轮缩放。相机第一次由控制器驱动，`Sample/Controllers/*` 落地。

这一相位不碰 Core，也不碰 Previewer 的 GL 路径——它接的是 Phase D 立起来的那四个事件。

**Phase F 改动过本篇的四处**，改动与理由见 05：

- 距离上下界从编译期常量变成可选入参，默认不设（下面「公开面」与「距离的上下界」两节按当时的写法保留）。
- `VerifyZoom` 改名为 `VerifyCameraMath`：它现在把绕转与平移一起验。
- `CameraModel` 拆成两个 partial 文件，加了 `Look` / `Pan`（当时叫 `Orbit`——那是第一版
  的 orbit 相机，后来整个改成了第一视角，见 05）。
- 转视角**已经做**，而且是**指针一动就转**、不需要按任何键（本篇「未解决」里说「不做」的那条已过时）。
- 走动速度是 5 方块/秒、鼠标灵敏度 0.25 度/DIP；两者都是手感参数，在控制器里。
  （Phase F 第三次返工之后灵敏度是 0.10，而且两个数都搬到了 `Previewer` 的**控件属性**上——
  侧边栏能直接拖，见 05「控件属性与侧边栏」。）

## 文件与职责

    Controllers/CameraModel.cs           相机状态的权威持有者 + 缩放 + 距离夹取
    Controllers/ScrollZoomController.cs  滚轮 -> 模型 -> SetCamera
    InputSelfTest.cs                     从「自己改相机」退到「旁观控制器改相机」

`WasdCameraController.cs` 这一相位没有创建（属 Phase F，见 05）。

**期间发现并修掉了一个输入故障**：真实鼠标滚轮在窗口上完全没有反应。根因不在这一相位的
代码里——控件的画面由 GL 直接画到窗口上，Avalonia 12 的合成层命中测试看不到它，
指针事件全部落在它下面那层容器上。修复（`ICustomHitTest`）与完整排查过程见 04。
这一篇里「滚轮走通了」的结论全部来自合成事件，而合成事件绕过命中测试，
所以那些结论本身都是对的，只是覆盖不到那一段。

`CameraModel` 在 Sample 而不是 Previewer：它要读 `CameraState`、要调 `SetCamera`，
而 Previewer 的定位是「只画不记」。把模型放进去，等于承认 Previewer 也该知道
「相机现在在哪」——那正是 Phase D 特意用「没有 GetCamera」关掉的门。

## 公开面

`CameraModel`（`internal sealed class`，Sample 内部可见）

    CameraModel(CameraState initial, Vector3 target)
    CameraState Camera { get; }        // 权威，只读
    Vector3 Target { get; }
    float Distance { get; }            // |Position - Target|，每次都算
    void Reset(CameraState camera)
    void Zoom(float steps)

    MinDistance = 2.4f                 // Phase F 起是构造函数的可选入参，默认不给
    MaxDistance = 7.0f
    ZoomRatioPerStep = 0.8f

    static void VerifyZoom()           // #if DEBUG（Phase F 改名为 VerifyCameraMath）

`ScrollZoomController`（`internal sealed class`，`IDisposable`）

    ScrollZoomController(Previewer previewer, CameraModel camera)
    Dispose()

## 相机权威为什么是「一个 CameraState」而不是「orbit 三元组」

orbit 相机通常存 `(target, distance, yaw, pitch)` 四个数。这里只存 `(target, CameraState)`。

理由在验收剧本上：剧本要摆几个不同的视角去看画面变化，走的是 `Reset(CameraState)`。
如果模型存的是 orbit 三元组，`Reset` 就得把 `eye/target` 反解成 `(yaw, pitch, distance)`
再转回来——那几次三角函数会带回两个问题：

- `yaw` 在 `atan2` 的 ±180 处跳变（`CameraState.LookAt` 的注释里已经记了这条）。
- 位置漂移 1e-5 量级。而画面校验正好在数像素，Phase C 记录的那三个像素数
  （20864 / 14317 / 30355）会在某次无关的改动之后对不上。

存 `CameraState` 就没有这一趟往返：`Reset` 是赋值，`Zoom` 只改 `Position` 一个分量。

代价是模型里没有 `Yaw` / `Pitch` 的独立字段。这一条在 Phase F 有了答案：
`Pan` 要挪 `Target`，而它挪的方式是**从当前 `Camera.Forward` 现取方向**，
不是读一份存下来的朝向——存两份正是上面那个分叉的来源。见 05。

## 缩放只动一个自由度

朝向取自**当前相机自己**：

    direction = (Camera.Position - target) / Distance
    Camera    = Camera with { Position = target + direction * next }

模型不另存一份 `yaw` / `pitch` 来算这个方向。存两份的话，
「剧本摆了一个别的视角」与「模型以为的朝向」就会分叉，症状是滚轮一滚相机自己转回某个旧方向——
而两次操作单看都各自正确，从代码里读不出来。这条有断言守着：`VerifyZoom` 第 4 步要求
缩放前后 `Camera.Forward` **精确相等**（不是容差相等）。给容差会把「谁顺手归一化了一下 yaw」
放过去，而那正是一条朝向会慢慢漂移的路。

`Position * ratio` 那种写法只在 `target` 恰好是原点时才对。上面的公式对任何 target 都成立，
而「target 是不是原点」不该由缩放来假设。

## 距离的上下界

    MinDistance = 2.4    外接球半径 sqrt(3)/2 = 0.866，垂直半视角 22.5°
                         → 0.866 / tan(22.5°) = 2.09，再近就该被近裁剪面切到了
    MaxDistance = 7.0    轮廓覆盖率约 3%（默认距离 4.7244 时是 8.3%），再远画面里只剩几个像素

（Phase F 起这两个数不再是模型的默认值，而是构造时可选的入参——上面这一组是
「为画面里那一个单位立方体量身定」的取值，现在由宿主决定要不要。见 05。）

夹取放在**模型**里而不是等画面校验去发现：把相机推到看不见东西的位置本就不该是一次合法操作。
`VerifyZoom` 连滚 50 档（0.8^50 ≈ 1.4e-5，早就把 `Pow` 推进溢出区了）之后断言距离
**就是** 2.4 / 7.0（容差 1e-3），而不是「大致接近」。这条验的是「夹取存在且到位」，
所以自检里显式把界传给构造函数；默认无界那一条另有一条断言守着。

`Pow` 溢出到 `Inf` 或下溢到 `0` 在有界的模型里是良性的：两者被夹到上下界恰好就是想要的语义。
无界之后它们会真的把相机推出去，所以那里加了一条数值兜底（距离算不出有限正数就不动），
理由见 05。

## 比例而不是步长

一档滚轮乘 0.8，而不是减一个固定距离。距离是尺度量：固定步长在远处太慢、在近处一跳就穿。
用比例意味着「来回滚一趟回到原处」是数学上的恒等，`VerifyZoom` 第 2 步验的就是这条——
两个方向的比例不是互为倒数时，手感会变成「滚出去再滚回来，画面没回到原样」。

Avalonia 的 `delta` 是浮点（触控板能给出小数档位），所以用的是 `Pow(ratio, steps)` 而不是
「`steps > 0` 乘一次、`steps < 0` 除一次」。

## 控制器为什么直接 SetCamera，而不是让模型发变更事件

两条路都成立。选直接调是因为少一层订阅-退订的簿记，而事件那一层的失败模式（订阅了但没退订、
退订了但还持有引用）在这里换不来任何好处——控制器本来就持有 `Previewer` 和模型的引用。

代价是 Phase F 会在这里之外多出 `SetCamera` 调用点。已经发生了：`MouseLookController`
与 `WasdCameraController` 各有一处（见 05）。而三处都在 UI 线程上、都在改完模型之后立刻推，
顺序上没有分歧：谁后改谁的结果就是最终画面。

## 验收结果

    dotnet run --project src/LitematicaViewer.Previewer.Sample -- --selftest 6

模型自检（上下文初始化之前就跑完，跟 GL 无关）：

    [SAMPLE][camera.zoom] 方向/比例/夹取全通过 start=4.7244 step=0.8 range=[2.4,7]

滚轮那一步的完整链路。`handlers=2` 是控制器与剧本两个订阅者，
打 0 就是「事件触发了但没人在听」，而这个相位的验收恰好是「有人在听并且改了相机」：

    [PREVIEWER][input.wheel] delta=(0,1)
    [PREVIEWER][event.scrolled] delta=1 handlers=2
    [SAMPLE][camera.zoom] steps=1 distance=4.7244->3.7795 clamped=False range=[2.4,7]
    [PREVIEWER][camera.set] version=4 pos=(-2.08, 1.6, -2.72) yaw=-37.41 pitch=25.05

四帧画面校验，仍按 Phase D 的「相机一变就验一帧」：

    version=1  pos=(2.6, 2, 3.4)        yaw=142.59  solid=65537  coverage=8.3%
    version=2  pos=(3.4, 2, -2.6)       yaw=52.59   solid=65537  coverage=8.3%
    version=3  pos=(-2.6, 2, -3.4)      yaw=-37.41  solid=65537  coverage=8.3%
    version=6  pos=(-2.08, 1.6, -2.72)  yaw=-37.41  solid=102693 coverage=13.1%

    [PREVIEWER][gl.error] code=0x0

    [SAMPLE][selftest.summary] ticks=326 scrolled=3 wheelIn=2 wheelOut=1 key=2 keyDown=True keyUp=True
    resizes=2 lastViewport=1184x768 expected=1184x768 cameraSets=2 distance=4.7244->3.7795 elapsed=5.88s
    [PREVIEWER][gl.deinit] frames=326 elapsed=6.04s avgFps=53.9

三处值得记下来的：

- **`solid` 从 65537 涨到 102693，比例 1.567 对得上 1/0.64 = 1.5625**，偏差 0.29%。
  Phase D 的记录里这一条是「相机确实移动了」的证据（等比缩放是正交投影才有的），
  这里它多了一层含义：距离确实是从模型出来、经由控制器推给渲染器的那个数。
- **`cameraSets=2`**。剧本自己摆视角只剩两次旋转；滚动缩放不经过它的 `ApplyCamera`。
  这个 2 是有意的——它曾经是 5（Phase D 时滚轮也走剧本），现在滚轮走控制器，
  数字不变就说明剧本没在旁边偷偷也改一次。
- **四帧的 `yaw` 与 Phase D 完全一致**（142.59 / 52.59 / -37.41），像素数与质心也一致。
  `Reset` 不经过任何反解，所以 Phase C/D 记录的那三组像素数继续有效，不必重测。

### Release 下的探针

判据仍是 Phase B 立的那条——不是「不输出」，而是字符串都不在程序集里：

| 程序集 | 探针 | Debug | Release |
|---|---|---|---|
| Sample.dll | `camera.zoom` | 有 | 无 |
| Sample.dll | `selftest.summary` | 有 | 无 |

`Zoom` 里的 `Debug.WriteLine` 自带 `[Conditional("DEBUG")]`；`VerifyZoom`（现名
`VerifyCameraMath`）整段连同 `MainWindow` 里的调用点一起包在 `#if DEBUG` 里——那段有
100 次循环的计算，不包的话 Release 也要白跑一遍。

`ZoomCore`（真正的计算）**不**在 `#if DEBUG` 里：`Zoom` 无条件调它，
把它包掉 Release 就编译不过了。

## 调试桩

    camera.zoom   每次 Zoom：档位、距离的前后值、是否被夹住、夹取区间
    camera.zoom   VerifyZoom 的六条断言汇总（单发一条，不逐次打）
    selftest.scroll  剧本旁观到的每一次滚轮与当时的距离

`camera.zoom` 只在 `Zoom` 里打，`VerifyZoom` 走 `ZoomCore`：那里要连滚 100 档，
逐档打桩会把终端冲掉，而「第五十档被夹住了」这种信息本来也没有用。

代码内的不变量断言（Phase E 新增）：

- `steps` 必须有限。`NaN` 会原样穿过 `Clamp` 一路传到 GPU，而 GL 不报错。
- 缩放前的距离必须是**正数**。`target` 压在相机上时视图矩阵已经退化，
  而这个状态在构造时就该被排除——这里只是别让 0 除进来变成 `NaN`。
- 一档的比例、一来一回的闭合、连滚 50 档的夹取、缩放不改朝向、夹取后位置仍是有限值、
  缩放不把相机挪出视线方向。六条都是纯计算，不经过 GL，也不经过窗口。

## 验收剧本为什么退到旁观者

Phase D 的剧本里，`OnScrolled` 自己改相机。Phase E 把它改成「只计数、只报事实」，
相机交给 `ScrollZoomController`。

让剧本继续自己改的话，控制器整个删掉剧本照样全绿——而那恰恰是这一相位要验的东西。
退到旁观者之后，`Report` 里那条「三次滚轮之后的距离」就成了唯一的口子：
控制器没接上、订阅漏了、方向接反了、比例写错了，四种错法各有一次红。

期望值按「净一档」算（`before * ZoomRatioPerStep`），而不是把每一步的距离记下来逐个比：
逐个比等于把控制器的实现抄一遍，抄错了也照样通过。

顺序不承重：控制器与剧本都订阅 `Scrolled`，谁先谁后由订阅顺序决定，
所以 `selftest.scroll` 只报事实，结论留到 `Report` 里出。

## 已知取舍

- 方向的定义在**模型**里（正 `steps` 减小距离），控制器只转发原始增量。
  要反过来改的是模型里那个比例，不是控制器。
- 缩放一步到位，没有惯性、没有缓动。`Tick` 已经在发 dt 了，加平滑的钩子现成，
  但那是手感问题，等有人真的滚过再说。
- `MinDistance` / `MaxDistance` 是编译期常量，不是配置。这一条在 Phase F 落了地：
  它们变成了构造函数上的可选入参，默认不设（见 05）。当时的判断是「场景换成真实建筑之后
  这两个数必然要跟着目标尺寸走」，而实际发生得更早——「目标尺寸」不再是唯一的原因，
  「能走能转的相机没有唯一的被看对象」才是。
- `ScrollZoomController` 只有十来行。它会变厚的地方是灵敏度档位、平滑、反向选项——
  那些都是手感，不是现在。
- `Reset` 目前只有验收剧本在用。它不是一个「为测试而开的口子」：
  它的语义是「把视角摆到某处」，将来的重置视角按钮和场景切换都会走它。
  写在这里是为了别让调用方去调 `Previewer.SetCamera` 绕过模型。

## 未解决

- **没有目视确认**。整个 Phase E 的结论仍然全部来自像素统计与距离数值。
  滚一下滚轮，看看是不是「往上滚画面变近」——这件事你看一眼就补上了。
- 滚轮的**方向**只验了「正 delta 让距离变小」，没验过真实鼠标上滚到底给的是正还是负。
  合成事件里 `delta=+1` 是剧本自己写的。真机上滚一下就能确认。
- 触控板的小数档位没有实测。`Pow(ratio, steps)` 支持它，但没在设备上跑过。
- 转视角这一相位不做。**Phase F 已经做了**（`MouseLookController` + `LookStarted/LookMoved/LookEnded`，
  指针移动即转、不用按键，见 05），
  所以 `CameraModel` 现在有改朝向的入口。屏幕射线拾取缺的只剩「屏幕点 → 世界射线」。
- 距离夹取的上界 7.0 是按单位立方体定的，只在「宿主显式设了界」时才生效——
  Phase F 起 `Sample` 一个界都不设（见 05）。
