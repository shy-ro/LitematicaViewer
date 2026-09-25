# 02 Previewer 审计（Phase B）

空窗口 + `OpenGlControlBase` + GL 初始化 + 清屏色。本阶段不画任何几何，
Previewer 也还不知道 Core 存在。

## 公开面

无。`Previewer` 这一阶段只有受保护的重写和私有探针，没有任何公开成员。
公开面从 Phase D 开始（`CameraState` / `SetCamera` / 四个事件），本阶段刻意不提前开。

## 依赖

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

## 实测的环境事实

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

## 验收结果

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

## 调试桩

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

## 已知取舍

- Sample 用 `Exe` 而不是 `WinExe`。WinExe 会把 stdout 摘掉，探针就只剩挂在调试器上时看得见。
  代价是运行时会附着一个控制台窗口。
- 显式指定 `Win32RenderingMode.AngleEgl`，不交给平台默认值。Win32 下能提供 GL 上下文的后端
  不止一个（`Software` / `AngleEgl` / `Wgl` / `Vulkan`），而 `OpenGlControlBase` 要的是能直接
  跑 GL 的那一个。
- `--selftest` 这个开关放在 Sample 而不是 Previewer。Previewer 的公开面要留给 Phase D 定义的
  那几个成员，不能为了测试往上面挂东西。
- 撤销注册用的是 window `Closed` 事件而不是 `Shutdown`，因为 `Shutdown` 会跳过 GL 的 Deinit，
  而那正是帧数断言所在的地方。
- 清屏色取蓝 `(0.12, 0.30, 0.55)`。不取白：立方体六个面按法线着色，全是浅色，
  白底上会糊成一片。不取纯黑：纯黑和「一帧都没画出来」在截图里分不开。
- 规范里列的 `Previewer.Events.cs` / `PreviewerInputAdapter.cs` / `CameraState.cs` /
  `GlCubeRenderer.cs` / `Gpu/*` / `Debug/DebugCube.cs` / `Sample/Controllers/*` 本阶段**没有创建**。
  它们各自属于 C / D / E / F，现在建就是空文件占位。
- 规范里 Sample 的文件清单没有 `App.axaml` / `App.axaml.cs`，但 Avalonia 必须有 `Application`
  子类，所以这两个文件是必需的补充。

## 未解决

- 视口只由日志里的数字（1024×768 与窗口客户端尺寸相等）背书，没有目视确认。
  `glClear` 不受视口影响，所以读回像素证明不了视口对不对。等 Phase C 画出立方体，
  形状对不对本身就是视口的检验。
- 帧率没有测量。当前是按需渲染，没有循环，测了也没有意义。
- `OnOpenGlLost` 里只记了一笔。从 Phase C 起这里必须把 GPU 资源全部标成待重建，
  否则上下文丢失后画面会一直是黑的而没有任何报错。
