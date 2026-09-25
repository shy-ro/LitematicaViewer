# 04 指针输入路由（Phase E 期间的修复）

Phase E 做完后，真实鼠标滚轮在窗口上没有任何反应。探针显示的不是「事件没接上」，
而是更前面的一段：**消息进了 Avalonia，但命中的不是这个控件**。

这一篇记的是那条链路怎么分层的、根因在哪儿、以及为什么自检剧本一直是绿的。

## 症状

- 滚轮没反应，画面不动。
- 键盘也没反应（同一段路）。
- 日志里一切正常：窗口激活、控件 `Focusable`、`Focus()` 返回 true、
  焦点管理器记着的就是 `Previewer`、事件订阅一个不少。
- **探针一路安静**——`input.wheel`、`event.scrolled`、`camera.zoom` 一条都不打。

「安静」和「消息根本没进程序」长得一模一样，这是这一段最花时间的地方。

## 分层

指针事件从操作系统到相机一共经过五段，任何一段断掉的表现都是「没反应」：

    Windows 消息
      ↓  ① 消息进了 Avalonia 没有
    窗口（隧道）
      ↓  ② 命中测试落在哪个元素上
    控件收到指针事件
      ↓  ③ 输入适配器订阅、映射
    Previewer.Scrolled / KeyChanged
      ↓  ④ 控制器订阅
    CameraModel
      ↓  ⑤ SetCamera 驱动渲染
    画面变了

探针按这五段摆：

    [SAMPLE][input.probe.window] tunnel ...   ① 与 ②：窗口隧道，命中之前
    [PREVIEWER][input.probe] ...              控件收到了什么
    [PREVIEWER][input.wheel] / [event.*]      ③
    [SAMPLE][camera.zoom] / [camera.set]      ④ 与 ⑤
    [SAMPLE][input.hittest] inputHitTest=...  ② 的主动询问

窗口那两层必须**显式按 `RoutingStrategies.Tunnel` 订阅**：`+=` 的默认策略是
`Direct | Bubble`，而指针事件是「先隧道下来、再冒泡上去」，命中测试发生在两者之间。
隧道那一次在命中之前到达窗口，所以「隧道有、控件没有」把范围缩到命中测试；
「隧道也没有」说明消息根本没进 Avalonia。用 `+=` 会把这两种情况混成同一种安静。

`[SAMPLE][input.hittest]` 则是主动问：`InputHitTest` 给出输入系统认定的目标，
`GetVisualsAt` 给出合成层认定的目标，两个都打出来才能分辨「合成器看不到控件」
和「控件被别的层挡住」。

## 根因

修复前的命中测试结果：

    point=(512,384) inputHitTest=Panel < Panel < MainWindow < TopLevelHost
    point=(512,384) hit[0] Panel < Panel < MainWindow < TopLevelHost
    point=(10,10)   inputHitTest=Panel < Panel < MainWindow < TopLevelHost
    point=(1014,758) inputHitTest=Panel < Panel < MainWindow < TopLevelHost

**三个点全部落在同一个匿名 `Panel` 上，控件自己不在列表里。** 而它的状态是好的：

    previewer bounds=0, 0, 1024, 768 visible=True effectiveVisible=True
              hitTestVisible=True
              parent=ContentPresenter#PART_ContentPresenter < VisualLayerManager#PART_VisualLayerManager
                     < Panel < MainWindow < TopLevelHost
    previewerInWindow topLeft=(0,0) bottomRight=(1024,768)

窗口可视树（z 序按兄弟顺序，后面的在上）：

    MainWindow bounds=0, 0, 1024, 768
      Panel bounds=0, 0, 1024, 768                                ← 模板根
        Border#PART_TransparencyFallback ... hitTestVisible=false
        Border ... background=set hitTestVisible=false
        Panel ... background=set                                 ← 修复前指针落在这儿
        VisualLayerManager#PART_VisualLayerManager
          ContentPresenter#PART_ContentPresenter
            Previewer#Viewport                                   ← 我们

对照实验把最后一种可能也排除了：把那个匿名 `Panel` 的命中关掉，重新问一次，
**命中列表里依然没有控件**。所以不是被挡，是控件自己对命中测试隐形。

原因是 Avalonia 12 的命中测试走**合成层**
（`Avalonia.Rendering.Composition.CompositingRenderer.HitTest` /
`CompositionTarget.TryHitTest`）：只有产生了合成视觉的元素才在命中范围里。
别的控件靠 `Border` / `Background` 产生绘制内容，而 `OpenGlControlBase` 的画面是
GL 直接画到窗口上的，**合成器那边它是空的**。

