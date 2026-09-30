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
build_installer.py                 一键出安装包（`python build_installer.py`，产物 dist/LitematicaViewer.Setup.exe）
tools/                             辅助脚本（生成测试 litematic、覆盖审计等，均在本机跑）
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
python build_installer.py
产物：dist/LitematicaViewer.Setup.exe
```

前置：dotnet SDK（global.json 钉 10.0.401）、NativeAOT 需要的 MSVC + Windows SDK
（csproj 里写死了 Windows Kits 10.0.26100.0 的 um/ucrt 库路径）、`packs/vanilla-1.20.1.jar`
（Setup 会把它嵌进去，缺了装出来是没资源的空壳）。脚本本身无参数，跑完打印产物大小。

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

## 命令行工具（开发 / 排查用）

两个冒烟 exe 自带子命令，产物一律落 `test_temp/`（见上文约定），用完即弃。

### Meshing.exe（`src/LitematicaViewer.Meshing/bin/<配置>/net10.0/`）

不带开关直接跑 = 默认回归：

```
LitematicaViewer.Meshing.exe <资源包...> <.litematic...>
```

按扩展名分流（`.litematic` 归待查文件，其余当资源包），跑一组硬编码不变量检查
（CheckSingleBlock / CheckFaceCulling / CheckAmbientOcclusion / CheckLogAxisRotation /
CheckFluidRules），再对每个 .litematic 跑量级+耗时检查，结束打印 `checks=N`。
必须传资源包，否则图集空、断言全崩。

| 子命令 | 用法 | 说明 |
|---|---|---|
| `--resolve` | `--resolve <状态串> <资源包...>` | 打印状态串命中的 variant / model id / 旋转角 / box 数面数 / 各 box 的 from-to。 |
| `--preview` | `--preview <outDir> <资源包...>` | 逐状态出三面等距散图（192×192，文件名 = 状态键），另落 `_atlas_L*.png`、`_sprites.txt`、`_report.txt`。 |
| `--montage` | `--montage <out.png> [开关...] <资源包...>` | 同一条 Render 链路拼网格大图，快速人检某类方块（开关见下表）。 |
| `--probe` | `--probe <.litematic> <资源包...>` | 逐坐标打印非空气状态串，给「渲染器和第三方解码器逐坐标对账」当硬证据。 |
| `--regionrender` | `--regionrender <out.png> <.litematic> [--size N] <资源包...>` | 真实 region 软件光栅化，与 GPU 截帧对照（level0 NEAREST、无 mipmap、无混合）。边长默认 768，下限 128；多 region 时第 2 个起落 `_r1`、`_r2`… |
| `--dumpverts` | `--dumpverts <.litematic> <资源包...>` | 打印头几个面的顶点原始数据（pos/normal/uv/tint），查面退化。 |
| `--uvhist` | `--uvhist <.litematic> <资源包...>` | 逐面取 uv 中心，统计落在各 sprite rect 内的面数，查 uv 映射错位。 |

资源包参数一律 jar/zip/文件夹，顺序 = 从栈底到栈顶，后面压前面的。

`--montage` 的开关**不分先后、全部可省**：

| 开关 | 取值 | 默认 | 说明 |
|---|---|---|---|
| `--names` | 无 | 关 | 每格左侧让出标签列写状态键（系统字体、纯黄字、按列宽断行不截断，全名另在 `.txt` 索引里）。 |
| `--nonames` | 无 | — | 显式关闭，容错成对写法。 |
| `--nodup` | 无 | 关 | 关掉「模型 + 旋转」去重，每个解得出画面的状态各占一格，索引无缺口（格数约翻倍）。 |
| `--gap` | N | 0 | 格间距像素，钳 0..64。格子底色与整图同色（40,40,40），0 即无缝无边框。 |
| `--filter` | S | 空 | 对状态键做 `OrdinalContains` 过滤（`iron` 只看铁系），默认不过滤。 |
| `--cell` | N | 128 | 每格像素边长，下限 32。取景按包围盒自适应，改它只改清晰度。 |
| `--chunk` | N | 0 | 每块格数：>0 时每满 N 格落盘一张再继续（第 1 张原名，之后 `_2`、`_3`…），0 = 不分块铺一张。 |

产物：`out.png`、同名 `.txt`（索引「序号: 状态键」，序号跨块全局连续）、输出目录下
`_report.txt` 与 `_sprites.txt`。（`--preview` 与 `--montage` 共用同一目录，会互相覆盖
`_report.txt`、`_sprites.txt`。）

例：全量出一份、每 2000 格一块、带标签、不去重：

```
LitematicaViewer.Meshing.exe --montage test_temp/montage/full.png --names --nodup --cell 128 --chunk 2000 packs_hold/vanilla-26.3.jar
```

旧的位置写法 `[过滤子串] [格边长] [每块格数]` 仍兼容（`--regionrender` 的 `[边长]` 同理），
但过滤位必须拿空串占位，而 PowerShell 会把 `""` 整个吞掉、参数整体左移一格（`128` 顶到过滤位），
所以一律改用上面的具名开关。

### Assets.exe（PackSmoke）

```
LitematicaViewer.Assets.exe <资源包...>                    # 默认回归
LitematicaViewer.Assets.exe --scan <原版包> [覆盖包...]     # 全量扫 blockstate 体检
```

### 不在 Meshing.exe 的开关

- `--shot <png>` / `--shot-dist <系数>`：Previewer.Sample 与 SamplePreviewer 的 GL 帧读回落盘。
- `--selftest <秒>`：只在 Previewer.Sample，自动开窗跑交互剧本后关窗。

## 已知边界

- piston_head 等方块若写 litematic 时没带全属性（如缺 type），会按「通配兜底」取第一个命中的
  variant 渲染（通常是原版默认值）；litematica 本体导出的文件属性总是齐全的，不受影响。
- bare id（完全无属性）的 multipart 方块（墙/栅栏）连接臂匹配仍按原版默认值语义待修，
  真实文件都带属性，暂不咬人。
