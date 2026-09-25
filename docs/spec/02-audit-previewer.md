# 02 Previewer 审计（Phase B–D）

Phase B：空窗口 + `OpenGlControlBase` + GL 初始化 + 清屏色。
Phase C：硬编码立方体 + 固定相机。
Phase D：相机状态 + `SetCamera` + 四个事件 + 输入适配器 + 渲染帧循环。

三个阶段都不涉及 Core，Previewer 至今不知道 Core 存在。

## Phase B：空窗口与清屏色

### 公开面

无。`Previewer` 这一阶段只有受保护的重写和私有探针，没有任何公开成员。
公开面从 Phase D 开始（`CameraState` / `SetCamera` / 四个事件），本阶段刻意不提前开。

### 依赖

`Avalonia` 12.1.3（只引这一个包）。Sample 再加 `Avalonia.Desktop` 与 `Avalonia.Themes.Fluent`。

不引 Silk.NET：实测 Avalonia 12.1.3 的 `GlInterface` 已经暴露了渲染要用的全部入口——
`CreateShader` / `ShaderSource` / `CompileShader` / `CreateProgram` / `LinkProgram` /
`CreateBuffer` / `BufferData` / `GenVertexArray` / `BindVertexArray` / `VertexAttribPointer` /
`Uniform1f` / `Uniform1i` / `UniformMatrix4fv` / `DrawArrays` / `DrawElements` / `TexImage2D` /
`BlitFramebuffer` / `GenFramebuffer` 都在。常量也有现成的：`Avalonia.OpenGL.GlConsts` 是
**公开类型**，79 个 `GL_*` 常量，含 `GL_TRIANGLES` / `GL_VERTEX_SHADER` / `GL_DEPTH_TEST` /
`GL_ARRAY_BUFFER` / `GL_ELEMENT_ARRAY_BUFFER` / `GL_STATIC_DRAW` / `GL_COMPILE_STATUS`。
再加一层 Silk.NET 等于在同一个上下文上挂两套函数表和两套枚举，它唯一多给的类型化枚举不是刚需。

`Avalonia.OpenGL.dll` 就在 `Avalonia` 包里面（`lib/net10.0/` 与 `lib/net8.0/`）。
NuGet 上那个叫 `Avalonia.OpenGL` 的包最新只有 0.7.0，是上古遗留物，不要引。

### 实测的环境事实

- Avalonia 12.1.3 有原生 `net10.0` 资产，不需要靠 `net8.0` 兜底。
- `OpenGlControlBase.OnOpenGlRender(GlInterface, int)` 是 **abstract**，必须重写；
  `OnOpenGlInit` / `OnOpenGlDeinit` / `OnOpenGlLost` 是带实现的 `protected virtual`，
  可重写可不重写，本实现调了 `base`。
- 受保护属性 `GlVersion`（类型 `Avalonia.OpenGL.GlVersion`，是个 struct，含
  `Type` / `Major` / `Minor` / `IsCompatibilityProfile`）**只有 getter**。也就是说
  没法要求「3.3 core」之类的具体版本，上下文版本由 Avalonia 按平台后端决定。
- 本机实测拿到的上下文：

      version    = 'OpenGL ES 3.0 (ANGLE 2.1.1 git hash: 1c89805903c1)'
      renderer   = 'ANGLE (AMD, AMD Radeon RX 580 2048SP, Direct3D11 vs_5_0 ps_5_0, D3D11-31.0.21925.1001)'
      vendor     = 'Google Inc. (AMD)'
      profile    = OpenGLES   gl=3.0   compatibility=False   extensions=128
      cap        vao=True  blit=True  drawBuffer=False

  两条推论直接约束 Phase C：着色器要写 GLES 的 `#version 300 es` 那一套（带精度限定符），
  不能照抄桌面 GL 的写法；`glDrawBuffer` 不可用，将来做多目标渲染时不能用它。

