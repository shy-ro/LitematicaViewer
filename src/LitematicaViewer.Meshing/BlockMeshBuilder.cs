using System.Diagnostics;
using System.Numerics;
using LitematicaViewer.Assets;
using LitematicaViewer.Assets.Model;
using LitematicaViewer.Core.Model;

namespace LitematicaViewer.Meshing;

// Document → 网格。邻居是空气才出面（区域外算空气，外表面照常出），
// 每个面的几何与 uv 从资产层解析出的 element 拿，variant 的 x/y 旋转与
// element 的自身旋转在这里变成矩阵。
public sealed partial class BlockMeshBuilder
{
    // id 里含这些片段的不遮挡邻居。模型判据把它们认成整方块（cube_all），只能按 id 排除。
    private static readonly string[] NonOccludingMarkers =
    [
        "glass", "leaves", "ice", "slime_block", "honey_block", "tinted_", "barrier", "sea_lantern"
    ];

    private readonly TextureAtlas _atlas;
    private readonly Dictionary<string, CachedState> _cache = new(StringComparer.Ordinal);
    private readonly BlockStateResolver _resolver;
    private int _emptySpriteFaces;
    private FluidMesher? _fluid;

    private int _skippedFaces;

    public BlockMeshBuilder(BlockStateResolver resolver, TextureAtlas atlas)
    {
        _resolver = resolver;
        _atlas = atlas;
    }

    public string Stats => $"skippedFaces={_skippedFaces} emptySpriteFaces={_emptySpriteFaces}";

    // 调色板里实际用到的 sprite 全集。先收集再建图集，图集里就没有用不上的贴图。
    // 收 region 列表而不是 document：调用方（Sample）可能只网格化单个 region。
    //
    // 这里必须直走 resolver 而不是 GetState 的缓存：缓存里的四边形是在图集上算的
    // （uv 要映射进 sprite 落位），而图集要等这份收集做完才建得出来——
    // 先有蛋后有鸡的顺序反过来一次，sprite 全集就永远是空的。
    public void CollectSprites(IEnumerable<LitematicRegion> regions, HashSet<string> output,
        PackStack? packs = null, ICollection<GeneratedSprite>? generatedSprites = null)
    {
        HashSet<string> seenStates = new(StringComparer.Ordinal);
        foreach (var region in regions)
        foreach (var state in region.Palette)
        {
            if (state.IsAir || !seenStates.Add(state.ToString())) continue;

            // 流体本体不走模型路径（elements 为空，形状在 BuildRegion 现算），
            // 贴图在这里登记；waterlogged 宿主本体照常解析，但只有宿主没有
            // 裸水的调色板也得有水面贴图，补一次。
            if (FluidMesher.IsPlainFluid(state))
            {
                output.Add(FluidMaterial.SpriteFor(state.Name));
                continue;
            }

            if (FluidMesher.IsWaterlogged(state)) output.Add(FluidMaterial.SpriteFor("minecraft:water"));

            foreach (var variant in _resolver.Resolve(state.ToString()).Variants)
            foreach (var element in variant.Model.Elements)
            foreach (var face in element.Faces)
                if (face.Sprite.Length > 0)
                    output.Add(face.Sprite);
        }

        CollectSpecialSprites(regions, output, packs, generatedSprites);
    }

