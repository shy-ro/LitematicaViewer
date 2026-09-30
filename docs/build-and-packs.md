# 构建与使用说明

拿到本仓库的 build 之后能跑什么、怎么装、资源包放哪，都在这一篇。

## 仓库结构（分层零引用）

```
src/
  LitematicaViewer.Core            读 .litematic（NBT -> Document）
  LitematicaViewer.Assets          资源包/图集/blockstate 解析（含 PackSmoke 验收）
  LitematicaViewer.Meshing         Document -> 网格（含 MeshSmoke 验收）
  LitematicaViewer.Previewer       GPU 渲染零件（GlMeshRenderer 等纯零件库）
  LitematicaViewer.SamplePreviewer 交互式预览器（Avalonia 桌面程序）
  LitematicaViewer.ShellPreview    Windows 预览窗格 COM handler（AOT dll）
  LitematicaViewer.Setup           安装器（把 ShellPreview dll 嵌进单文件 exe）
tools/                             辅助脚本（生成测试文件、出安装包）
dist/                              产物与部署脚本（deploy_setup.py）
```

依赖单向：SamplePreviewer 与 ShellPreview 引用 Previewer/Assets/Core/Meshing，没有人反向引用。

环境要求：.NET 10 SDK（global.json 已钉版本，装好 SDK 后 `dotnet build` 即可）。

## 三个产物的构建

### 1. 交互式预览器（日常看文件用）

```
dotnet build src/LitematicaViewer.SamplePreviewer -c Debug
src/LitematicaViewer.SamplePreviewer/bin/Debug/net10.0/LitematicaViewer.SamplePreviewer.exe 某文件.litematic
```

可选参数：`--shot 输出.png` 载入后自动截帧落盘；`--shot-dist 系数` 取景距离倍数。
资源包从 exe 目录向上最多七层找 `packs/`，仓库根就是命中位置，开发时不用配置。

约定：所有测试产物（截帧、诊断图、montage、rects 落盘、测试 litematic、日志）一律写进仓库根的 `test_temp/`（已 gitignore）——tools 脚本已按此定向，手工跑 `--shot`/`--montage` 等也请把输出路径指到 `test_temp/` 下，别散在仓库根。

### 2. 预览窗格 COM handler（资源管理器里选中 .litematic 出预览）

不需要单独构建，出安装包时一并编好（AOT x64 原生 dll）。

### 3. 安装包（一键脚本）

```
python tools/build_installer.py
产物：dist/LitematicaViewer.Setup.exe
```

脚本做两步：先 publish ShellPreview 到 dist/ShellPreview（AOT 原生 dll + av_libglesv2.dll），
再 publish Setup（把这些文件连同 packs/vanilla-1.20.1.jar 一起嵌进单文件安装器）。
两步必须分开跑——同图并发 publish 会抢公共引用的 obj 把 pdb 锁死。

安装包里嵌死了三样东西：handler dll、ANGLE 库（av_libglesv2.dll）、原版 1.20.1 jar。
装出来就是全功能的，不需要用户再自备资源。

## 安装 / 卸载

```
LitematicaViewer.Setup.exe            （交互式，确认后自动重启资源管理器）
LitematicaViewer.Setup.exe /quiet     （静默装，同样自动重启资源管理器）
LitematicaViewer.Setup.exe /uninstall （卸载，从「设置-应用」进来的也是这个入口）
```

- 按用户安装（HKCU + %LocalAppData%，不需要管理员）。
- 安装位置：%LocalAppData%\LitematicaViewer\Preview\
- 安装/卸载都会先杀 prevhost（共享预览代理宿主锁着 dll，不杀覆盖写/删目录必炸），
  完成后自动重启资源管理器刷新 shell 的 handler 状态。
- 只注册预览，不绑定双击打开方式；.litematic 双击仍是系统「选择打开方式」。
- 排查日志：%Temp%\lvsetup.log（安装器）、%LocalAppData%\LitematicaViewer\Preview\shellpreview.log（handler）。

批量验证用 dist/deploy_setup.py：卸旧 -> 装新 -> 回读注册表校验。

## 资源包（packs/）怎么加

三处查找位置，规则相同：

| 运行形态         | 查找位置                                          |
|------------------|---------------------------------------------------|
| SamplePreviewer  | exe 目录向上最多七层的 packs/（仓库开发时命中根目录） |
| 安装版预览窗格   | %LocalAppData%\LitematicaViewer\Preview\packs\     |
| 仓库内冒烟工具   | 显式传参（MeshSmoke/PackSmoke 的最后一个参数）      |

目录里放什么：

1. **原版 jar（必需，栈底）**：vanilla-1.20.1.jar 之类。安装版已经内置，仓库开发把 jar 丢进根目录 packs/ 即可。
2. **材质包（可选，覆盖原版）**：文件夹或 zip/jar 都行。

入栈顺序是硬规则：**先所有 .jar（字典序），再其余（文件夹/zip，字典序）**，后入栈的覆盖先入栈的。
不能靠整体字典序——"XK红石显示包" 按名字会排在 "vanilla" 前面，把原版压到底、覆盖失效且不报错。

加材质包的步骤（两种形态一样）：把包文件/文件夹丢进对应 packs/，重开预览（安装版换文件或重选即可，
不用重装；Sample 重启程序）。

## 已知边界

- piston_head 等方块若写 litematic 时没带全属性（如缺 type），会按「通配兜底」取第一个命中的
  variant 渲染（通常是原版默认值）；litematica 本体导出的文件属性总是齐全的，不受影响。
- bare id（完全无属性）的 multipart 方块（墙/栅栏）连接臂匹配仍按原版默认值语义待修，
  真实文件都带属性，暂不咬人。