`ICustomHitTest` 就是给这种「自己画自己」的控件留的口子。修复就是实现它：

    bool ICustomHitTest.HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

判据用 `Bounds` 而不是「有没有画东西」：这个控件的画面盖满自己的整个矩形，
让指针穿过去点它背后的东西本来就没有意义。

修复后同一组探针：

    point=(512,384) inputHitTest=Previewer#Viewport < ContentPresenter#PART_ContentPresenter < ...
    point=(10,10)   inputHitTest=Previewer#Viewport < ...
    point=(1014,758) inputHitTest=Previewer#Viewport < ...

### 入参是控件自己的坐标（Phase F 更正）

上面那条一行写完的判据，原本多绕了一次坐标换算：

    // 入参是 TopLevel 坐标，先转回自己的坐标系再跟 Bounds 比。
    Point? local = root.TranslatePoint(point, this);
    return local is { } value && new Rect(Bounds.Size).Contains(value);

它是错的，而且错了很久没被发现：控件当时正好在窗口左上角，偏移是 `(0,0)`，
「转一次」与「不转」完全等价，那三个采样点全过。

Phase F 把控件挪到侧边栏右边（左边偏移 300）之后，`(10,10)` 那一点开始命中失败：
左边缘那 300 个像素被减成了负数，那里放不进 `Bounds`，于是整个控件的命中被放弃，
输入落到窗口模板里那层匿名 `Panel` 上。这次是被探针的断言抓住的——`Debug.Assert` 直接终止进程。

实测（临时在 `HitTest` 里打出入参，看完即删）：

    探针在窗口坐标 (790,400) 提问 -> 这里收到 (490,400) = 790 - 300
    探针在窗口坐标 (310, 10) 提问 -> 这里收到 ( 10, 10) = 310 - 300
    探针在窗口坐标 (1270,790) 提问 -> 这里收到 (970,790) = 1270 - 300

收到的是**控件自己的坐标**，不需要任何换算。这一条是布局逼出来的：
「入参是 TopLevel 坐标」这个说法在控件位于原点时无法被证伪，而它是错的。

记在文档里而不只记在注释里，是因为它的形状很容易再犯：这条判据的正确形式不是
「转一次坐标」，而是**没有坐标要转**。下面任何一句「先把它转回自己的坐标系」都是多余的，
而多出来的那一句在控件位于原点时是恒等变换，测不出来。

## 为什么自检剧本是绿的

剧本用 `RaiseEvent` 把合成事件**直接打到控件上**，而 `RaiseEvent` 不走命中测试。
也就是说，从 Phase D 起，那条剧本验证的是「③ ④ ⑤」三段，
而「① ②」——消息进来和命中落点——它一段都没碰到。

这不是剧本的疏漏，是它的设计边界：它要验的是「事件接上了没有」，
而这次坏的是「事件根本到不了它面前」。补上的守卫是 `input.hittest` 里那条断言：

    Debug.Assert(ReferenceEquals(inputHit, Viewport), ...);

`InputHitTest` 正是输入系统自己用的那条路，命中错了它立刻炸。**这一条是新加的，
合成事件永远覆盖不到它。**

## 注入输入为什么帮不上忙

想用 `SendInput` / `mouse_event` 把真实的鼠标消息送进窗口来做端到端验证，
结果一条都没到——隧道层也是干净的。

原因在 `Avalonia.Win32.dll` 里能直接搜到：它调用 `EnableMouseInPointer` 并处理
`WM_POINTERUPDATE` / `GetPointerInfo` / `IsMouseInPointerEnabled`，
也就是走 **WM_POINTER 系列**而不是 `WM_MOUSEMOVE`。
而 Windows 的规则是：**注入的鼠标输入不产生指针消息**（它不是来自指针设备）。

于是「注入走不通」和「真实鼠标被挡住」在日志里同样是安静。
这两件事靠探针分开：注入那次隧道层也没有输出，而真实鼠标那次有。

顺带一条：`Win32PlatformOptions` 里没有任何输入相关的开关
（只有 `CompositionMode` / `RenderingMode` / `DpiAwareness` 等），关不掉这条路径。
要做真正的端到端输入自动化，只能上驱动级的模拟。

## 修复过程中顺带改掉的

真实滚轮一旦通了，验收剧本立刻红：

    Assertion Failed
    [SAMPLE][selftest] Scrolled 触发次数不对 scrolled=43 expected=3

43 = 脚本发的 3 次 + 屏幕前的人滚的 40 次。这不是实现的问题，
是剧本的断言位置不对：它在窗口关闭时数总数，而验收窗口是一个真实、有焦点、
随时可能被操作的窗口。