    public MeshData BuildRegion(LitematicRegion region)
    {
        var size = region.Bounds.Size;
        Debug.Assert(size.X > 0 && size.Y > 0 && size.Z > 0, $"[MESH] region {region.Name} 尺寸非正 {size}");

        // 调色板的空气标记一次备好：内层循环里查数组比查 HashSet 快一个量级，
        // 大模型（几百万体素 × 6 邻居）全靠这里。
        var airFlags = new bool[region.Palette.Length];
        var occludeFlags = new bool[region.Palette.Length];
        var waterFlags = new bool[region.Palette.Length];
        var waterLevels = new int[region.Palette.Length];
        var waterSprites = new string[region.Palette.Length];
        var waterTints = new float[region.Palette.Length];
        for (var i = 0; i < airFlags.Length; i++)
        {
            airFlags[i] = region.Palette[i].IsAir;
            occludeFlags[i] = !airFlags[i] && OccludesNeighbours(region.Palette[i].ToString());
            // waterlogged 宿主也标水：相邻水格共享连通标记才不会在交界上画两片
            // 重合的侧壁互相 z-fight。贴图与 tint 同样一次备齐。
            waterFlags[i] = !airFlags[i] && FluidMesher.IsWaterCell(region.Palette[i]);
            waterLevels[i] = FluidMesher.LevelOf(region.Palette[i]);
            waterSprites[i] = FluidMesher.IsPlainFluid(region.Palette[i])
                ? FluidMaterial.SpriteFor(region.Palette[i].Name)
                : FluidMaterial.SpriteFor("minecraft:water");
            // waterlogged 宿主的水面按水染色（宿主自己的 tint 与水无关）。
            waterTints[i] = FluidMesher.IsPlainFluid(region.Palette[i])
                ? TintSlotFor(region.Palette[i].ToString())
                : TintSlotFor("minecraft:water");
        }

        List<float> vertices = [];
        List<int> indices = [];
        var blocks = region.BlockIndices.ToArray();
        FluidMesher.World fluidWorld = new(waterFlags, waterLevels, occludeFlags, blocks, size);

        void EmitFluid(int cx, int cy, int cz, int paletteIdx, Vector3 at)
        {
            _fluid ??= new FluidMesher(_atlas);
            _fluid.EmitCell(vertices, indices, at, fluidWorld, cx, cy, cz,
                waterLevels[paletteIdx], waterSprites[paletteIdx], waterTints[paletteIdx]);
        }

        for (var y = 0; y < size.Y; y++)
        for (var z = 0; z < size.Z; z++)
        {
            var rowBase = (y * size.Z + z) * size.X;
            for (var x = 0; x < size.X; x++)
            {
                var index = rowBase + x;
                var paletteIndex = blocks[index];
                if ((uint)paletteIndex >= (uint)airFlags.Length || airFlags[paletteIndex])
                    // 越界下标按空气跳过，与 Core 的拾取/统计同一口径；
                    // 回退 palette[0] 的读法会让「统计说没方块」的地方长出方块。
                    continue;

                var state = region.Palette[paletteIndex];
                Vector3 origin = new(region.Bounds.Min.X + x, region.Bounds.Min.Y + y, region.Bounds.Min.Z + z);

                if (FluidMesher.IsPlainFluid(state))
                {
                    // 流体本体：模型 elements 是空的，形状按邻居现算，不进
                    // GetState 的模型缓存。
                    EmitFluid(x, y, z, paletteIndex, origin);
                    continue;
                }

                var cached = GetState(state);
                if (cached.Variants.Count == 0)
                {
                    // 没有模型的宿主也可能带水（罕见），水面照出。
                    if (waterFlags[paletteIndex]) EmitFluid(x, y, z, paletteIndex, origin);

                    continue;
                }

                // variants 型花色（stone 的 4 个旋转）按位置散列轮换，与 MC 的随机
                // 取法一样逐方块变化但确定性（同一文件每次载入画面一致）；
                // multipart 型全部部件一起画，不挑。
                IEnumerable<CachedVariant> chosen = cached.IsMultipart
                    ? cached.Variants
                    :
                    [
                        cached.Variants[
                            (int)(((uint)(x * 73856093) ^ ((uint)y * 19349663) ^ ((uint)z * 83492791)) %
                                  (uint)cached.Variants.Count)]
                    ];
                foreach (var variant in chosen)
                {
                    foreach (var quad in variant.Quads)
                    {
                        if (quad.Cullface is Vector3 cull
                            && IsNeighborOccluding(blocks, occludeFlags, size, x, y, z, cull))
                        {
                            _skippedFaces++;
                            continue;
                        }

                        if (quad.Sprite.Length == 0)
                        {
                            // 引用解不开的面直接丢（日志在资产层打过一次）；
                            // 补一块棋盘占位反而需要把它塞进图集，不值得。
                            _emptySpriteFaces++;
                            continue;
                        }

                        EmitQuad(vertices, indices, origin, quad, blocks, occludeFlags, size, x, y, z);
                    }

                    // waterlogged 宿主：本体已画，水面叠在宿主格里（vanilla 双层渲染）。
                    if (waterFlags[paletteIndex]) EmitFluid(x, y, z, paletteIndex, origin);
                }
            }
        }

        EmitBlockEntityOverlays(region, vertices, indices);
        EmitEntities(region, vertices, indices);

        Debug.WriteLine(
            $"[MESH][build] region={region.Name} blocks={region.CountNonAirBlocks()} " +
            $"verts={vertices.Count / MeshData.FloatsPerVertex} faces={indices.Count / 6}");
        return new MeshData([.. vertices], [.. indices]);
    }