- 传给 `OnOpenGlRender` 的 `fb` **实测是 1，不是 0**。也就是说 Avalonia 渲染到自己的
  framebuffer 上，而不是默认帧缓冲。所以那次 `BindFramebuffer` 不能省——省掉的症状是
  清到了别处，而屏幕上什么都没有。
- `Bounds` 是 DIP，framebuffer 是物理像素。视口要乘 `TopLevel.GetTopLevel(this).RenderScaling`。
- 渲染是**按需**的：4 秒里只画了 1 帧，没人 invalidate 就不会再画。Phase D 要让相机
  动起来，得靠 `RequestNextFrameRendering()` 或事件驱动把帧推起来，不能指望自动循环。

### 验收结果

    dotnet run --project src/LitematicaViewer.Previewer.Sample -- --selftest <秒>
    dotnet run --project src/LitematicaViewer.Previewer.Sample            # 不传参数就一直开着

不带开关是正常应用：开一个 1024×768 的窗口，里面整块是 `Previewer`，背景是设定的清屏色。
带 `--selftest <秒>` 则到点自动走正常关闭路径（不是 `Shutdown`），把 Deinit 的帧数断言一并跑掉。

一次 4 秒自检的实际输出：

    [SAMPLE][app.start] args=[--selftest 4] baseDir='...bin\Debug\net10.0\'
    [SAMPLE][window.opened] client=1024, 768 scaling=1
    [PREVIEWER][gl.init] version='OpenGL ES 3.0 (ANGLE 2.1.1 ...)' renderer='ANGLE (AMD, ...)' vendor='Google Inc. (AMD)'
    [PREVIEWER][gl.init] clearColor=(0.12,0.3,0.55,1) expected=(0.12,0.3,0.55,1)
    [PREVIEWER][gl.init] profile=OpenGLES gl=3.0 compatibility=False extensions=128
    [PREVIEWER][gl.init] cap.vao=True cap.blit=True cap.drawBuffer=False
    [PREVIEWER][gl.render] frame=1 fb=1 bounds=1024x768 scaling=1 viewport=1024x768
    [PREVIEWER][gl.readback] center=(31,76,140,255) expected=(31,76,140,255)±1
    [PREVIEWER][gl.deinit] frames=1 expected=>0
    [SAMPLE][window.closed] client=1024, 768 scaling=1

「背景纯色」不是靠看日志成立的：`CheckFrameBufferCenter` 用 `glReadPixels` 把 framebuffer
中心的像素读回来，断言它等于清屏色换算到 8 位的值。这同时证明了清屏落在 Avalonia 交给我们
的那个 framebuffer 上。读回实测 `(31,76,140,255)`——0.12/0.30/0.55 × 255 = 30.6/76.5/140.25，
同一批里 30.6 进位成 31 而 76.5 舍成 76，取整规则从数值上推不出来，所以断言留 1 的余量，
而不是把某个后端的取整方式当成规范。

从 Phase C 起，屏中间是立方体而不是背景色，这条断言随之改成「四角是背景色 +
可见面恰好是朝向相机的那三个」，见下一节。

`CheckFrameBufferCenter` 那段是**唯一**用 `#if DEBUG` 包住的地方，而且是连调用点一起包的：
断言本身在 Release 下会消失，但 `ReadPixels` 强制的那次 GPU 同步不会。判据很简单——
搜程序集。判定的方法可复用：

| | Debug | Release |
|---|---|---|
| `glReadPixels` | 有 | 无 |
| `gl.readback` / `gl.init` / `gl.render` | 有 | 无 |

也就是说 Release 下探针不只是「不输出」，而是字符串都不在程序集里。

### 调试桩

