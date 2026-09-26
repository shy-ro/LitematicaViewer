using System.Diagnostics;
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
