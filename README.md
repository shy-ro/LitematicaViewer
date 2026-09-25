# LitematicaViewer

Minecraft `.litematic` 投影文件预览器。

## 结构

    src/LitematicaViewer.Core             读文件：.litematic -> LitematicDocument
    src/LitematicaViewer.Previewer        Avalonia + Silk.NET 的 OpenGL 视口
    src/LitematicaViewer.Previewer.Sample 可运行的示例宿主
    docs/spec                             约定与各阶段审计

Core 与 Previewer 之间零引用。把 Document 转成 mesh 的中间层尚未设计。

## 构建

    dotnet build

SDK 版本由 `global.json` 钉在 10.0.401。

## Core 的调试入口

Core 在 Debug 配置下是一个可执行程序，用于对真实文件跑一遍解析并校验不变量：

    dotnet run --project src/LitematicaViewer.Core -- <某个.litematic的路径>

失败时返回非 0。Release 配置下 Core 是普通库，该入口不参与构建。