格式 `[PREVIEWER][<阶段>]` 与 `[SAMPLE][<阶段>]`，直接调 `Debug.WriteLine` 与 `Debug.Assert`。
桩保留，不删。

    app.start        参数、基准目录、自检秒数
    window.opened    客户端尺寸与 RenderScaling
    gl.init          版本串、渲染器、厂商、清屏色、profile/版本/兼容位/扩展数、
                     vao / blit / drawBuffer 三个能力位
    gl.render        第 1 帧与之后每 60 帧一次：帧号、fb、Bounds（DIP）、缩放、视口（像素）
    gl.readback      framebuffer 中心像素 vs 期望值
    gl.deinit        累计帧数
    gl.lost          上下文丢失（本阶段只记一笔）

代码内的不变量断言：

- GL 回调必须落在构造控件的那条线程上（R4）。GL 上下文不跨线程，而这个错误不抛异常，
  只在某些机器上偶发黑屏或花屏。
- `gl.Version` 不能为空。
- Deinit 时帧数必须大于 0。挂上去了却一帧没画，从应用侧看和正常工作没有区别。

## Phase C：硬编码立方体

### 文件与职责

    GlCubeRenderer.cs     立方体几何、固定相机、六个面的方向表
    Gpu/GlShader.cs       编译 / 链接 / 设 uniform，IDisposable
    Gpu/GlMesh.cs         VAO + 交错顶点缓冲 + 索引缓冲，IDisposable
    Debug/DebugCube.cs    几何不变量 + 整帧读回的校验

全部是 `internal`。Previewer 的公开面仍然是零，「Phase D 之前不往上面挂东西」这条约定没破。

`Gpu/GlResourceManager.cs` 没有建：目前一个 mesh 一个 shader，持有者 `GlCubeRenderer` 自己就是。
再加一层管理者只是转发。等出现第二个 mesh（中间层落地）再抽。

### 实测的渲染事实

- 上下文是 ANGLE 给的 **GLES 3.0**，所以着色器必须写 `#version 300 es`，且每个 stage 都要有精度限定符。
  桌面 GL 的 `#version 330 core` 在这里编译不过。
- 首帧结束后 `glGetError` 是 `0x0`：VAO、索引缓冲的绑定、属性指针、绘制调用这一串在 ES 3.0 下都合法。
- Avalonia 交过来的 framebuffer **带可用的深度附件**：开了深度测试之后，背向相机的三个面一个像素都没露出来
  （除共边处的 1 个，见下）。
- 那个 framebuffer **没有多重采样**：整帧 786432 个像素，认不出的颜色是 0 个，边缘是硬切的。

三个可见面的像素数之比，正好等于三个面法线与视线的夹角余弦之比：

| 面 | 法线 | 法线·眼点 | 像素数 | 像素数 / 点积 |
|---|---|---|---|---|
| +X | (1,0,0) | 2.6 | 20864 | 8025 |
| +Y | (0,1,0) | 2.0 | 14317 | 7159 |
| +Z | (0,0,1) | 3.4 | 30355 | 8928 |

最后一列接近常数。这是透视投影与深度测试同时正确的证据——深度测试失效的话，
整个轮廓只会剩最后提交的那一种颜色。

### 验收结果

    dotnet run --project src/LitematicaViewer.Previewer.Sample -- --selftest 4

    [PREVIEWER][gl.cube] vertices=24 indices=36 eye=<2.6, 2, 3.4> fov=45 expected=24/36
    [PREVIEWER][gl.error] code=0x0 expected=0x0
    [PREVIEWER][gl.cube.face] face=0 normal=<1, 0, 0> color=255,128,128 pixels=20864 towardCamera=True
    [PREVIEWER][gl.cube.back] face=1 normal=<-1, 0, 0> pixels=1
    [PREVIEWER][gl.cube.face] face=1 normal=<-1, 0, 0> color=0,128,128 pixels=1 towardCamera=False
    [PREVIEWER][gl.cube.face] face=2 normal=<0, 1, 0> color=128,255,128 pixels=14317 towardCamera=True
    [PREVIEWER][gl.cube.face] face=3 normal=<0, -1, 0> color=128,0,128 pixels=0 towardCamera=False
    [PREVIEWER][gl.cube.face] face=4 normal=<0, 0, 1> color=128,128,255 pixels=30355 towardCamera=True
    [PREVIEWER][gl.cube.face] face=5 normal=<0, 0, -1> color=128,128,0 pixels=0 towardCamera=False
    [PREVIEWER][gl.cube.render] solid=65537 clear=720895 unmatched=0 coverage=8.3% visibleFaces=3 expected=3
    [PREVIEWER][gl.cube.render] centroid=(509.1,380.1) expected=(512.0,384.0)