改成**在动作发生的同步点上验**——`RaiseEvent` 是同步的，`RaiseWheel` 返回时
这一次已经走完，那一刻的距离就是脚本那三次的结果，外部输入插不进来：

    RaiseWheel(WheelIn);
    RaiseWheel(WheelIn);
    RaiseWheel(WheelOut);

    float afterWheels = _camera.Distance;
    Debug.Assert(MathF.Abs(afterWheels - _expectedDistanceAfterZoom) < 1e-3f, ...);

总数那几条随之放宽成 `>=`。改完之后，同样的外部输入下剧本全绿，
而且日志里能看到真相：

    [SAMPLE][selftest.summary] scrolled=12 wheelIn=6 wheelOut=6
    distance=4.7244->5.6000

12 次里只有 3 次是脚本的，距离停在 5.6 而不是 3.78——**那 9 次真实滚轮确实驱动了相机**。

## 验收

    dotnet run --project src/LitematicaViewer.Previewer.Sample -- --selftest 8

    [SAMPLE][input.hittest] point=(512,384) inputHitTest=Previewer#Viewport < ...
    [SAMPLE][input.hittest] previewerInWindow topLeft=(0,0) bottomRight=(1024,768)
    [SAMPLE][selftest.summary] ticks=430 scrolled=12 wheelIn=6 wheelOut=6 key=2
      resizes=2 lastViewport=1184x768 expected=1184x768 cameraSets=2
      distance=4.7244->5.6000
    [PREVIEWER][gl.deinit] frames=430 elapsed=8.07s avgFps=53.3

真实输入的整条链路（修复前 `source=Panel`，修复后 `source=Previewer`）：

    [SAMPLE][input.probe.window] tunnel wheel delta=(0,-1) source=Previewer
    [PREVIEWER][input.probe] pointer.wheel delta=(0,-1) pos=(...)
    [PREVIEWER][input.wheel] delta=(0,-1)
    [PREVIEWER][event.scrolled] delta=-1 handlers=1
    [SAMPLE][camera.zoom] steps=-1 distance=... clamped=... range=[2.4,7]
    [PREVIEWER][camera.set] version=... pos=(...) yaw=... pitch=...
    [PREVIEWER][gl.cube.render] solid=... visibleFaces=3

## 调试桩

    input.probe        控件收到的原始指针事件（enter/exit/focus/press/release/wheel/moved）
    input.probe.window 窗口隧道层的同一批事件，外加 source 的父链
    input.hittest      三个点上的 InputHitTest 与 GetVisualsAt 结果、控件自身状态、
                       它在窗口坐标里的位置、整棵窗口可视树
    window.activated / window.deactivated
                       WM_MOUSEWHEEL 发给焦点窗口而不是光标下的窗口，
                       「窗口在后台时收不到滚轮」是预期行为，日志里必须留痕

`pointer.moved` 逐条打会把终端冲掉，所以只打首条加每 200 条一条（控件那边是每 60 条）——
它要回答的只是「移动到底到没到」这一个是非题。
`source` 的完整父链只在第一条上打，之后退化成类型名。

## 已知取舍

- 探针把 `AttachInputProbe` 挂在**输入适配器的构造函数**里，而不是 Previewer 自己的构造里：
  「有人打算处理输入了」是它唯一有意义的挂载时机，没人接输入的程序不需要这份日志。
  代价是它挂在 `#if DEBUG` 里，调用点也得跟着包。
- 窗口隧道探针整个在 `#if DEBUG` 里包着（方法和订阅一起）。`Debug.WriteLine` 自带
  `[Conditional]`，但订阅本身会留下几个永远空转的处理器，那是 Release 不该付的代价。
- `Describe` 打父链而不只打类型名：`Panel` 这种基类名有两个完全不同的可能——
  它是控件的祖先，还是盖在控件之上的一层。一眼分得开的只有链本身。

## 未解决

- **端到端输入自动化没有做**。`SendInput` 这条路被 WM_POINTER 堵死了，
  现在能自动验的到 `InputHitTest` 为止。再往上（真实消息 → 命中 → 事件）
  需要驱动级模拟，代价和收益都要重新算。目前靠人滚一下鼠标来补最后那一段。
- 鼠标转视角这一相位不做（规范明确排除）。这一篇修的是「指针能不能到达控件」，
  不是「到达之后做什么」。（Phase F 两件都做了：指针一动就转视角，见 05。）
- 触控 / 笔输入没有验过。`ICustomHitTest` 的判据只用了 `Bounds`，
  对这两类输入应该同样成立，但没有实测。
- `GetVisualsAt` 与 `InputHitTest` 在这次故障里表现一致（都跳过控件），
  所以「两者不一致时问题在合成器」这条经验是从文档推的，没有实测反例。
