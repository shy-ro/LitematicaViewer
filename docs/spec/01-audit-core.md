# 01 Core 审计（Phase A）

用 Poly.NBT 的 DOM 路径读取 `.litematic`，产出不可变的 `LitematicDocument`。
本阶段不涉及任何渲染，Core 不知道 Previewer 存在。

## 公开面

    LitematicLoader.TryLoadFile(path)              -> LoadResult
    LitematicLoader.TryLoad(bytes, sourcePath)     -> LoadResult
    LitematicParser.ParseRaw(bytes)                -> LitematicParseResult
    LitematicParser.ToDomain(result, sourcePath)   -> LitematicDocument
    BlockStatesCodec.Unpack(data, count, paletteSize) -> ImmutableArray<int>
    BlockStatesCodec.Pack(indices, paletteSize)       -> long[]
    BlockStatesCodec.GetBitsPerBlock(paletteSize)     -> int
    BlockStatesCodec.GetPackedLongCount(count, paletteSize) -> int

`LoadResult` 是返回值而不是异常：调用方要给用户看一个文件，路径写错和文件损坏都只是
"这个文件打不开"。`Error` 取值 `FileNotFound` / `IoFailure` / `NotNbt` / `Truncated` /
`Malformed` / `RegionDecodeFailed`。区域级的损坏进 `Issues`，不影响整体加载。

`LitematicDocument` 暴露 `SourcePath`、`Metadata`、`Regions`、`Bounds`、`TotalBlocks`、`TotalVolume`。
`LitematicRegion` 暴露 `Name`、`Position`、`Size`、`Bounds`、`Palette`、`BlockIndices`、
`GetState(world)`、`ToIndex`、`ToLocal`、`CountNonAirBlocks`。

## 依赖

`Poly.NBT` 0.1.0 + `PolyType` 1.4.1，走 NuGet `PackageReference`。
不引项目引用，不用本机那份 `C:/dotnet_libprcmt/Poly.NBT` 克隆。

Poly.NBT 的唯一目标是 `net10.0`，与本仓库一致。它自带零压缩支持：`gzip` / `zlib` /
`Deflate` 在它的程序集里零出现，所以 `.litematic` 的 gzip 外壳由 `NbtContainerReader` 自己解。
按首字节嗅探 gzip / zlib / 裸 NBT，zlib 用 FCHECK 判定（首字节低半字节为 8 且前两字节大端
值是 31 的倍数），不枚举 `78 01` / `78 9C` / `78 DA`。

## 实测的格式事实

数据来源：`C:\Users\ssp47\Downloads\2026.9.5投影文件新更新(499文件)`，499 个 `.litematic`，
全部由 Litemapy 0.11.0b0 生成。抽查了最小的 779 字节、10 KB 与 311 KB 三个文件。

- 根 tag 是 Compound，**名字是空字符串**。不是 `"Schematic"`。所以类型判定只能靠结构字段
  （`Regions` 是否存在），不能靠 `RootTagName`。
- 根字段：`Version`(=6)、`SubVersion`(=1)、`MinecraftDataVersion`(=3465)、`Metadata`、`Regions`。
- `Metadata`：`EnclosingSize{x,y,z}`、`Author`、`Description`、`Name`、`Software`、
  `RegionCount`、`TimeCreated`、`TimeModified`、`TotalBlocks`、`TotalVolume`、`PreviewImageData`。
- `Regions` 是 Compound，键是区域名（实测都是 `Unnamed`），值含：
  `Position{x,y,z}`、`Size{x,y,z}`、`BlockStatePalette`(List<Compound>)、`Entities`、
  `TileEntities`、`PendingBlockTicks`、`PendingFluidTicks`、`BlockStates`(LongArray)。
  后四个本阶段不解，但要确认存在且不被当作未知字段。
- `Size` **可以为负**。实测 `Position(4,2,8)` 配 `Size(-5,-3,-9)`，体积是 135 = 5×3×9。
  约定是 Size 表示两个角之差且上界排他，所以实际跨度是 `|Size|`，
  包围盒的下角是 `min(Position, Position+Size)`。`Metadata.EnclosingSize` 等于 `|Size|` 也印证这点。
- 调色板元素是 Compound `{Name, Properties?}`，`Properties` 的值为字符串。

这一批文件字段齐整，可选字段并不缺失。但仍走 DOM 加默认值：类型化路径把缺席的
构造参数当成必填，换个来源的文件就会直接抛 `Required constructor arguments are missing`。

## 位解包：必须用紧凑位流

这是本次唯一会静默出错的地方。`.litematic` 把调色板下标写进一串 long，第 i 个方块占
**位流的第 `i*bits` 到 `i*bits+bits-1` 位**，允许跨 long 边界。