`CheckRendered` 把整张 framebuffer 读回来，按颜色反推画面上到底出现了什么，断言六件事：

1. 画面里有非背景像素。
2. 朝向相机的面恰好 3 个。相机在 `<2.6,2,3.4>`，三个分量都是正的，所以可见的必然是 +X / +Y / +Z。
3. 每个可见面都占到足够多的像素（至少轮廓的 5%）。
4. 背向相机的面不超过万分之一个像素。
5. 认不出的颜色不超过 1%。这条目前恒成立，留着是给以后开 MSAA 或换后端用的。
6. 轮廓质心落在画面中心 3% 以内。立方体关于中心对称，透视投影保持中心对称，
   所以质心必然落在视线的落点上；投影矩阵转置错了或者宽高比算反了，这条立刻偏出去。

这套能证明的：几何没画错、索引没跨面、深度测试生效、投影矩阵方向没错、法线与面色的对应没错。
不能证明的：立方体的朝向是否和代码里的固定相机一致——把相机和立方体一起转 90 度，所有断言照样过。
那一条要等相机由输入驱动之后才验得了。

### 那 1 个像素

`face=1`（背向相机的 -X 面）漏出 1 个像素。原因是共边处的深度相等：同一个像素中心被两个相邻三角形
同时覆盖时，插值出的深度也相等，而 `GL_LESS` 是**严格小于**，先画的那个留下。
共边处先画的正好是背向面时，就漏出这一个像素。

判据因此写成「不超过万分之一个像素」而不是「等于零」。两者量级差着五个数量级：
深度测试真失效是整个轮廓只剩一种颜色，几万个像素。

要一个不漏得上多边形偏移（`glPolygonOffset`）或者把共边顶点错开。`glPolygonOffset`
不在 `GlInterface` 已封装的那批入口里，要用得自己取函数地址——那是以后的事。

### 为什么立方体和它的验收在同一个 commit

读回校验在 Phase B 断言的是「画面中心是清屏色」。Phase C 一旦在中间画上东西，这条立刻为假。
先把立方体提交、后把校验提交，中间那个 commit 的断言必然失败，bisect 到它会得到错误的结论。
两者只能一起进。

## Phase D：事件骨架

### 文件与职责

    CameraState.cs               相机状态 + yaw/pitch 约定 + 视图 / 投影矩阵
    Previewer.Events.cs          对外公开面：四个事件 + SetCamera
    PreviewerInputAdapter.cs     Avalonia 输入 -> Previewer 事件
    Sample/InputSelfTest.cs      合成输入事件的验收剧本

### 公开面

    CameraState（readonly record struct，六个字段全部不可变）
      static CameraState Default                          默认视角（Phase C 写死的那一组数）
      static Vector3 ForwardOf(float yaw, float pitch)
      static CameraState LookAt(Vector3 eye, Vector3 target, float fov, float near, float far)
      Vector3 Forward
      Matrix4x4 GetViewMatrix()
      Matrix4x4 GetProjectionMatrix(float aspectRatio)

    Previewer
      event Action<float>? Scrolled
      event Action<Key, bool>? KeyChanged
      event Action<int, int>? ViewportResized
      event Action<double>? Tick
      void SetCamera(CameraState camera)