    private CachedState GetState(BlockStateDefinition state)
    {
        // BlockStateDefinition.ToString() 已按 key 排序：同一状态在缓存里只有一份，
        // 不同进程里拼出的键也一致（这个保证写在 Core 那边的注释里）。
        var key = state.ToString();
        if (_cache.TryGetValue(key, out var cached)) return cached;

        // tint 只跟方块 id 与面的 tintindex 有关，与 variant 无关：整个状态一份就够。
        // 槽号在缓存时定死写进顶点，渲染侧按槽查固定色板，不再回头碰资产层。
        var tintSlot = TintSlotFor(key);
        var resolved = _resolver.Resolve(key);
        List<CachedVariant> variants = [];
        foreach (var variant in resolved.Variants)
        {
            List<CachedQuad> quads = [];
            foreach (var element in variant.Model.Elements)
            foreach (var face in element.Faces)
                if (BuildQuad(element, face, variant, face.TintIndex >= 0 ? tintSlot : 0f) is { } quad)
                    quads.Add(quad);

            variants.Add(new CachedVariant(quads));
        }

        CachedState result = new(resolved.IsMultipart, variants);
        _cache[key] = result;
        return result;
    }

    private CachedQuad? BuildQuad(ModelElement element, ElementFace face, ResolvedVariant variant, float tintSlot)
    {
        if (!TryGetRect(face.Sprite, out var rect))
        {
            // sprite 没进图集（例如带 tint 的覆盖图后来才收集）不该发生：
            // CollectSprites 与 Build 用同一个解析路径。真发生时按缺面处理并留痕。
            Debug.WriteLine($"[MESH][build] sprite 不在图集里 face={face.Sprite}");
            return null;
        }

        var from = element.From / 16f;
        var to = element.To / 16f;
        var (normal, r, d) = FaceBasis(face.Face);

        // 面四角：从「屏幕左上」出发，往右加 r、往下加 d。
        // r/d 是轴对齐单位向量（带符号），左上角按「沿 r 的负方向、沿 d 的负方向取到边」推。
        var nAxis = AxisOf(normal);
        var nSign = ComponentOf(normal, nAxis);
        Vector3 tl = new(
            Pick(from.X, to.X, 0, nSign),
            Pick(from.Y, to.Y, 1, nSign),
            Pick(from.Z, to.Z, 2, nSign));

        var rAxis = AxisOf(r);
        var rSign = ComponentOf(r, rAxis);
        tl = WithAxis(tl, rAxis, Pick(from, to, rAxis, -rSign));

        var dAxis = AxisOf(d);
        var dSign = ComponentOf(d, dAxis);
        tl = WithAxis(tl, dAxis, Pick(from, to, dAxis, -dSign));

        var rVec = r * Extent(from, to, rAxis);
        var dVec = d * Extent(from, to, dAxis);
        var a = tl;
        var b = tl + rVec;
        var c = tl + rVec + dVec;
        var e = tl + dVec;

        // element 自身旋转（big_dripleaf 的 45°），再叠 variant 的整体旋转（横放的原木）。
        // 顺序固定：先 element 后 variant；橡木原木 axis=x 的冒烟断言钉着这套约定。
        if (element.Rotation is { } rot)
        {
            var origin = rot.Origin / 16f;
            a = RotateAround(a, rot.Axis, rot.AngleDegrees, origin);
            b = RotateAround(b, rot.Axis, rot.AngleDegrees, origin);
            c = RotateAround(c, rot.Axis, rot.AngleDegrees, origin);
            e = RotateAround(e, rot.Axis, rot.AngleDegrees, origin);
            normal = Rotate(normal, rot.Axis, rot.AngleDegrees);
        }

        a = ApplyVariantRotation(a, variant);
        b = ApplyVariantRotation(b, variant);
        c = ApplyVariantRotation(c, variant);
        e = ApplyVariantRotation(e, variant);
        normal = RotateDirection(normal, variant);

        // uv：元素面 0..16 → 图集像素 → [0,1]。v 不翻转，理由见 MapUv。
        var uvA = MapUv(face.Uv.X, face.Uv.Y, rect);
        var uvB = MapUv(face.Uv.Z, face.Uv.Y, rect);
        var uvC = MapUv(face.Uv.Z, face.Uv.W, rect);
        var uvD = MapUv(face.Uv.X, face.Uv.W, rect);

        // 面级 uv rotation：MC 把贴图在面上顺时针转（观察面时）。活塞模板四个侧面
        // 全靠它把 piston_side 顶部的木带包到平台前缘；不转的话橙带躺倒顶边，
        // 看起来就像活塞头纹理长到了别的面上。角点整体轮转（uvA 原是贴图左上）：
        // 顺转 90° 后面的左上角采样自贴图左下，180° 来自右下，270° 来自右上。
        switch (((face.UvRotationDegrees % 360) + 360) % 360)
        {
            case 90:
                (uvA, uvB, uvC, uvD) = (uvD, uvA, uvB, uvC);
                break;
            case 180:
                (uvA, uvB, uvC, uvD) = (uvC, uvD, uvA, uvB);
                break;
            case 270:
                (uvA, uvB, uvC, uvD) = (uvB, uvC, uvD, uvA);
                break;
        }

        Vector3? cullface = face.Cullface is string dir && TryDirection(dir, out var cull) ? cull : null;

        // variant 的 x/y 旋转同样转 cullface：横放的原木（axis=x 的 variant 带 x=90），
        // 「up」面实际朝 ±X，剔除要查的是东西两侧的邻居。不转的话剔除查错了邻居，
        // 横放原木堆上会错删该画的面、留出本来该删的。
        if (cullface is { } cullDir && (variant.XDegrees != 0f || variant.YDegrees != 0f))
            cullface = RotateDirection(cullDir, variant);

        return new CachedQuad(a, b, c, e, normal, uvA, uvB, uvC, uvD, face.Sprite, cullface, tintSlot);
    }