另一种常见写法是"每个 long 塞 `64/bits` 个"，在这种排法下跨界分支根本不会被执行
（`perLong*bits <= 64` 恒成立）。两种排法在 bits 整除 64 时结果完全相同，只在调色板稍大时
分道扬镳，而且不报错、只是颜色整体错位。

用文件里 `BlockStates` 的实际长度判定，两者差得很清楚：

| 文件 | 调色板 | 位宽 | 方块数 | 紧凑位流 | 每 long 塞 64/bits 个 | 文件实际 |
|---|---|---|---|---|---|---|
| 水上树屋 | 31 | 5 | 126225 | 9862 | 10519 | **9862** |
| 全部的热气球 | 459 | 9 | 3498908 | 492034 | 499844 | **492034** |

`CoreSmoke` 里 `CheckPackedLongCountPins` 把这两组数字钉成了回归。

位宽：`paletteSize <= 1` 为 0 位；否则 `max(2, ceil(log2(paletteSize)))`，下限是 2 不是 1。

## 验收结果

Debug 配置下的 Core 是可执行程序，直接在终端跑：

    dotnet run --project src/LitematicaViewer.Core -- <文件路径...>

`Tests/CoreSmoke.cs` 里 71 项检查，代码内断言在夹具上的不变量，再对传入的每个真实文件
比对头部声明。三个真实文件的结果：

| 文件 | 区域 | 体积 | 重算非空气方块 | 头部声明 |
|---|---|---|---|---|
| 1959款法拉利250GT | 1 | 135 | 76 | 76 |
| 水上树屋 | 1 | 126225 | 13563 | 13563 |
| 全部的热气球 | 1 | 3498908 | 349941 | 349941 |

重算值与头部声明逐项相等。这是位解包、负 Size 归一化、空气集合三件事同时正确的强证据：
任何一处错了，方块数都会偏，而且不会以异常的形式暴露。

`TotalBlocks` 对外只暴露重算值。头部那份也读进来放进 `Metadata`，差异只报不判——
将来遇到别的工具生成的文件，两者可能不等。

## 调试桩

统一格式 `[CORE][<阶段>] 变量=值 expected=期望`，直接调 `Debug.WriteLine`，
不套 `#if DEBUG`（该 API 自带 `[Conditional("DEBUG")]`）。桩保留，不删。

    container        文件头 16 字节、判定的压缩方式、解压前后字节数
    parse.root       根 tag 名与顶层键列表
    parse.metadata   version / subVersion / dataVersion / 头部声明的区域数与方块数
    parse.region     位置、带符号 Size、绝对尺寸、包围盒、体积、调色板数、位宽、
                     BlockStates 实际长度 vs 需要长度、以及四个本阶段不解的列表的长度
    codec.unpack     count / paletteSize / bits / longs / 跨界次数 / 截断尾长
    document         总区域数、整体包围盒、重算方块数 vs 头部声明
    document.region  每个区域的体积、索引数、非空气数、越出调色板的下标数
    load             失败时的错误分类与消息

`CodeSmoke.Main` 挂了 `TextWriterTraceListener(Console.Out)`。不挂的话 `Debug.WriteLine`
只写进调试器的输出窗口，从终端跑什么都看不到。

## 已知取舍

- `GetState` 在区域外、下标越出 `BlockIndices`、或下标越出 `Palette` 时一律回退到
  `palette[0]`。三个越界原因混在一起是刻意的：对渲染方来说都只是"这里没有可信的方块"。
  代价是如果某天 palette[0] 不是空气，越界会静默变成一层薄壳。`CountOutOfPaletteIndices`
  单独把第三种情况暴露出来，桩里报 0。
- `LitematicParser` 里那个 `NbtSerializer` 是静态共享的。它不是线程安全的保证对象，
  当前只在单线程加载路径上复用。R3 禁止 lock，将来真并发加载要在调用方各建实例。
- 一个区域的体积超过 `int.MaxValue` 时直接跳过并记 `Issues`：索引类型是 `int`。
  本轮 499 个文件的区域体积都远小于此。
- `NbtSerializer` 的预设 `NbtOptions.JavaEdition` 是**静态只读字段**而不是属性，
  与常见文档描述不同。
- 仓库根下的 `tools/*.py` 是本机用来反查格式的临时脚本，已 gitignore，不进仓库。

## 未解决

- `Size` 为负时包围盒下角取 `min(Position, Position+Size)`，这个结论是由
  `EnclosingSize == |Size|` 与方块数吻合反推的，没有对照 Litematica 官方实现或渲染结果
  验证过。等 Phase C 能画出方块之后，用负 Size 的文件目视确认一次。
- 区域里的 `Entities` / `TileEntities` / `PendingBlockTicks` / `PendingFluidTicks` 只统计长度，
  不解析内容。预览器当前不需要它们。