没有 `GetCamera`，没有 `PointerMoved` / `PointerPressed`，没有 `SetMeshes`。
相机状态的权威在控制器侧（Phase E/F 的 `CameraModel`），Previewer 只是它的消费者。

（Phase F 补了三个手势事件 `LookStarted` / `LookMoved` / `LookEnded`：指针移动就是在转视角。
裸的 `PointerMoved` 仍然没有——差值要在能记住上一次位置的地方算，那是控制器。见 05。）

`Key` 直接用 `Avalonia.Input.Key`，不再造一个自己的枚举：Previewer 本来就依赖 Avalonia，
多一层翻译表只多一个漏项的地方。

### 相机约定（这一相位钉死的接口）

yaw 0 朝 +Z，yaw 增大绕 +Y 转向 -X（俯视图里顺时针），pitch 增大是向下看，单位是度。

    yaw=0    pitch=0     ->  +Z
    yaw=90   pitch=0     ->  -X
    yaw=0    pitch=90    ->  近似 -Y（被夹到 89.9，差 0.1 度）

约定必须只有一份实现。画面是这份状态喂给视图矩阵算出来的，将来的屏幕射线拾取是同一份状态
喂给另一套三角函数算出来的；两边但凡有一点不一致，症状是准星压着 A 却拾到 B——不抛异常、
不报错，距离永远差一点，而且只有一边存在的时候根本测不出来。所以 `ForwardOf` 是唯一换算入口，
`LookAt` 是它的逆，往返对拍写成了可执行断言（`CameraState.VerifyConvention`，上下文初始化时跑一次）。

三条断言：

1. 上表前两条精确相等；第三条按「基本朝下」判（`dot(down, -UnitY) > 0.9995`），
   因为夹取让它不是精确的 -Y。
2. 夹取之后仍是单位向量。退化成零向量的视图矩阵整个失效，而 GL 一声不吭。
3. `LookAt` 之后 `Forward` 回到原方向，64 个随机方向，容差 1e-5。随机方向的 y 分量压在 ±0.9 以内：
   接近正上/正下时 pitch 顶到夹取边界，往返本来就不闭合——那是夹取的必然结果，不是 bug，
   拿它当反例会掩盖真正的不一致。

pitch 夹在 ±89.9 度，且夹在**取用点**而不是构造点：读 `Forward` 的每一处（视图矩阵、将来的屏幕射线）
拿到的是同一个方向。在构造点夹会把控制器手里那份状态悄悄改掉，控制器按自己那份算、渲染按夹过的那份画，
两边越差越远。到 ±90 度时 forward 与 up 共线，`CreateLookAt` 退化，画面整块消失而 GL 不报任何错。

### 事件与帧循环

- `Scrolled` / `KeyChanged` 由输入适配器投递，`ViewportResized` / `Tick` 由 Previewer 自己发。
- 投递时把订阅者数量一并打出来。没人订阅时的行为和「事件根本没触发」一模一样，
  而这个相位的验收恰好是「能看到事件触发」——打出 0 就是答案。
- `ViewportResized` 只在尺寸变化时发，首帧那次是 0x0 到实际尺寸。给的是**物理像素**，与 GL 视口一致。
- `Tick` 在画之前发：控制器在 `Tick` 里 `SetCamera`，这一帧立刻用得上，少一帧延迟。
  反过来先画再 tick 也跑得起来，代价是相机永远落后一帧，拖起来像有阻尼。
- `Tick` 的参数是距上一帧的秒数，**夹在 0.25 秒**。拖窗口、停在断点、切显示器都会让两帧隔上几百毫秒，
  不夹住的话控制器拿着这个 dt 一积分就是一整段瞬移，滚动缩放会直接跳穿。
- 帧循环改成自驱动：每帧末尾 `RequestNextFrameRendering()`，节流交给合成器。
  不改成「只在相机变化时才请求」是因为 `Tick` 是控制器的时间来源：一旦没有输入就不出帧，
  靠时间推进的东西（惯性、缩放动画）会直接停住。代价是空闲时也按刷新率出帧。