    private Vector3 ApplyVariantRotation(Vector3 v, ResolvedVariant variant)
    {
        var result = v - new Vector3(0.5f);
        if (variant.XDegrees != 0f) result = Rotate(result, 0, variant.XDegrees);

        if (variant.YDegrees != 0f) result = Rotate(result, 1, variant.YDegrees);

        return result + new Vector3(0.5f);
    }

    // 方向量（法线）只旋转不平移：绕中心转的那个「减中心加中心」套在方向上，
    // 会把 (0,1,0) 转成 <±1,1,1> 这种长度 1.7 的鬼东西——实锤过一次。
    private static Vector3 RotateDirection(Vector3 v, ResolvedVariant variant)
    {
        var result = v;
        if (variant.XDegrees != 0f) result = Rotate(result, 0, variant.XDegrees);

        if (variant.YDegrees != 0f) result = Rotate(result, 1, variant.YDegrees);

        return result;
    }

    private static Vector3 RotateAround(Vector3 v, int axis, float degrees, Vector3 origin)
    {
        return Rotate(v - origin, axis, degrees) + origin;
    }

    // MC 的 variant/element 旋转角是「从轴正端看顺时针」（furnace facing=east 的
    // y=90 必须把北面转到东面；ladder facing=east 的 y=90 必须把贴面从南缘转到西缘），
    // 换成右手系向量数学就是取负角。正角会把 y=90/270 的方块镜像到格子的另一侧
    // （东西向梯子悬空、按钮贴错边），南北向（0/180）恰好自逆掩盖了半个世纪。
    // oak_log[axis=x] 的冒烟断言端面朝 ±X，对符号不敏感，改符号后仍然钉着顺序约定。
    private static Vector3 Rotate(Vector3 v, int axis, float degrees)
    {
        var radians = -degrees * MathF.PI / 180f;
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);
        return axis switch
        {
            0 => new Vector3(v.X, v.Y * cos - v.Z * sin, v.Y * sin + v.Z * cos),
            1 => new Vector3(v.X * cos + v.Z * sin, v.Y, -v.X * sin + v.Z * cos),
            2 => new Vector3(v.X * cos - v.Y * sin, v.X * sin + v.Y * cos, v.Z),
            _ => v
        };
    }

    // 面基：normal 朝外；r/d = 「从外面看这个面」的右与下。
    // 推导：观察方向 f = -normal，屏幕右 r = f × up，屏幕下 d = f × r。
    // 顶/底两面 up 取 ∓Z（俯视时屏幕上方是北），侧面一律取 +Y。
    private static (Vector3 Normal, Vector3 R, Vector3 D) FaceBasis(FaceName face)
    {
        var (normal, up) = face switch
        {
            FaceName.Down => (new Vector3(0, -1, 0), new Vector3(0, 0, 1)),
            FaceName.Up => (new Vector3(0, 1, 0), new Vector3(0, 0, -1)),
            FaceName.North => (new Vector3(0, 0, -1), new Vector3(0, 1, 0)),
            FaceName.South => (new Vector3(0, 0, 1), new Vector3(0, 1, 0)),
            FaceName.West => (new Vector3(-1, 0, 0), new Vector3(0, 1, 0)),
            FaceName.East => (new Vector3(1, 0, 0), new Vector3(0, 1, 0)),
            _ => (Vector3.UnitY, Vector3.UnitY)
        };

        var view = -normal;
        var r = Vector3.Cross(view, up);
        var d = Vector3.Cross(view, r);
        return (normal, r, d);
    }

    private static bool IsNeighborOccluding(int[] blocks, bool[] occludeFlags, Vector3I size, int x, int y, int z,
        Vector3 direction)
    {
        var nx = x + (int)direction.X;
        var ny = y + (int)direction.Y;
        var nz = z + (int)direction.Z;
        if (nx < 0 || ny < 0 || nz < 0 || nx >= size.X || ny >= size.Y || nz >= size.Z) return false; // 区域外算空气：外表面照常画

        var index = (ny * size.Z + nz) * size.X + nx;
        return (uint)index < (uint)blocks.Length
               && (uint)blocks[index] < (uint)occludeFlags.Length
               && occludeFlags[blocks[index]];
    }

    // 邻居要「完整不透明的方块」才有资格剔除贴着它的面。之前只要邻居非空气就剔除，
    // 栅栏、台阶、活板门、链这些非完整方块也把邻居的面删掉——热气球的吊篮（活板门 +
    // 台阶拼的）大片缺面就是这么来的。MC 的对应属性是 canOcclude，由方块代码决定，
    // 资产 JSON 里没有；这里用「模型是单个占满整格的元素」近似，再按 id 排除
    // 那些模型是整方块但不该遮挡的（玻璃、树叶这类透明/挖孔方块）。
    private bool OccludesNeighbours(string stateId)
    {
        var block = stateId;
        var bracket = stateId.IndexOf('[');
        if (bracket >= 0) block = stateId[..bracket];

        foreach (var marker in NonOccludingMarkers)
            if (block.Contains(marker, StringComparison.Ordinal))
                return false;

        var resolved = _resolver.Resolve(stateId);
        if (resolved.Variants.Count == 0) return false;

        foreach (var variant in resolved.Variants)
            if (variant.Model.Elements is not [ModelElement sole]
                || sole.From != Vector3.Zero
                || sole.To != new Vector3(16f))
                return false;

        return true;
    }

    // tint 槽号：0 不染 / 1 草绿 / 2 叶绿 / 3 水蓝 / 4..19 红石 power 0..15，着色器里的固定色板按同一套编号。
    // MC 原版按方块 id 查 colormap 注册表，tintindex 只是槽位号；这里用同一思路把
    // 「哪个方块染什么色」定在网格侧。按 plains 群系的固定色走（litematica 本体
    // 默认也是固定色），不做群系插值。
    private static float TintSlotFor(string stateId)
    {
        var block = stateId;
        var bracket = stateId.IndexOf('[');
        if (bracket >= 0) block = stateId[..bracket];

        // 红石线模型的 line/overlay 面使用 tintindex=0；原版由 power 查红石
        // 颜色表染色。材质包通常提供灰度线贴图，槽位为 0 会让它保持白色。
        if (block is "minecraft:redstone_wire" or "redstone_wire")
        {
            var power = 0;
            if (bracket >= 0)
            {
                var properties = stateId[(bracket + 1)..].TrimEnd(']');
                foreach (var property in properties.Split(',', StringSplitOptions.TrimEntries))
                {
                    var pair = property.Split('=', 2);
                    if (pair.Length == 2 && pair[0] == "power" && int.TryParse(pair[1], out var parsed))
                    {
                        power = Math.Clamp(parsed, 0, 15);
                        break;
                    }
                }
            }

            return 4f + power;
        }

        // seagrass 含 "grass"，必须先于草绿判；kelp、bubble_column 是水色同理。
        if (block.Contains("water", StringComparison.Ordinal)
            || block.Contains("seagrass", StringComparison.Ordinal)
            || block.Contains("kelp", StringComparison.Ordinal)
            || block.Contains("bubble_column", StringComparison.Ordinal))
            return 3f;

        if (block.Contains("leaves", StringComparison.Ordinal)
            || block.Contains("vine", StringComparison.Ordinal))
            return 2f;

        if (block.Contains("grass", StringComparison.Ordinal)
            || block.Contains("fern", StringComparison.Ordinal)
            || block.Contains("sugar_cane", StringComparison.Ordinal))
            return 1f;

        // 没归类的 tint（红石线的红、气泡柱之类）宁可不染保持灰白，
        // 也不要染成草绿——错色比缺色更难排查。
        return 0f;
    }

    private static void EmitQuad(List<float> vertices, List<int> indices, Vector3 origin, CachedQuad quad,
        int[] blocks, bool[] occludeFlags, Vector3I size, int x, int y, int z)
    {
        var baseIndex = vertices.Count / MeshData.FloatsPerVertex;
        foreach (var (position, uv) in new[]
                 {
                     (quad.A, quad.UvA), (quad.B, quad.UvB), (quad.C, quad.UvC), (quad.D, quad.UvD)
                 })
        {
            vertices.Add(origin.X + position.X);
            vertices.Add(origin.Y + position.Y);
            vertices.Add(origin.Z + position.Z);
            vertices.Add(quad.Normal.X);
            vertices.Add(quad.Normal.Y);
            vertices.Add(quad.Normal.Z);
            vertices.Add(uv.X);
            vertices.Add(uv.Y);
            vertices.Add(quad.TintSlot);
            vertices.Add(VertexAo(position, quad.Normal, blocks, occludeFlags, size, x, y, z));
        }

        indices.Add(baseIndex);
        indices.Add(baseIndex + 1);
        indices.Add(baseIndex + 2);
        indices.Add(baseIndex);
        indices.Add(baseIndex + 2);
        indices.Add(baseIndex + 3);
    }

    private static float VertexAo(Vector3 position, Vector3 normal, int[] blocks, bool[] occludeFlags,
        Vector3I size, int x, int y, int z)
    {
        var normalAxis = AxisOf(normal);
        if (MathF.Abs(ComponentOf(normal, normalAxis)) < 0.999f) return 1f;
        var faceCoordinate = normalAxis switch
        {
            0 => position.X,
            1 => position.Y,
            _ => position.Z
        };
        var normalSign = ComponentOf(normal, normalAxis) > 0 ? 1 : -1;
        if (MathF.Abs(faceCoordinate - (normalSign > 0 ? 1f : 0f)) > 0.001f) return 1f;

        Span<int> tangentAxes = stackalloc int[2];
        var cursor = 0;
        for (var axis = 0; axis < 3; axis++)
            if (axis != normalAxis)
                tangentAxes[cursor++] = axis;

        var side1Sign = ComponentAt(position, tangentAxes[0]) >= 0.5f ? 1 : -1;
        var side2Sign = ComponentAt(position, tangentAxes[1]) >= 0.5f ? 1 : -1;
        Span<int> normalStep = stackalloc int[3];
        Span<int> side1 = stackalloc int[3];
        Span<int> side2 = stackalloc int[3];
        normalStep[normalAxis] = normalSign;
        side1[tangentAxes[0]] = side1Sign;
        side2[tangentAxes[1]] = side2Sign;

        var side1Blocked = IsOccludingAt(blocks, occludeFlags, size,
            x + normalStep[0] + side1[0], y + normalStep[1] + side1[1], z + normalStep[2] + side1[2]);
        var side2Blocked = IsOccludingAt(blocks, occludeFlags, size,
            x + normalStep[0] + side2[0], y + normalStep[1] + side2[1], z + normalStep[2] + side2[2]);
        var cornerBlocked = IsOccludingAt(blocks, occludeFlags, size,
            x + normalStep[0] + side1[0] + side2[0],
            y + normalStep[1] + side1[1] + side2[1],
            z + normalStep[2] + side1[2] + side2[2]);

        if (side1Blocked && side2Blocked) return 0.55f;
        var occupied = (side1Blocked ? 1 : 0) + (side2Blocked ? 1 : 0) + (cornerBlocked ? 1 : 0);
        return occupied switch { 0 => 1f, 1 => 0.82f, 2 => 0.68f, _ => 0.55f };
    }

    private static float ComponentAt(Vector3 value, int axis) => axis switch
    {
        0 => value.X,
        1 => value.Y,
        _ => value.Z
    };

    private static bool IsOccludingAt(int[] blocks, bool[] occludeFlags, Vector3I size, int x, int y, int z)
    {
        if (x < 0 || y < 0 || z < 0 || x >= size.X || y >= size.Y || z >= size.Z) return false;
        var index = (y * size.Z + z) * size.X + x;
        return (uint)index < (uint)blocks.Length
               && (uint)blocks[index] < (uint)occludeFlags.Length
               && occludeFlags[blocks[index]];
    }

    private Vector2 MapUv(float u16, float v16, SpriteRect rect)
    {
        // uv 0..16（MC 纹素）→ 图集像素 → [0,1]。不做 1-v 翻转：GL 采样的 t=0
        // 就是上传数据的第一行，而图集字节数组行 0 是图集顶（PNG 约定），
        // 所以 v = y/H 直接对应。曾经在这里翻转过，单行图集的 cell 上下对称
        // （16px sprite + 上下各 8px pad）镜像后仍落回自己 cell，完全看不出来；
        // 多行图集时行 0 的 sprite 会镜像进行 1 的区域，采样到挖孔贴图
        // 的透明黑后被 cutout discard 整片丢光——机甲文件「大面积缺面」的根因。
        var u = (rect.X + u16 / 16f * rect.Width) / _atlas.Width;
        var v = (rect.Y + v16 / 16f * rect.Height) / _atlas.Height;
        return new Vector2(u, v);
    }

    private bool TryGetRect(string sprite, out SpriteRect rect)
    {
        return _atlas.TryGetRect(sprite, out rect);
    }

    private static float Pick(float from, float to, int axis, float sign)
    {
        return axis switch
        {
            0 => sign > 0 ? to : from,
            1 => sign > 0 ? to : from,
            _ => sign > 0 ? to : from
        };
    }

    private static float Pick(Vector3 from, Vector3 to, int axis, float sign)
    {
        return axis switch
        {
            0 => sign > 0 ? to.X : from.X,
            1 => sign > 0 ? to.Y : from.Y,
            _ => sign > 0 ? to.Z : from.Z
        };
    }

    private static Vector3 WithAxis(Vector3 v, int axis, float value)
    {
        return axis switch
        {
            0 => v with { X = value },
            1 => v with { Y = value },
            _ => v with { Z = value }
        };
    }

    private static int AxisOf(Vector3 v)
    {
        return MathF.Abs(v.X) > 0.5f ? 0 : MathF.Abs(v.Y) > 0.5f ? 1 : 2;
    }

    private static float ComponentOf(Vector3 v, int axis)
    {
        return axis switch
        {
            0 => v.X,
            1 => v.Y,
            _ => v.Z
        };
    }

    private static float Extent(Vector3 from, Vector3 to, int axis)
    {
        return axis switch
        {
            0 => MathF.Abs(to.X - from.X),
            1 => MathF.Abs(to.Y - from.Y),
            _ => MathF.Abs(to.Z - from.Z)
        };
    }

    private static bool TryDirection(string name, out Vector3 direction)
    {
        switch (name)
        {
            case "down":
                direction = new Vector3(0, -1, 0);
                return true;
            case "up":
                direction = new Vector3(0, 1, 0);
                return true;
            case "north":
                direction = new Vector3(0, 0, -1);
                return true;
            case "south":
                direction = new Vector3(0, 0, 1);
                return true;
            case "west":
                direction = new Vector3(-1, 0, 0);
                return true;
            case "east":
                direction = new Vector3(1, 0, 0);
                return true;
            default:
                direction = default;
                return false;
        }
    }

    // ---------- 缓存：每个方块状态解析一次，旋转在缓存时就算完 ----------

    private sealed record CachedQuad(
        Vector3 A,
        Vector3 B,
        Vector3 C,
        Vector3 D,
        Vector3 Normal,
        Vector2 UvA,
        Vector2 UvB,
        Vector2 UvC,
        Vector2 UvD,
        string Sprite,
        Vector3? Cullface,
        float TintSlot);

    private sealed record CachedVariant(List<CachedQuad> Quads);

    // Multipart=true：Variants 是同一格要一起画的部件（墙 = post + 连接臂），
    // 全部输出；false：Variants 是互斥花色（stone 的 4 个旋转），按位置挑一个。
    // 挑一个的旧逻辑曾把 multipart 也当花色处理，墙和栅栏每次只画一个部件，
    // 连接臂整条消失。
    private sealed record CachedState(bool IsMultipart, List<CachedVariant> Variants);
}
