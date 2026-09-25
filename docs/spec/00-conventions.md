# 00 通用约定

适用于本仓库全部代码。各阶段的实现细节与审计结论放在 01/02/03 里。

## 分层

    Core（Poly.NBT）              Previewer（Avalonia）
         ↑                                ↑
     中间层（MeshBuilder，尚未设计）───────┘

Core 与 Previewer 之间零引用。Previewer 不知道 Core 存在，Core 不知道渲染存在。
把 `LitematicDocument` 转成 mesh 的中间层等两边都能独立跑起来之后再设计契约。

Previewer 只用 Avalonia 自带的 GL 绑定（`Avalonia.OpenGL.GlInterface`），不引 Silk.NET。
实测 Avalonia 12.1.3 的 `GlInterface` 已经覆盖了渲染要用的全部入口——着色器编译、
缓冲区、VAO、uniform、绘制、纹理、FBO 都在，缺的只是类型化枚举。加第二套绑定层
只会多一份枚举和函数表，而常量本身有现成的：`Avalonia.OpenGL.GlConsts` 是公开类型，
79 个 `GL_*` 常量都在里面，包括后面的 `GL_TRIANGLES` / `GL_VERTEX_SHADER` / `GL_DEPTH_TEST`。

在中间层落地之前，任何一边都不得为了让另一边好过而修改自己的公开面。

## 硬约束

R1  GPU 资源由创建者持有，实现 `IDisposable`
R2  使用层不创建、不销毁 GPU 资源
R3  全项目零 lock，跨线程用 `Channel` 或 `Task`
R4  GL 调用只在主线程
R5  GPU 资源的创建与销毁只出现在 `Initialize` / `Dispose` 两处
R6  纯 CPU 数据用 `record` / `ImmutableArray`
R7  无状态计算写成静态纯函数
R8  未经确认不进入下一个 Phase

## 命名

- 类型、方法、属性：PascalCase。私有字段：`_camelCase`。局部变量与参数：`camelCase`。
- 跨层的值对象用 `record`，几何用 `readonly record struct`。
- 单位：方块坐标一律 int，不用浮点，避免偏移叠加时的相等性比较失真。

## 源码注释

只写「为什么」。非显然的决策、踩过的坑、反直觉的实现。例如：

    // 用 OpenGlControlBase 而非 NativeControlHost：Avalonia 已封装 GL 上下文生命周期。
    // 若日后需要多视口或自定义上下文，再换。

不写「这个方法做什么」、不写参数含义（除非反直觉）、不写职责/调用者/资源边界/线程归属。
后面这几类内容全部进 `docs/spec/*.md`。

## 调试信息

调试输出直接用 `System.Diagnostics.Debug.WriteLine` 与 `Debug.Assert`。这两个 API 自带
`[Conditional("DEBUG")]`，Release 下编译器会移除整个调用点，所以通常不需要再套 `#if DEBUG`。
只有当探针本身需要一段 Release 不该付代价的计算时，才用 `#if DEBUG` 包住那段计算。

探针格式：

    [CORE][<阶段>] <变量>=<值> expected=<期望>

桩在功能通过验证后不删除，保留备查。

## 测试

测试不独立成项目。核心不变量、边界、踩过的坑的回归放在各项目内部（`Tests/` 或源码中的
`Debug.Assert`），不引入 xUnit。没有意义的测试不写。

## Git

- `main` 任何时候可编译、可运行。
- 每个 Phase 一条独立分支：`phase/a-core`、`phase/b-previewer-init`、`phase/c-previewer-cube`、
  `phase/d-events`、`phase/e-scroll`、`phase/f-wasd`。
- 用户确认后才 `git checkout main && git merge --no-ff phase/x`。禁止未经确认并入 main。
- 不允许把 main 合进 Phase 分支，要同步就用 rebase。

提交格式：

    <type>(<scope>): <subject>

    <body>

`type` 取 `feat` / `fix` / `refactor` / `test` / `docs` / `chore`。
`scope` 取 `core` / `previewer` / `sample` / `build` / `docs`。
`subject` 用英文祈使句，首字母小写，无句号，不超过 72 字符。`body` 写 why，不写 what。

一个 commit 只做一件事。判断标准：revert 这个 commit 之后其他功能还正常工作吗；能则粒度合适。
禁止 AI 署名、禁止无意义 message、禁止一个 commit 跨 Phase、禁止提交不能编译的代码。

禁止的 git 操作：向 main 强推、amend 已推送的 commit、不先 stash 就 `reset --hard`。