### 输入适配器

宿主构造它，它就订阅控件的 `PointerWheelChanged` / `KeyDown` / `KeyUp` 并转发。

- 构造时打开 `Focusable`，宿主在窗口打开后调一次 `Focus()`。键盘事件只发给获得焦点的元素，
  控件默认不可聚焦；漏掉这一步的症状是「滚轮有效、按键毫无反应」，看起来像事件没接上。
- 滚轮只取 `Delta.Y`。横向滚轮要做就另加一个事件，而不是往同一个 delta 里塞两个含义——
  塞进去之后没人能从签名上看出它到底是哪个。
- 长按会重复触发 down（平台自动重复）。事件只报告状态，去重是控制器的事：
  控制器按状态处理时重复的 down 天然幂等，按边沿处理才会踩到。
- 它不碰 GL，也不碰相机，连 GL 上下文都拿不到，R2 自然成立。

### 画面校验（从「固定 3 个可见面」改成「与当前相机一致」）

`CheckRendered` 现在接受一个 `CameraState`，自己算出哪些面**应该**可见
（`dot(normal, camera - faceCenter) > 0`），再断言观测到的像素分布与这个集合完全一致。

这比原来那句「可见面恰好 3 个」强得多：渲染器若还在用某个写死的相机，观测集合就会和当前相机
算出来的对不上，而它自己不会报任何错。判据里减掉面心那 0.5 是必要的——只算 `dot(normal, camera)`
会把已经侧转过去的面算成可见，相机贴近时才会暴露。

触发时机也跟着改了：**相机一变就重验一帧**，总数封顶 6 次。整张 framebuffer 读回来会强制 GPU 同步，
逐帧读会把帧率打到地板上；而换了相机就是换了一张画面，值得验一次，没换相机时同一张画面验两遍不给新信息。

### 验收结果

    dotnet run --project src/LitematicaViewer.Previewer.Sample -- --selftest 6

剧本按**秒**推进而不是按帧（帧率随机器与窗口大小变）：0.4s 转 90 度、0.8s 转 180 度、
1.2s 滚轮三次、1.6s 按下 W、2.0s 松开 W、2.4s 把窗口拉宽 160。

    四帧画面校验，每次都与当前相机一致：

    pos=(2.6, 2, 3.4)        yaw=142.59  可见 {+X,+Y,+Z}  pixels 20864/14317/30355  solid=65537
    pos=(3.4, 2, -2.6)       yaw=52.59   可见 {+X,+Y,-Z}  pixels 30356/14317/20864  solid=65537
    pos=(-2.6, 2, -3.4)      yaw=-37.41  可见 {-X,+Y,-Z}  pixels 20864/14317/30355  solid=65537
    pos=(-2.08, 1.6, -2.72)  yaw=-37.41  可见 {+X,+Y,-Z}  pixels 32660/22416/47538  solid=102693

    [SAMPLE][selftest.summary] ticks=328 scrolled=3 wheelIn=2 wheelOut=1 key=2 keyDown=True keyUp=True
    resizes=2 lastViewport=1184x768 expected=1184x768 cameraSets=5 elapsed=5.89s
    [PREVIEWER][gl.deinit] frames=328 elapsed=6.09s avgFps=53.9

三条值得记下来的结果：

- **绕 +Y 转 90 度之后，三个可见面的像素数是同一组数换了个面**（20864/14317/30355），
  连轮廓总面积都是同一个 `solid=65537`。这不是巧合：投影轮廓面积按正交近似等于
  Σ|cos θ|，而 90 度旋转把 (x,z) 变成 (-z,x)，|x|+|z| 不变。换个 90 度倍数以外的角度，
  这组数就会散开。换句话说，这条是「投影方向确实按 yaw 转了 90 度」的定量证据。
