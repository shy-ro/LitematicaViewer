# 实体与特殊贴图渲染计划

> 状态：已实施（2026-09-30）。告示牌双面文字、字体 2× 烘焙、画、盔甲架、
> 矿车、掉落物、船/木筏与常见静态生物、箱子/潜影盒/铁轨回归矩阵和结构化审计均已接入；Sample、
> ShellPreview 与 regionrender 共享同一条 generated sprite 收集链。

## 目标

覆盖告示牌、盔甲架、潜影盒、箱子、铁轨、画，以及所有依赖实体贴图或方块实体 NBT 的静态投影模型；同时解决告示牌文字发糊、贴图错位、朝向错误和实体数据丢失问题。

## 现状审计与已知风险

1. Core 已解析 `TileEntities` 与 `Entities`，但特殊层只处理 sign/banner/decorated_pot、item frame、armor stand；画（painting）没有生成几何，盔甲架仅是简化盒件。
2. `SpecialBlockModels` 对箱子、潜影盒、床等使用静态回退盒，箱子/潜影盒不读取方块实体动画或朝向 NBT。
3. 告示牌文字被烘焙为 96×48 贴图，再经过 atlas mipmap；小字号与三线性采样会造成截图中的模糊和黑边。
4. `EmitSignText` 目前只按普通立牌平面处理，墙牌/吊牌的支撑几何与背面文字不完整；实体朝向、ItemFrame Facing、ArmorStand Rotation/Pose 需要统一打点。
5. 铁轨当前依赖资源包 blockstate/model；需要专门验证 shape、ascending、waterlogged、材质包覆盖及 UV，避免把普通模型问题误判为实体层问题。

## 实施阶段

### A. 一次性诊断

- 在 CollectSprites、BuildRegion、EmitBlockEntityOverlays、EmitEntities、Atlas.Build 和 GL Load 增加带坐标、ID、状态、NBT 字段、sprite rect、顶点/面数的结构化日志。
- 新增 `--entity-audit <litematic> <packs...>`，按类别输出数量、缺失资源、模型命中、UV 越界、几何包围盒和跳过原因。

### B. 字体与告示牌

- 字体 atlas 改为独立高分辨率生成图，保留 bitmap provider 的原始像素，不提前压缩；文字 quad 使用半像素内缩并关闭不必要的 mip 采样。
- 统一普通牌、墙牌、吊牌的朝向和支撑几何；支持 front/back 文本、颜色与 glowing。

### C. 实体与特殊模型

- 画：读取 `Motive`、Facing、尺寸贴图，生成带边框的定向 quad。
- 盔甲架：补齐双臂、双腿、头部、姿态和 yaw，校准 pivot 与缩放。
- 箱子/潜影盒：校准原版展开图、朝向、双箱拼接、染色贴图与 NBT 动画静态投影。
- 铁轨：建立形状/坡道/弯道专项回归矩阵，确认材质包覆盖后仍有完整几何。

### D. 回归与交付

- 对真实 litematic 运行 audit、Montage、Preview 三条链路；日志标记每类实体的“数据→资源→几何→提交”状态。
- `dotnet build`、Core/Assets/Meshing smoke、目标文件专项审计全部通过后，再构建安装版 Preview Handler。

## 验收标准

- 告示牌中文笔画清晰，四行和多字体不出现明显糊边；
- 画、盔甲架、箱子、潜影盒、铁轨在不同朝向/材质包下几何完整；
- 审计日志能直接指出缺失数据、缺失 sprite、UV 越界或零面模型，而不是只显示最终“消失”。

## 实施验证

- 2026-10-01 告示牌复核：牌面/支柱原先整张贴图糊到每个面（64x32 展开图被拉平重影），
  改为逐面展开 uv：数字取自原版 SignModel（板 24x12x2 texOffs(0,0)、柱 2x14x2 texOffs(0,14)）
  ×2/3 缩放后落到方块局部像素，几何与朝向集中在 `Assets/SignGeometry.cs`，Meshing 的文字层
  贴同一块牌面（墙牌的贴墙偏移只写一遍）。顺带修掉 facing 型朝向反侧：南北向自逆掩盖了这个号。
  回归：MeshSmoke 12 项（新增 rotation=0/4/8 与墙牌 facing=east 的包围盒断言）；
  真实 litematic 四朝向渲染查图正常。

- 2026-10-01 箱子复核（接缝黑线）：双箱中央的细黑线来自 Minecraft `normal_left` /
  `normal_right` 实体贴图为防止相邻纹素渗色保留的边界，不是网格裂缝。保留当前
  `left/right` 专用贴图与已确认 UV，不再通过扩大几何、删除连接面或修改采样边界消除。
  这一条只解释了「黑线为什么在」，没有解释「透明段为什么跑到外缘」，见下条。

