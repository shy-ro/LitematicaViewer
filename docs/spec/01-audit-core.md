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

    VoxelPicker.TryPick(document, origin, direction, maxDistance) -> bool + VoxelHit
    VoxelPicker.TryPick(region, origin, direction, maxDistance)   -> bool + VoxelHit
    VoxelFaces.NormalOf(face)                                     -> Vector3I

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

## 拾取

射线与体素网格求交放在 Core，因为它不需要 GL、也不需要相机。整条链路是两半：

    屏幕上的点 ──[相机：Position/Yaw/Pitch/Fov]──> 世界空间射线 ──[Core]──> 方块 + 面 + 距离

右半在这里，左半在 Previewer，依赖 `CameraState`（Phase D 才定义）。所以本阶段
Previewer 侧的鼠标拾取还接不上，Core 侧已经可以直接调。

`VoxelPicker.TryPick` 给 document 时取多区域里最近的那个，命中区域也一并返回；
给 region 时只走那一个。方向不必预先归一化，`Distance` 的语义是沿射线的世界单位长度。

用 Amanatides & Woo 逐格步进，不用等步长采样：

- 步长给小了，350 万体积的区域上每次点击要走几百万步。
- 步长给大了，在格子角上掠射时会整格跳过去，而跳过的那一格表现为"明明点到方块却说没命中"。
- 一格被完整穿过时，参数长度至少是 1（要离开一格必须整面跨出去）。这是逐格步进的
  终止性来源，也是"等步长采样漏掉的一定是被截出来的碎片，不会是完整的一格"这句话的依据。

### 实测的浮点事实

`VoxelHit.BlockPosition` 来自整数步进，精确。`VoxelHit.Distance` 不然：它是
`entry` 加上一串 `tDelta` 累加出来的，实测在 10～20 的坐标量级下漂移有 1e-4。
拿 `Distance` 重算命中点，会落在格子边界的任意一侧。要判"这一格是不是在射线经过的位置上"，
得取命中点前后各 1e-3 的窗口，不能只往一侧取样。

进入点落在格子边界上时要沿射线方向挪 1e-4 再 `floor`。轴对齐射线的进入点必然落在边界上，
不挪就会挑到盒外那一格——而轴对齐恰好是 Litematica 里最常见的射线。

### 与统计口径对齐

越出调色板的下标在拾取里当空气跳过，跟 `CountNonAirBlocks` 一致。换成 `GetStateAt`
会回退到 `palette[0]`，于是"统计说这里没有方块"的地方反而能拾取出一个方块，两边对不上账。
这是本阶段第二次遇到同一个回退带来的口径分歧，`GetState` 那个宽松回退保留给渲染用。

### 面的朝向

`VoxelFace` 的顺序照抄 Minecraft 的 `Direction` 并整体后移一位给 `None` 让位，
将来按面取方块模型贴图可以直接用 `(byte)Face - 1`。North 是 -Z，与直觉相反。
射线起点在方块内部时 `Face` 是 `None`，不是硬凑一个方向——凑出来的方向会让
`AdjacentPosition` 返回一个并不相邻的坐标。

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

`Tests/CoreSmoke.cs` 里 100 项夹具检查，再对传入的每个真实文件比对头部声明与拾取。
三个真实文件的结果：

| 文件 | 区域 | 体积 | 重算非空气方块 | 头部声明 | 瞄准首个非空气方块拾取 |
|---|---|---|---|---|---|
| 1959款法拉利250GT | 1 | 135 | 76 | 76 | (-1,-1,-1) grass_block |
| 水上树屋 | 1 | 126225 | 13563 | 13563 | (0,0,0) water |
| 全部的热气球 | 1 | 3498908 | 349941 | 349941 | (6,0,8) chain |

重算值与头部声明逐项相等。这是位解包、负 Size 归一化、空气集合三件事同时正确的强证据：
任何一处错了，方块数都会偏，而且不会以异常的形式暴露。

拾取另有一个独立对拍基准 `Tests/PickingReference.cs`：沿射线等步长采样，用
`Bounds.Contains` + `GetState` 判断，与逐格步进除了"射线是哪条"之外不共享任何逻辑。
8×6×4 的稀疏夹具上 128 条随机射线，逐格步进与基准给出同一格、距离差在一个采样步长内。
三个真实文件上再各瞄一个已知存在的方块横向打进去，两边同样给出同一格。

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
    pick.hit         区域名、方块坐标、进入面、距离、步数、调色板下标、方块状态
    pick.miss        跳过该区域的原因（射线不合法 / 区域尺寸退化 / 盒外 / 走出盒外）与步数
    pick.document    区域数、是否命中、最终距离——多区域时用来确认取的是最近的
    pick.real        真实文件上瞄的那一格与实际命中的那一格，两者不等时说明中间有遮挡
    pick.cross.FAIL  对拍不一致的两组坐标与距离，附射线起点与方向

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
- `VoxelHit.Distance` 有 1e-4 量级的浮点漂移（累加出来的），`BlockPosition` 是精确的。
  要用距离就得记住这一点：拿它当坐标反算会落在格子边界的任意一侧。
- 拾取每次调用都会新建 `Vector3` 临时量并按区域重新归一化方向，没有做批量化。
  一次点击几微秒，不值得为它引一套射线批处理。
- 仓库根下的 `tools/*.py` 是本机用来反查格式的临时脚本，已 gitignore，不进仓库。

## 未解决

- `Size` 为负时包围盒下角取 `min(Position, Position+Size)`，这个结论是由
  `EnclosingSize == |Size|` 与方块数吻合反推的，没有对照 Litematica 官方实现或渲染结果
  验证过。等 Phase C 能画出方块之后，用负 Size 的文件目视确认一次。
- 区域里的 `Entities` / `TileEntities` / `PendingBlockTicks` / `PendingFluidTicks` 只统计长度，
  不解析内容。预览器当前不需要它们。
- 拾取只有 CPU 那一半。屏幕上的点换成世界空间射线要相机参数，而 `CameraState` 要到
  Phase D 才定义，所以鼠标拾取在 Previewer 侧还接不上，也没有验证过。等 Phase D 之后
  用一条已知的相机姿态反推，确认射线方向与目视一致。
- 拾取只认"第一个非空气方块"。水、玻璃这类非固体方块当前一律算实心，点到水面会停在水上。
  等中间层定了哪些方块要参与渲染，再决定拾取要不要跟着那套规则走。