- 推近到 0.8 倍距离后 `solid` 从 65537 涨到 102693，比例 1.567 对得上 1/0.64 = 1.5625——
  透视下的小偏差正是「相机确实移动了」的痕迹，等比缩放是正交投影才会有的。
- 四帧的 `unmatched=0`、`backFacePixels ∈ {0,1}`。共边那 1 个像素依旧按占比判。

### 调试桩

    camera.convention  三个轴向、夹取后的单位性、LookAt/Forward 往返 64 次
    camera.set         每次 SetCamera 的版本号、位置、yaw/pitch、forward、fov/near/far
    event.scrolled     delta 与订阅者数量
    event.key          键、按下还是松开、订阅者数量
    event.resize       新尺寸、旧尺寸、订阅者数量
    gl.render          帧号、fb、Bounds（DIP）、缩放、视口（像素）、dt、累计秒数
    gl.cube.render     逐面像素数、期望可见与否、轮廓质心、当前相机
    gl.deinit          帧数、总时长、平均帧率

代码内的不变量断言（Phase D 新增）：

- `SetCamera` 必须在 UI 线程上被调用（R4 的守卫，同 GL 回调）。将来相机若改由别的线程驱动，
  这里必须换成 `Channel`：`CameraState` 是六个字段的结构体，两个线程同时读写会撕裂，
  读到的可能是新 `Position` 配旧 `Yaw`，而那表现成「画面偶尔抖一下」，没人会想到是这里。
- `SetCamera` 的入参必须合法（位置有限、yaw/pitch 有限、near > 0、far > near、fov ∈ (0,180)）。
  这些算出 NaN 的话，矩阵会带着 NaN 一路传到 GPU，而 GL 不报错，画面直接空白。
- 出范围的 `pitch` 只记一笔不判死：夹取发生在取用点，控制器手里的状态和实际生效的本就能不同，
  这里断言死会把一个合法的调用判成错误。但也不能不响——它会一直生效不了，而日志里什么都没有。

### 验收剧本为什么合成真实路由事件

「事件触发了」光看探针说服力不够：没人订阅时它同样安静，而「安静」和「没接上」在日志里长得一样。
所以剧本把 Avalonia 的 `KeyEventArgs` / `PointerWheelEventArgs` 合成出来，`RaiseEvent` 打进控件。

合成真事件而不是直接调 `Previewer` 的内部投递方法：这样输入适配器那一层（订阅、映射、方向、
只取 Y 分量）也在验证范围里。绕过适配器，它把方向接反了剧本照样全绿。

滚轮那一步是**三次**而不是一上一下：一来一回正好抵消，相机回到原位，
「相机一变就验一帧」看到的是上一张画面，等于什么都没验。净效果是推近到 0.8 倍。

窗口拉宽那一步断言的是 `resizes >= 2` 且最后一次的尺寸等于窗口客户端尺寸乘 `RenderScaling`。
只断言 `>= 1` 的话，把缩放那一步删掉也能过（首帧那次就够了），那就白验了。

## 已知取舍

- Sample 用 `Exe` 而不是 `WinExe`。WinExe 会把 stdout 摘掉，探针就只剩挂在调试器上时看得见。
  代价是运行时会附着一个控制台窗口。
- 显式指定 `Win32RenderingMode.AngleEgl`，不交给平台默认值。Win32 下能提供 GL 上下文的后端
  不止一个（`Software` / `AngleEgl` / `Wgl` / `Vulkan`），而 `OpenGlControlBase` 要的是能直接
  跑 GL 的那一个。
- `--selftest` 这个开关放在 Sample 而不是 Previewer。Previewer 的公开面只留那几个成员，
  不能为了测试往上面挂东西。
- 撤销注册用的是 window `Closed` 事件而不是 `Shutdown`，因为 `Shutdown` 会跳过 GL 的 Deinit，
  而那正是帧数断言所在的地方。
