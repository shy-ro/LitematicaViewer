using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using LitematicaViewer.Assets.Model;

namespace LitematicaViewer.Assets.Tests;

// 资产层的打桩验收，对真实文件跑：args[0] 是原版包（jar/zip/文件夹），
// 后面的参数依次是覆盖包（材质包）。与 CoreSmoke 同一套规矩：WriteLine 打桩、Assert 收口。
public static class PackSmoke
{
    private static int _checks;

    public static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("用法: PackSmoke <原版包(jar/zip/文件夹)> [覆盖包...] 或 PackSmoke --scan <原版包> [覆盖包...]");
            return 2;
        }

        using TextWriterTraceListener listener = new(Console.Out);
        Trace.Listeners.Add(listener);
        Trace.AutoFlush = true;

        // --scan 模式：全量扫包栈里的 blockstate，把「解不出 variant / 解不出面 /
        // sprite 缺图」的方块一次点名。画面上「某方块整块消失」时先跑这个，
        // 比对着截图猜快得多。
        var scan = args[0] == "--scan";
        var packPaths = scan ? args[1..] : args;

        using PackStack packs = new();
        foreach (var path in packPaths)
        {
            var pack = Directory.Exists(path) ? ResourcePack.OpenFolder(path) : ResourcePack.OpenZip(path);
            packs.Add(pack);
            Debug.WriteLine($"[ASSETS][smoke] 装包 {pack.Name} entries={pack.Enumerate("assets/").Count()}");
        }

        BlockStateResolver resolver = new(packs);

        if (scan) return ScanAllBlocks(packs, resolver);

        CheckPackStack(packs);
        CheckStone(resolver);
        CheckShulkerUv(resolver);
        CheckShulkerColors(resolver);
        CheckShulkerFacings(resolver);
        CheckChestVariants(resolver);
        CheckChestHalvesSeam(resolver);
        CheckRailMatrix(resolver);
        CheckRedstoneMultipart(resolver);
        CheckLogAxis(resolver);
        CheckStairsMultipart(resolver);
        CheckElementRotation(resolver);
        CheckOverridePack(packs, resolver);
        CheckBatchRealBlocks(resolver);
        CheckAtlas(packs);

        Debug.WriteLine($"[ASSETS][smoke] 完成 checks={_checks} {resolver.Stats}");
        return 0;
    }

    private static void CheckPackStack(PackStack packs)
    {
        // 栈里任何一层有 stone.json 就能读到——这条不成立的话后面全不用看。
        Debug.Assert(
            packs.TryRead("assets/minecraft/blockstates/stone.json", out var stone),
            "[ASSETS][smoke] 栈里读不到 assets/minecraft/blockstates/stone.json");
        Debug.Assert(stone.Length > 2, $"[ASSETS][smoke] stone.json 是空的 len={stone.Length}");
        _checks++;
    }

    private static void CheckStone(BlockStateResolver resolver)
    {
        // cube_all：六个面同一张贴图、无 tint、默认整张 uv。
        // 26.3 的 stone blockstate 给了 4 个 variant（旋转/镜像的花色变化，刻意不止一个），
        // 所以这里只断言「至少一个」，逐面断言对每个 variant 都成立。
        var state = resolver.Resolve("minecraft:stone");
        Debug.Assert(state.Variants.Count >= 1,
            $"[ASSETS][smoke] stone 命中 {state.Variants.Count} 个 variant expected>=1");
        foreach (var variant in state.Variants)
        {
            Debug.Assert(variant.Model.Elements.Count == 1,
                $"[ASSETS][smoke] {variant.ModelId} 应有 1 个 element 实得 {variant.Model.Elements.Count}");
            var element = variant.Model.Elements[0];
            Debug.Assert(element.Faces.Count == 6,
                $"[ASSETS][smoke] {variant.ModelId} 应有 6 个面 实得 {element.Faces.Count}");
            foreach (var face in element.Faces)
            {
                Debug.Assert(face.Sprite == "minecraft:block/stone",
                    $"[ASSETS][smoke] {variant.ModelId} 面贴图={face.Sprite} expected=minecraft:block/stone");

                // 镜像 variant 用翻转的 uv 实现（16,0,0,16 而不是 0,0,16,16），
                // 所以断言「占满整张贴图」而不是「恰好等于默认值」。
                Debug.Assert(
                    face.Uv.X >= 0f && face.Uv.Y >= 0f && face.Uv.Z <= 16f && face.Uv.W <= 16f
                    && MathF.Abs(MathF.Abs(face.Uv.Z - face.Uv.X) - 16f) < 1e-4f
                    && MathF.Abs(MathF.Abs(face.Uv.W - face.Uv.Y) - 16f) < 1e-4f,
                    $"[ASSETS][smoke] {variant.ModelId} 面 uv 应占满整张贴图 实得 {face.Uv}");
                Debug.Assert(face.TintIndex == -1, $"[ASSETS][smoke] {variant.ModelId} 不应有 tint 实得 {face.TintIndex}");
            }
        }

        Debug.WriteLine($"[ASSETS][smoke] stone: {state.Variants.Count} 个 variant 全部 cube_all 6 面 1 贴图 ✓");
        _checks++;
    }

    private static void CheckLogAxis(BlockStateResolver resolver)
    {
        // #引用与贴图合并：原木侧面与端面是两张图，由 cube_column 的 #end/#side 给出。
        var state = resolver.Resolve("minecraft:oak_log[axis=y]");
        Debug.Assert(state.Variants.Count == 1,
            $"[ASSETS][smoke] oak_log[axis=y] 命中 {state.Variants.Count} expected=1");
        var element = state.Variants[0].Model.Elements.Single();
        var up = element.Faces.Single(f => f.Face == FaceName.Up);
        var north = element.Faces.Single(f => f.Face == FaceName.North);
        Debug.Assert(up.Sprite == "minecraft:block/oak_log_top",
            $"[ASSETS][smoke] 原木端面={up.Sprite} expected=oak_log_top");
        Debug.Assert(north.Sprite == "minecraft:block/oak_log",
            $"[ASSETS][smoke] 原木侧面={north.Sprite} expected=oak_log");

        // axis=x 时 variant 带 y 旋转（MC 用整体旋转表达横放的原木）。
        var rotated = resolver.Resolve("minecraft:oak_log[axis=x]");
        Debug.Assert(rotated.Variants.Count == 1, "[ASSETS][smoke] oak_log[axis=x] 应命中 1 个 variant");
        Debug.Assert(
            rotated.Variants[0].XDegrees != 0f || rotated.Variants[0].YDegrees != 0f,
            "[ASSETS][smoke] oak_log[axis=x] 应带 x/y 整体旋转（横放）");
        Debug.WriteLine(
            $"[ASSETS][smoke] oak_log: axis=y 端/侧贴图分开 ✓，axis=x 旋转=({rotated.Variants[0].XDegrees},{rotated.Variants[0].YDegrees}) ✓");
        _checks++;
    }

    private static void CheckShulkerUv(BlockStateResolver resolver)
    {
        var state = resolver.Resolve("minecraft:yellow_shulker_box");
        Debug.Assert(state.Variants.Count >= 1, "[ASSETS][smoke] yellow_shulker_box 应命中模型");
        var elements = state.Variants[0].Model.Elements;
        Debug.Assert(elements.Count == 2,
            $"[ASSETS][smoke] 潜影盒应有盒身+盖子两个盒件 实得 {elements.Count}");

        // 新模型按实体尺寸：底座 16x8x16，盖子 16x12x16 且向下重叠 4px。
        // UV 每个区域内缩 0.25 像素，避免实体展开图相邻区域线性串色。
        var bodyMinV = elements[0].Faces.Min(face => MathF.Min(face.Uv.Y, face.Uv.W));
        var lidMinV = elements[1].Faces.Min(face => MathF.Min(face.Uv.Y, face.Uv.W));
        Debug.Assert(MathF.Abs(bodyMinV - 7.0625f) < 1e-4f,
            $"[ASSETS][smoke] 潜影盒盒身 UV 起点应为 7.0625 实得 {bodyMinV}");
        Debug.Assert(MathF.Abs(lidMinV - 0.0625f) < 1e-4f,
            $"[ASSETS][smoke] 潜影盒盖子 UV 起点应为 0.0625 实得 {lidMinV}");
        Debug.Assert(elements[0].From == Vector3.Zero && elements[0].To == new Vector3(16, 8, 16),
            $"[ASSETS][smoke] 潜影盒盒身尺寸应为 16x8x16 实得 {elements[0].From}→{elements[0].To}");
        Debug.Assert(elements[1].From == new Vector3(0, 4, 0) && elements[1].To == new Vector3(16, 16, 16),
            $"[ASSETS][smoke] 潜影盒盖子尺寸应为 16x12x16 且重叠 4px 实得 {elements[1].From}→{elements[1].To}");
        _checks++;
    }

    private static void CheckShulkerColors(BlockStateResolver resolver)
    {
        string[] colors =
        [
            "", "white_", "orange_", "magenta_", "light_blue_", "yellow_", "lime_", "pink_",
            "gray_", "light_gray_", "cyan_", "purple_", "blue_", "brown_", "green_", "red_", "black_"
        ];
        foreach (var prefix in colors)
        {
            var id = $"minecraft:{prefix}shulker_box";
            var state = resolver.Resolve(id);
            Debug.Assert(state.Variants.Count > 0 && state.Variants.All(static variant => variant.Model.Elements.Count == 2),
                $"[ASSETS][smoke] {id} 应完整解析盒身+盖子");
            foreach (var face in state.Variants.SelectMany(static variant => variant.Model.Elements)
                         .SelectMany(static element => element.Faces))
                Debug.Assert(face.Uv.X is >= 0 and <= 16 && face.Uv.Z is >= 0 and <= 16 &&
                             face.Uv.Y is >= 0 and <= 16 && face.Uv.W is >= 0 and <= 16,
                    $"[ASSETS][smoke] {id} UV 越界 {face.Uv}");
        }
        Debug.WriteLine($"[ASSETS][smoke] 潜影盒: {colors.Length} 个染色版本盒身/盖子与 UV ✓");
        _checks++;
    }

    private static void CheckShulkerFacings(BlockStateResolver resolver)
    {
        var expected = new Dictionary<string, (int Axis, float Angle)?>
        {
            ["up"] = null, ["down"] = (0, 180), ["north"] = (0, -90),
            ["south"] = (0, 90), ["east"] = (2, -90), ["west"] = (2, 90)
        };
        foreach (var pair in expected)
        {
            var state = resolver.Resolve($"minecraft:purple_shulker_box[facing={pair.Key}]");
            var elements = state.Variants.Single().Model.Elements;
            Debug.Assert(elements.Count == 2, $"[ASSETS][smoke] shulker facing={pair.Key} 应有两壳件");
            if (pair.Value is null)
                Debug.Assert(elements.All(static element => element.Rotation is null),
                    "[ASSETS][smoke] shulker facing=up 不应旋转");
            else
                Debug.Assert(elements.All(element => element.Rotation is { } rotation &&
                    rotation.Axis == pair.Value.Value.Axis &&
                    MathF.Abs(rotation.AngleDegrees - pair.Value.Value.Angle) < 1e-4f),
                    $"[ASSETS][smoke] shulker facing={pair.Key} 旋转不符");
        }
        _checks++;
    }

    private static void CheckChestVariants(BlockStateResolver resolver)
    {
        foreach (var facing in new[] { "north", "east", "south", "west" })
        foreach (var type in new[] { "single", "left", "right" })
        {
            var id = $"minecraft:chest[facing={facing},type={type},waterlogged=false]";
            var state = resolver.Resolve(id);
            Debug.Assert(state.Variants.Count > 0, $"[ASSETS][smoke] {id} 没有模型");
            var elementCount = state.Variants.SelectMany(static variant => variant.Model.Elements).Count();
            const int expected = 3;
            Debug.Assert(elementCount >= expected,
                $"[ASSETS][smoke] {id} 应包含盖/身/锁舌 实得 {elementCount}");
            var expectedAngle = facing switch { "south" => 0f, "east" => 90f, "west" => -90f, _ => 180f };
            foreach (var element in state.Variants.SelectMany(static variant => variant.Model.Elements))
                if (expectedAngle == 0f)
                    Debug.Assert(element.Rotation is null, $"[ASSETS][smoke] {id} south 不应旋转");
                else
                    Debug.Assert(element.Rotation is { Axis: 1 } rotation &&
                                 MathF.Abs(rotation.AngleDegrees - expectedAngle) < 1e-4f,
                        $"[ASSETS][smoke] {id} element rotation 不符");
        }
        Debug.WriteLine("[ASSETS][smoke] 箱子: 4 facing × single/left/right ✓");
        _checks++;

        // 探针：单箱每个面的 box 与 uv（排查上下/前后段位用）。uv 单位是 16/64 后的值，
        // 乘 4 就是贴图像素。期望（原版 ChestModel）：盖 texOffs(0,0) 5 高、身 texOffs(0,19)
        // 10 高、锁舌 texOffs(0,0) 2x4x1。
        foreach (var element in resolver.Resolve("minecraft:chest[facing=north,type=single,waterlogged=false]")
                     .Variants.SelectMany(static variant => variant.Model.Elements))
        {
            Debug.WriteLine($"[ASSETS][chest.probe] box=({element.From.X},{element.From.Y},{element.From.Z})..({element.To.X},{element.To.Y},{element.To.Z}) faces={element.Faces.Count}");
            foreach (var face in element.Faces)
                Debug.WriteLine($"[ASSETS][chest.probe]   {face.Face,-6} uvPx=({face.Uv.X * 4:F2},{face.Uv.Y * 4:F2})..({face.Uv.Z * 4:F2},{face.Uv.W * 4:F2}) sprite={face.Sprite}");
        }
    }

    // 双箱两个半块的接缝方向。_left 半张贴图的第一段（West）和 _right 的第三段
    // （East）是透明留白，必须分别落在 x=0 / x=16 这两条朝搭档的边上。
    // 面序写成 East–North–West–South（东西镜像）时单箱毫无变化——四个侧段几乎同色，
    // 半张的透明段却被推到外缘，双箱整块空面朝外。这条断言只能从 uv 段号上抓回来。
    private static void CheckChestHalvesSeam(BlockStateResolver resolver)
    {
        // 15 宽展开：dimZ=14、dimX=15 → u0=0 u1=14 u2=29 u3=43 u4=58，÷4 得 uv。
        // 顶格另算：右界是 u2+dimX=44（不是侧面的 u3=43），差分只有 1px——正因如此
        // 单箱（14x14）永远试不出来，只有半块这条 15x14 的盒子能钉住它。
        const float westStart = 0f;      // 第 1 段，_left 的透明接缝
        const float northStart = 3.5f;   // 第 2 段（背面）
        const float eastStart = 7.25f;   // 第 3 段，_right 的透明接缝
        const float southStart = 10.75f; // 第 4 段（正面，闩所在的那面）
        const float upRight = 11f;       // u2+width = 44 → 11（侧面东段的右界是 10.75）
        foreach (var facing in new[] { "north", "east", "south", "west" })
        {
            var left = Body(resolver, facing, "left");
            Debug.Assert(left.From.X == 0f,
                $"[ASSETS][smoke] chest[left] facing={facing} 接缝应在西缘 实得 From.X={left.From.X}");
            // 原版 left 层用 Util.allOfEnumExcept(WEST)：接缝那一面根本不生成。
            // 我们早先只靠半张贴图的透明段遮住它，mip 粗层把透明段染成不透明就会浮出暗板。
            Debug.Assert(left.Faces.All(face => face.Face != FaceName.West),
                $"[ASSETS][smoke] chest[left] facing={facing} 不该生成接缝面 West");
            Uv(left, FaceName.North, northStart, 7.25f, "left North 应采第二段");
            Uv(left, FaceName.East, eastStart, 10.75f, "left East 应采第三段");
            Uv(left, FaceName.South, southStart, 14.5f, "left South（正面）应采第四段");

            var right = Body(resolver, facing, "right");
            Debug.Assert(right.To.X == 16f,
                $"[ASSETS][smoke] chest[right] facing={facing} 接缝应在东缘 实得 To.X={right.To.X}");
            Debug.Assert(right.Faces.All(face => face.Face != FaceName.East),
                $"[ASSETS][smoke] chest[right] facing={facing} 不该生成接缝面 East");
            Uv(right, FaceName.West, westStart, 3.5f, "right West 应采第一段");
            Uv(right, FaceName.North, northStart, 7.25f, "right North 应采第二段");
            Uv(right, FaceName.South, southStart, 14.5f, "right South（正面）应采第四段");

            // 顶/底段序：原版 ModelPart.Cube 是 **Down 第一格、Up 第二格**（26.3 未混淆
            // 字节码实算 DOWN=(u+dz, v, u+dz+dx, v+dz)、UP=(u+dz+dx, v+dz, u+dz+2dx, v)）。
            // 箱身 Up 必须落在第二格（贴图那格是「黑口 + 2px 木框」的内部），Down 是第一格。
            // 顶格三件事一次钉死：① u 从 u2=29 起；② u 右界是 u2+width=44（不是 43）；
            // ③ v 倒序——Uv.Y 是区间下沿（大）、Uv.W 是上沿（小），写正序会把盖顶木纹
            // 沿南北镜像（单箱两格内容近乎对称，只有角上一个暗像素换边，肉眼抓不住）。
            var body = Element(resolver, facing, "left", 10f);
            UvRect(body, FaceName.Down, 3.5f, 4.75f, 7.25f, 8.25f, "箱身 Down 应采第一格");
            UvRect(body, FaceName.Up, 7.25f, 8.25f, upRight, 4.75f, "箱身 Up 应采第二格（width 宽、v 倒序）");
            Debug.Assert(body.Faces.Single(face => face.Face == FaceName.Up).Uv.Y >
                         body.Faces.Single(face => face.Face == FaceName.Up).Uv.W,
                "[ASSETS][smoke] chest Up 的 v 必须是倒序（原版 UP 传 v+dz 在前、v 在后）");

            // 盖走 texOffs(0,0)：盖条带在 v14..18，身条带在 v33..42。盖传 v=19 会把盖
            // 采成身的顶部五行，盖/身那道暗分界线消失（单箱看着仍像箱子，所以骗过了一轮）。
            var lid = Element(resolver, facing, "left", 5f);
            UvRect(lid, FaceName.North, 3.5f, 3.5f, 7.25f, 4.75f, "箱盖侧面应采盖条带 v14..19");
            UvRect(lid, FaceName.Up, 7.25f, 3.5f, upRight, 0f, "箱盖 Up 应采盖那条带 v0..14（v 倒序）");
            // 盖 y 9..14（原版 offset(0,9)），与箱身 0..10 重叠 1px。写 10..15 除了整体高
            // 1px，还会让盖底与箱身顶共面抢深度。
            Debug.Assert(lid.From.Y == 9f && lid.To.Y == 14f,
                $"[ASSETS][smoke] chest lid 应在 y 9..14 实得 {lid.From.Y}..{lid.To.Y}");
            // 闩 y 7..11（原版 addBox(...,-2,14,2,4,1) @ offset(0,9,1)）。
            var latch = Element(resolver, facing, "left", 4f);
            Debug.Assert(latch.From.Y == 7f && latch.To.Y == 11f,
                $"[ASSETS][smoke] chest lock 应在 y 7..11 实得 {latch.From.Y}..{latch.To.Y}");
        }

        Debug.WriteLine("[ASSETS][smoke] 双箱接缝: 接缝面不生成 + 顶格 width 宽且 v 倒序 ✓");
        _checks++;
        return;

        static void Uv(ModelElement element, FaceName face, float u0, float u1, string message)
        {
            var uv = element.Faces.Single(candidate => candidate.Face == face).Uv;
            Debug.Assert(MathF.Abs(uv.X - u0) < 1e-4f && MathF.Abs(uv.Z - u1) < 1e-4f,
                $"[ASSETS][smoke] {message} 实得 {uv}");
        }

        static void UvRect(ModelElement element, FaceName face, float u0, float v0, float u1, float v1,
            string message)
        {
            var uv = element.Faces.Single(candidate => candidate.Face == face).Uv;
            Debug.Assert(MathF.Abs(uv.X - u0) < 1e-4f && MathF.Abs(uv.Y - v0) < 1e-4f &&
                         MathF.Abs(uv.Z - u1) < 1e-4f && MathF.Abs(uv.W - v1) < 1e-4f,
                $"[ASSETS][smoke] {message} 实得 {uv}");
        }

        // 按盒件高度取件：箱身 10、盖 5、闩 4。
        static ModelElement Element(BlockStateResolver resolver, string facing, string type, float height) =>
            resolver.Resolve($"minecraft:chest[facing={facing},type={type},waterlogged=false]")
                .Variants.SelectMany(static variant => variant.Model.Elements)
                .First(element => MathF.Abs(element.To.Y - element.From.Y - height) < 1e-4f);

        static ModelElement Body(BlockStateResolver resolver, string facing, string type) =>
            resolver.Resolve($"minecraft:chest[facing={facing},type={type},waterlogged=false]")
                .Variants.SelectMany(static variant => variant.Model.Elements)
                .OrderByDescending(static element => element.To.Y - element.From.Y)
                .First();
    }

    private static void CheckRailMatrix(BlockStateResolver resolver)
    {
        string[] railShapes =
        [
            "north_south", "east_west", "ascending_east", "ascending_west", "ascending_north",
            "ascending_south", "south_east", "south_west", "north_west", "north_east"
        ];
        foreach (var shape in railShapes) AssertRail($"minecraft:rail[shape={shape},waterlogged=false]");
        foreach (var rail in new[] { "powered_rail", "detector_rail", "activator_rail" })
        foreach (var shape in railShapes.Take(6))
            AssertRail($"minecraft:{rail}[powered=false,shape={shape},waterlogged=false]");
        Debug.WriteLine("[ASSETS][smoke] 铁轨: 10 普通 shape + 18 powered/detector/activator shape ✓");
        _checks++;
        return;

        void AssertRail(string id)
        {
            var state = resolver.Resolve(id);
            var elements = state.Variants.SelectMany(static variant => variant.Model.Elements).ToArray();
            Debug.Assert(elements.Length > 0 && elements.Sum(static element => element.Faces.Count) > 0,
                $"[ASSETS][smoke] {id} 没有铁轨几何");
            if (id.Contains("ascending_", StringComparison.Ordinal))
                Debug.Assert(elements.SelectMany(static element => new[] { element.From.Y, element.To.Y }).Max() > 1f,
                    $"[ASSETS][smoke] {id} 坡道没有抬高 Y");
        }
    }

    private static void CheckRedstoneMultipart(BlockStateResolver resolver)
    {
        // XK redstone display 的连接条件使用 "side|up"；应同时命中两段连接线
        // 与 power=7 数字层，不能只剩数字。
        var state = resolver.Resolve(
            "minecraft:redstone_wire[east=none,north=side,power=7,south=none,west=none]");
        var modelIds = state.Variants.Select(static variant => variant.ModelId).ToArray();
        Debug.Assert(modelIds.Any(static id => id.Contains("redstone_dust_side", StringComparison.Ordinal)),
            $"[ASSETS][smoke] 红石 north=side 没命中连接线 multipart: [{string.Join(", ", modelIds)}]");
        Debug.Assert(modelIds.Any(static id => id.EndsWith("redstone_dust_p07", StringComparison.Ordinal)),
            $"[ASSETS][smoke] 红石 power=7 没命中数字层: [{string.Join(", ", modelIds)}]");
        Debug.Assert(modelIds.Length >= 3,
            $"[ASSETS][smoke] 单向红石应包含两段线+数字层，实得 {modelIds.Length}: [{string.Join(", ", modelIds)}]");
        _checks++;
    }

    private static void CheckStairsMultipart(BlockStateResolver resolver)
    {
        // 楼梯是 multipart：条件命中在这里验。
        // 26.3 重做了楼梯模型——旧的 45° element rotation 没了，现在是两个轴对齐的盒子
        // （1.20 时代的资料会说楼梯带旋转，照旧资料写断言就会在这上头栽）。
        var state = resolver.Resolve("minecraft:oak_stairs[facing=east,half=bottom,shape=straight,waterlogged=false]");
        Debug.Assert(state.Variants.Count >= 1,
            $"[ASSETS][smoke] oak_stairs 应至少命中 1 个 apply 实得 {state.Variants.Count}");
        var variant = state.Variants[0];
        var totalFaces = variant.Model.Elements.Sum(e => e.Faces.Count);
        Debug.Assert(variant.Model.Elements.Count >= 2,
            $"[ASSETS][smoke] 26.3 楼梯应是两个盒子 实得 {variant.Model.Elements.Count} 个 element");
        Debug.Assert(totalFaces >= 6, $"[ASSETS][smoke] 楼梯面数应 ≥6 实得 {totalFaces}");
        Debug.WriteLine(
            $"[ASSETS][smoke] oak_stairs: multipart 命中，{variant.Model.Elements.Count} 个盒子 {totalFaces} 个面 ✓");
        _checks++;
    }

    private static void CheckElementRotation(BlockStateResolver resolver)
    {
        // element rotation 在 26.3 的原版包里还剩 159 个模型在用，big_dripleaf 是其中之一
        // （y 轴 45°，带 rescale——rescale 暂不解析，罕见且影响小）。
        var state = resolver.Resolve("minecraft:big_dripleaf[facing=north,tilt=none,waterlogged=false]");
        Debug.Assert(state.Variants.Count >= 1, "[ASSETS][smoke] big_dripleaf 没解析出 variant");
        var rotation = state.Variants.SelectMany(v => v.Model.Elements)
            .Select(e => e.Rotation)
            .FirstOrDefault(r => r is not null);
        Debug.Assert(rotation is not null, "[ASSETS][smoke] big_dripleaf 应带 element rotation");
        Debug.Assert(rotation!.Value.Axis == 1 && MathF.Abs(rotation.Value.AngleDegrees - 45f) < 1e-4f,
            $"[ASSETS][smoke] big_dripleaf 旋转应绕 y 轴 45° 实得 axis={rotation.Value.Axis} angle={rotation.Value.AngleDegrees}");
        Debug.WriteLine($"[ASSETS][smoke] big_dripleaf: element rotation y/45° 解析 ✓ origin={rotation.Value.Origin}");
        _checks++;
    }

    private static void CheckOverridePack(PackStack packs, BlockStateResolver resolver)
    {
        // 用户包的命名空间（create）原版栈里没有——能读 = 分层查找通了。
        Debug.Assert(
            packs.TryRead("assets/create/blockstates/linear_chassis.json", out var chassis),
            "[ASSETS][smoke] 用户包的 create 命名空间读不到");

        // variants 的键要求全部属性匹配：不带属性的状态一个键都命中不了是**正确**行为，
        // 所以这里必须给全属性，而不是拿裸方块名去测命中数。
        var state = resolver.Resolve("create:linear_chassis[axis=y,sticky_bottom=false,sticky_top=false]");
        Debug.Assert(
            state.Variants.Count == 1,
            $"[ASSETS][smoke] create:linear_chassis[axis=y] 应命中 1 个 variant 实得 {state.Variants.Count}");
        Debug.WriteLine(
            $"[ASSETS][smoke] create:linear_chassis: {state.Variants.Count} 个 variant、" +
            $"{state.Variants.Select(v => v.Model.Elements.Count).Sum()} 个 element ✓");
        _checks++;
    }

    private static void CheckBatchRealBlocks(BlockStateResolver resolver)
    {
        // 一批常见方块各解析一遍：不逐个断言贴图名（那是每个模型的读法问题），
        // 只断言「每个都至少命中一个 variant、每个 variant 至少一个面」——
        // 这一条拦住的是 parent 链断裂、#引用悬空这类会让整批方块消失的系统性故障。
        string[] batch =
        [
            "minecraft:dirt",
            "minecraft:grass_block[snowy=false]",
            "minecraft:cobblestone",
            "minecraft:oak_planks",
            "minecraft:glass",
            "minecraft:furnace[facing=north,lit=false]",
            "minecraft:oak_leaves[distance=7,persistent=true,waterlogged=false]",
            "minecraft:sand",
            "minecraft:water[level=0]",
            "minecraft:oak_fence[east=true,north=false,south=false,waterlogged=false,west=true]",
            "minecraft:glowstone",
            "minecraft:redstone_lamp[lit=true]",
            "minecraft:repeater[facing=north,delay=1,locked=false,powered=true]",
            "minecraft:piston[facing=up,extended=true]"
        ];

        var totalVariants = 0;
        var totalFaces = 0;
        var spritesWithoutTexture = 0;
        foreach (var blockId in batch)
        {
            var state = resolver.Resolve(blockId);
            Debug.Assert(
                state.Variants.Count > 0,
                $"[ASSETS][smoke] {blockId} 没解析出任何 variant note=parent 链断或 blockstate 缺失");
            foreach (var variant in state.Variants)
            {
                totalVariants++;
                foreach (var element in variant.Model.Elements)
                {
                    totalFaces += element.Faces.Count;
                    spritesWithoutTexture += element.Faces.Count(f => f.Sprite.Length == 0);
                }
            }
        }

        Debug.Assert(
            spritesWithoutTexture == 0,
            $"[ASSETS][smoke] 批量解析中有 {spritesWithoutTexture} 个面的贴图引用解不开 note=看上面的 resolve 日志找是哪个模型");
        Debug.WriteLine($"[ASSETS][smoke] 批量 {batch.Length} 个方块: {totalVariants} variants / {totalFaces} faces ✓");
        _checks++;
    }

    private static void CheckAtlas(PackStack packs)
    {
        // 缺的贴图给棋盘占位、动画取首帧、装箱不重叠——三条是图集的最低保证。
        var atlas = TextureAtlas.Build(packs,
        [
            "minecraft:block/stone",
            "minecraft:block/oak_log",
            "minecraft:block/oak_log_top",
            "minecraft:block/water_still", // 动画：竖排多帧 + mcmeta
            "minecraft:block/oak_planks",
            "minecraft:block/__nope__" // 故意缺失
        ]);

        Debug.Assert(atlas.MissingCount == 1, $"[ASSETS][smoke] 缺失贴图应正好 1 张 实得 {atlas.MissingCount}");

        // 落位不越界 + 两两不重叠（占格检查：图集不大，直接开一张占用表）。
        var occupied = new bool[atlas.Width * atlas.Height];
        foreach (var rect in atlas.Rects)
        {
            Debug.Assert(
                rect.X >= 0 && rect.Y >= 0 && rect.X + rect.Width <= atlas.Width &&
                rect.Y + rect.Height <= atlas.Height,
                $"[ASSETS][smoke] {rect.Sprite} 落位越界 rect={rect} atlas={atlas.Width}x{atlas.Height}");
            for (var y = rect.Y; y < rect.Y + rect.Height; y++)
            for (var x = rect.X; x < rect.X + rect.Width; x++)
            {
                var index = y * atlas.Width + x;
                Debug.Assert(!occupied[index], $"[ASSETS][smoke] {rect.Sprite} 与别的 sprite 重叠在 ({x},{y})");
                occupied[index] = true;
            }
        }

        // 动画贴图只留首帧：water_still 源文件是 16x512，进图集必须是 16x16。
        var water = atlas.Rects.Single(r => r.Sprite == "minecraft:block/water_still");
        Debug.Assert(water.Width == 16 && water.Height == 16,
            $"[ASSETS][smoke] water_still 应只取首帧 实得 {water.Width}x{water.Height}");

        // 占位棋盘的 (0,0) 是品红：缺贴图在画面上要一眼认得出来。
        var missing = atlas.Rects.Single(r => r.Sprite == "minecraft:block/__nope__");
        var offset = (missing.Y * atlas.Width + missing.X) * 4;
        Debug.Assert(
            atlas.Pixels[offset] == 248 && atlas.Pixels[offset + 1] == 0 && atlas.Pixels[offset + 2] == 248 &&
            atlas.Pixels[offset + 3] == 255,
            $"[ASSETS][smoke] 缺失贴图的占位色不是品红 got=({atlas.Pixels[offset]},{atlas.Pixels[offset + 1]},{atlas.Pixels[offset + 2]},{atlas.Pixels[offset + 3]})");

        Debug.WriteLine(
            $"[ASSETS][smoke] 图集 {atlas.Width}x{atlas.Height}，{atlas.Rects.Count} 个 sprite（缺 {atlas.MissingCount}），装箱无重叠 ✓");
        _checks++;
    }

    // ---------- --scan：全量 blockstate 体检 ----------

    // 遍历栈里全部 blockstate，逐个 resolve，点名三类故障：
    // noVariants=解不出任何 variant（parent 链断、multipart 全不命中）；
    // noFaces=有 variant 但一个面都没有（elements 空、引用解不开全被丢）；
    // 缺图=sprite 不在包栈里（渲染出来是棋盘，名单里能看出缺哪张）。
    private static int ScanAllBlocks(PackStack packs, BlockStateResolver resolver)
    {
        var ids = packs
            .Enumerate("assets/")
            .Where(path => path.EndsWith("/blockstates/", StringComparison.Ordinal) is false
                           && path.Contains("/blockstates/", StringComparison.Ordinal)
                           && path.EndsWith(".json", StringComparison.Ordinal))
            .Select(path =>
            {
                // assets/<ns>/blockstates/<name>.json → <ns>:<name>
                var nsStart = "assets/".Length;
                var slash = path.IndexOf('/', nsStart);
                var marker = path.IndexOf("/blockstates/", nsStart, StringComparison.Ordinal);
                var ns = path[nsStart..marker];
                var name = path[(marker + "/blockstates/".Length)..^".json".Length];
                return $"{ns}:{name}";
            })
            .Distinct()
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        List<string> noVariants = [];
        List<string> noFaces = [];
        HashSet<string> allSprites = new(StringComparer.Ordinal);
        Dictionary<string, int> faceCountById = new(StringComparer.Ordinal);

        foreach (var id in ids)
        {
            // 无属性 id 解 multipart/全属性 variants 必然 0 面（26.3 的键是全属性匹配），
            // 那是姿势问题不是资产问题：从 blockstate JSON 里挖属性组合再逐个 resolve。
            var (Ns, Path) = SplitId(id);
            List<string> combos = [];
            if (packs.TryRead($"assets/{Ns}/blockstates/{Path}.json", out var stateJson))
                combos = DigPropertyCombos(stateJson);

            if (combos.Count == 0) combos = [""];

            var faces = 0;
            foreach (var combo in combos)
            {
                var probe = combo.Length == 0 ? id : $"{id}[{combo}]";
                ResolvedBlockState state;
                try
                {
                    state = resolver.Resolve(probe);
                }
                catch (Exception ex)
                {
                    noVariants.Add($"{id} ({ex.GetType().Name})");
                    faces = -1;
                    break;
                }

                foreach (var variant in state.Variants)
                foreach (var element in variant.Model.Elements)
                {
                    faces += element.Faces.Count;
                    foreach (var face in element.Faces)
                        if (face.Sprite.Length > 0)
                        {
                            if (face.Sprite.EndsWith("all", StringComparison.Ordinal))
                                Console.WriteLine($"[ASSETS][scan]   裸all来源 {probe} model={variant.ModelId}");

                            allSprites.Add(face.Sprite);
                        }
                }
            }

            if (faces < 0) continue; // resolve 抛过异常，上面已点名

            faceCountById[id] = faces;
            if (faces == 0) noFaces.Add(id);
        }

        // 全量图集：把缺图名单一次拿全。
        var atlas = TextureAtlas.Build(packs, allSprites);

        Console.WriteLine($"[ASSETS][scan] blockstates={ids.Count} 有面={faceCountById.Count} " +
                          $"sprites={allSprites.Count} atlas={atlas.Width}x{atlas.Height}");
        Console.WriteLine($"[ASSETS][scan] noVariants={noVariants.Count}");
        foreach (var id in noVariants) Console.WriteLine($"[ASSETS][scan]   noVariants {id}");

        Console.WriteLine($"[ASSETS][scan] noFaces={noFaces.Count}");
        foreach (var id in noFaces) Console.WriteLine($"[ASSETS][scan]   noFaces {id}");

        Console.WriteLine($"[ASSETS][scan] missingSprites={atlas.MissingCount}");
        foreach (var sprite in atlas.MissingSprites) Console.WriteLine($"[ASSETS][scan]   missing {sprite}");

        return 0;
    }

    private static (string Ns, string Path) SplitId(string raw)
    {
        var colon = raw.IndexOf(':');
        return colon < 0 ? ("minecraft", raw) : (raw[..colon], raw[(colon + 1)..]);
    }

    // 从 blockstate JSON 挖出一组属性组合（k=v,k=v），足够让 variants/multipart
    // 至少命中一次。variants 模式取键里的属性；multipart 递归挖 when（含 OR/AND），
    // 每个属性取第一个见到的值（值带 | 取第一段）。挖不出就返回空，调用方按无属性兜底。
    private static List<string> DigPropertyCombos(byte[] json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Dictionary<string, string> props = new(StringComparer.Ordinal);
            if (root.TryGetProperty("variants", out var variants))
                foreach (var entry in variants.EnumerateObject())
                foreach (var pair in entry.Name.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var eq = pair.IndexOf('=');
                    if (eq > 0) props.TryAdd(pair[..eq], pair[(eq + 1)..]);
                }
            else if (root.TryGetProperty("multipart", out var multipart))
                foreach (var part in multipart.EnumerateArray())
                    if (part.TryGetProperty("when", out var when))
                        DigWhen(when, props);

            return [string.Join(",", props.Select(p => $"{p.Key}={p.Value}"))];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static void DigWhen(JsonElement when, Dictionary<string, string> props)
    {
        if (when.ValueKind != JsonValueKind.Object) return;

        foreach (var entry in when.EnumerateObject())
        {
            if (entry.Name is "OR" or "AND")
            {
                if (entry.Value.ValueKind == JsonValueKind.Array)
                    foreach (var sub in entry.Value.EnumerateArray())
                        DigWhen(sub, props);
                else
                    DigWhen(entry.Value, props);

                continue;
            }

            if (props.ContainsKey(entry.Name)) continue;

            var value = entry.Value.ValueKind switch
            {
                JsonValueKind.String => entry.Value.GetString() ?? "",
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Array => entry.Value.GetArrayLength() > 0 ? entry.Value[0].GetString() ?? "" : "",
                _ => ""
            };
            var bar = value.IndexOf('|');
            if (bar >= 0) value = value[..bar];

            props[entry.Name] = value;
        }
    }
}