- 2026-10-01 实体 cuboid 侧面段序复核（双箱空洞的真正根因）：`EntityCuboid` 的侧面条带
  原写成 East–North–West–South，与原版 `ModelPart.Cube` 的 **West–North–East–South**
  是东西镜像——面名不变、采样段整体错位。单箱四段几乎同色看不出来，半张贴图会把
  「透明接缝段」推到外缘，双箱就是一块空洞。三个互相独立的锚点钉死这条段序：
  ① `normal_left` 第一段（West）/ `normal_right` 第三段（East）透明，只能定东西；
  ② 26.3 `template_sign_rot_0` 正面（south）贴图在 1.20.1 实体图里逐像素命中 (28,2)，
  即第 4 段=正面，排除「第 2 段是正面」的写法；
  ③ 陷阱箱 `trapped.png` 与 `normal.png` 逐像素 diff 只有 28 个点、红标记集中在
  x47–50（第 4 段），而官方说明是「闩四周泛红」。
  连带修正：`Chest` 半箱盒子改成 left=x0..15 / right=x1..16（接缝朝里），删掉 `mirrorU`
  补丁；`PreviewBlocks.RunContainerMontage` 的双箱摆位改回原版规则
  `leftAtHigh: facing is "south" or "west"`——摆位和模型互为镜像时测试矩阵会「看着连得上」，
  把 bug 盖住。
  回归：`--chest-dump` 的 12 格（4 单箱 + 4 朝向 × left@low/left@high）里，空洞**只**出现在
  摆位与模型不一致的那 4 格（south/west 用 left@low、north/east 用 left@high），
  按原版规则摆的 4 格全部密封 ⇒ 真文件（原版摆位）双箱成整盖、锁落接缝正中；
  两套栈全量 montage 逐格差分（`v1201`→`g1201`、`v263`→`g263`）变化面精确落在
  箱子族 / 潜影盒 / 告示牌三族，其他方块零变化；PackSmoke 新增 `CheckChestHalvesSeam`
  断言四个侧面的 uv 段号与盒缘。

- 2026-10-01 箱子二轮复核（盖的 texOffs 与顶/底格序）：上一轮只修了侧面的段序，盖和顶/底面还错两处。
  ① 原版 `ModelPart.Cube` 的顶/底面是 **Down 占第一格、Up 占第二格**（26.3 未混淆字节码实算
  `DOWN=(u+dz,v,u+dz+dx,v+dz)`、`UP=(u+dz+dx,v+dz,u+dz+2dx,v)`，侧面 `WEST/NORTH/EAST/SOUTH`
  与代码一致），我们原先写反了；② `Chest` 的盖沿用了箱身的 `texOffs(0,19)`，而贴图里盖的侧条带
  只有 5 行（v14..19）、箱身的 10 行（v33..43），传 v=19 会让盖的侧面去采箱身条带的顶部 5 行；
  盖应传 v=0，盖 y 9..14、闩 y 7..11（原 10..15 / 8..12，整体高 1px，且盖底与箱身顶完全共面）。
  修法：`EntityCuboid` 与 `UnwrappedBoxElement` 的 Down/Up 换回原版顺序，`Chest` 三处（single/left/right）
  改 texOffs 与 y。
  验证手法（这一轮定下来的判据）：**判漏光要数「轮廓内部的背景色像素」**——逐行取最左/最右前景、
  数中间等于背景色的点；「近黑像素」不行，箱内暗部本身就是纯黑贴图。实测 4 个 `single` 朝向与
  按原版规则摆的 4 个 `double` 全为 0，反摆的 4 个各 444..460（接缝两面透明、缺口朝外）；
  等价判据是世界包围盒是否连续 `1..31`（反摆成 `0..32`，中间空 2px）。可控 A/B（只差这三处改动的
  `test_temp/_chest/prefix` vs `postfix`）显示差异集中在盖的侧面条带与盖身交界线位置；盖顶/底观感
  几乎不变，之前记的「顶面碎点噪点」是缩略大图的降采样锯齿，已在坑日志里划掉。

- `dotnet build`：0 warnings / 0 errors。
- Assets smoke：15 项；覆盖 17 色潜影盒、12 种箱子朝向/类型、双箱接缝段序、28 种铁轨状态、
  XK 红石 multipart 叠层与批量真实方块。
- Meshing smoke：12 项；覆盖告示牌前后文字、4×3 画、盔甲架姿态与法线、
  掉落物、船/木筏和常见静态生物。
- 实体语料扫描：624 个 `.litematic` 全部解析成功；新增掉落物、船/木筏、村民、
  溺尸、铁/雪傀儡、猪灵、潜影贝、猪和蝙蝠的可辨认静态模型。
- 真实文件审计：`50倍速单区块熔炉组2604.litematic` 为 8,258 面，
  `specialSkipped=0`、`specialMissingSprites=0`；两辆漏斗矿车已生成静态网格。