- 清屏色取蓝 `(0.12, 0.30, 0.55)`。不取白：立方体六个面按法线着色，全是浅色，
  白底上会糊成一片。不取纯黑：纯黑和「一帧都没画出来」在截图里分不开。
- 规范列的文件里，`Gpu/GlResourceManager.cs` 那时没有创建：要等出现第二个 mesh 才有东西可管。
  `Sample/Controllers/*` 属 E / F，在 Phase E 落地了其中两个（见 03）。
  其余（`Previewer.cs` / `Previewer.Events.cs` / `PreviewerInputAdapter.cs` / `CameraState.cs` /
  `GlCubeRenderer.cs` / `Gpu/*` / `Debug/DebugCube.cs`）都已就位。
- 规范里 Sample 的文件清单没有 `App.axaml` / `App.axaml.cs`，但 Avalonia 必须有 `Application`
  子类，所以这两个文件是必需的补充。
- 立方体顶点由 `(法线, 切向 U, 切向 V)` 表生成，而不是手写 24 个顶点共 144 个浮点数。
  手写的 144 个数不可能靠眼睛查错，而表生成让「U × V = 法线」这条绕序约束可以被断言守着。
- `DebugCube` 的命名空间是 `LitematicaViewer.Previewer.Diagnostics` 而不是 `.Debug`：
  后者会在作用域链上跟 `System.Diagnostics.Debug` 撞名。文件夹名按规范保持 `Debug/`。
- 为 `UniformMatrix4fv` 开了 `AllowUnsafeBlocks`。它只收 `Void*`，没法用 `IntPtr` 绕开
  （除非再走一遍 `GetProcAddress` 取函数地址，为一行代码不值得）。其余地方不用 unsafe。
- 没开背面剔除。深度测试已经把正确性兜住了，剔除省的是光栅化，等有几十万个三角形再说。
- 相机从 `GlCubeRenderer` 挪到了 `CameraState`（Phase D），渲染器每帧从外面拿到它算矩阵，自己一个数都不留。
  相机状态的权威在消费侧，渲染器不该持有它——持有就会有人以为可以从渲染器读回相机。

## 未解决

- 没有目视确认。所有结论都来自像素统计：颜色对、位置对、面积比例对、覆盖率对，
  但「看起来像个立方体」这件事没有人看过。你看一眼就能补上。
- 鼠标转视角这一相位不做（规范明确排除）。**Phase F 做了**，而且是「指针一动就转」、
  不用按键（`Look*` 三个事件 + `MouseLookController`，见 05）——所以相机现在有了持续改它的东西。
  屏幕射线拾取还缺的只剩「屏幕上的点 → 世界射线」那半边。
- 相机还没有由控制器驱动。Phase D 只把入口和事件摆好，所以那一刻没有任何东西会持续改相机——
  转视角只能靠合成事件或者代码。`CameraModel` 与 `ScrollZoomController` 在 Phase E 落地（见 03），
  `WasdCameraController` 在 F。
- 空闲时也按刷新率出帧（实测 53 fps）。这一条是有意的取舍（`Tick` 是控制器的时间来源），
  但如果以后发现待机耗电不可接受，就得改成「有订阅者或相机变过才请求下一帧」，
  同时给靠时间推进的控制器留一个退路。
- 上下文丢失后的重建没验过。`OnOpenGlLost` 现在只丢引用、不发 GL 调用，
  重建依赖 Avalonia 再来一次 `OnOpenGlInit`。这一条是照契约写的，不是验过的——
  要主动触发得制造 TDR 或者切一次远程桌面，代价太大。
- 「立方体朝向与相机一致」这一条现在**有**证据了（四帧的可见面集合随相机变），
  但仍然是相对证据：真正对着已知方块去点位，要等拾取接上。
- 没有开背面剔除，也没用深度偏移去消那 1 个共边像素。两者都要等有真实网格之后才有意义。
