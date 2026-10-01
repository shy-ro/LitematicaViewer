using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using LitematicaViewer.Assets;
using LitematicaViewer.Assets.Model;
using LitematicaViewer.Core.Model;
using LitematicaViewer.Core.Parsing;
using LitematicaViewer.Meshing.Extension;

namespace LitematicaViewer.Meshing.Tests;

// --preview：把包栈里每个方块状态按「三个面可见」的等距视图单独渲染成 PNG。
// 用途：材质解析 / 网格 uv 的故障（比如「铁块的面是空的」）要对账到单个状态，
// 靠在视口里找角度截图永远对不全；把每个状态的渲染产物铺成文件，
// 人查 + 程序查（完整方块内部透洞自动点名）都能落到位。
//
// 渲染走与 GPU 完全相同的前半段（resolver → BlockMeshBuilder → atlas 像素采样），
// 只有光栅化是软件的：z-buffer 三角形遍历 + NEAREST 采样 + 同一套 alpha cutout
// 与 tint 色板。所以材质链上的任何问题在这里必然复现；而 GPU 侧的问题
// （混合、mipmap）不会出现——那是刻意分开的：两边不一致本身就是一个判据。
internal static class PreviewBlocks
{
    private const int Side = 192;

    // montage 的底色。格子背景必须与它一致：格子之间不留缝、不画边框，
    // 只要两者不同色就会看到一圈深色方框（那正是「不同颜色的边框」的来源）。
    private const byte BackgroundR = 40;
    private const byte BackgroundG = 40;
    private const byte BackgroundB = 40;

    // 区域 alpha 加权均色（alpha>=128 才计色）。返回 (-1,-1,-1) 表示区域内无不透明纹素。
    // TexturePad 必须与 TextureAtlas.Pad 一致：那是 private 的，这里抄一份并注释钉死。
    private const int TexturePad = 8;

    // ---------- 软件光栅化 ----------

    // 等距视角：yaw 45°、pitch ≈ 33.7°，立方体的顶面与两个侧面同时可见。
    private static readonly Vector3 EyeDir = Vector3.Normalize(new Vector3(1f, 0.85f, 1f));
    private static readonly Vector3 Light = Vector3.Normalize(new Vector3(0.35f, 0.9f, 0.2f));

    private static readonly uint[] CrcTable = BuildCrcTable();

    public static int Run(string outDir, string[] packPaths)
    {
        return RunCore(outDir, null, packPaths);
    }

    // ---------- --regionrender：把真实 litematic 的整个 region 用软件光栅化画出来 ----------

    // 用途：GPU 截帧看着不对时，把同一条 BuildRegion 网格用软件光栅化再画一遍。
    // 软件这张用 level0 NEAREST 采样、无 mipmap、无混合——两边不一致就是 GPU 侧的问题；
    // 软件这张本身就花，就是网格/sprite 落位的问题。与 --montage 一样复用 RunCore 的
    // 装包与 atlas 构建，只是渲染对象从「单方块」换成「整个文档的每个 region」。
    public static int RunRegionRender(string[] args)
    {
        // args：--regionrender <out.png> <litematic> [--size N] <资源包...>。--size 不分先后，
        // 旧的 [边长] 位置写法仍认（放在 litematic 之后、资源包之前）。
        var outPath = args[1];
        var litematicPath = args[2];
        var side = 768;
        var hasSide = false;
        var packPaths = new List<string>();
        for (var i = 3; i < args.Length; i++)
        {
            if (args[i] == "--size")
            {
                if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out var namedSide))
                {
                    Console.Error.WriteLine("[MESH] --size 需要整数参数");
                    return 2;
                }

                side = Math.Max(128, namedSide);
                hasSide = true;
                i++;
            }
            else if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"[MESH] 未知开关: {args[i]}（可用: --size N）");
                return 2;
            }
            else
            {
                packPaths.Add(args[i]);
            }
        }

        if (!hasSide && packPaths.Count > 0 && int.TryParse(packPaths[0], out var posSide))
        {
            side = Math.Max(128, posSide);
            packPaths.RemoveAt(0);
        }
        using PackStack packs = new();
        foreach (var path in packPaths)
            packs.Add(Directory.Exists(path) ? ResourcePack.OpenFolder(path) : ResourcePack.OpenZip(path));

        BlockStateResolver resolver = new(packs);
        var result = LitematicLoader.TryLoadFile(litematicPath);
        if (!result.Success)
        {
            Console.Error.WriteLine($"[MESH][regionrender] 载入失败: {result.Error}");
            return 1;
        }

        // 走与 Sample/ShellPreview 完全相同的收集入口，确保真实区域里的告示牌文字、
        // 旗帜图案、画和物品展示框不会只在软件 regionrender 中丢失。
        HashSet<string> sprites = new(StringComparer.Ordinal);
        List<GeneratedSprite> generatedSprites = [];
        BlockMeshBuilder collector = new(resolver, TextureAtlas.Build(packs, []));
        collector.CollectSprites(result.Document!.Regions, sprites, packs, generatedSprites);
        var atlas = TextureAtlas.Build(packs, sprites, generatedSprites);
        Console.Out.WriteLine(
            $"[MESH][regionrender] 图集 {atlas.Width}x{atlas.Height} sprites={atlas.Rects.Count} 缺失={atlas.MissingCount}");
        BlockMeshBuilder builder = new(resolver, atlas);

        var regionIndex = 0;
        foreach (var region in result.Document.Regions)
        {
            var mesh = builder.BuildRegion(region);
            Vector3 centre = new(
                (region.Bounds.Min.X + region.Bounds.Max.X + 1f) / 2f,
                (region.Bounds.Min.Y + region.Bounds.Max.Y + 1f) / 2f,
                (region.Bounds.Min.Z + region.Bounds.Max.Z + 1f) / 2f);
            var radius = MathF.Max(
                region.Bounds.Size.X, MathF.Max(region.Bounds.Size.Y, region.Bounds.Size.Z)) / 2f;
            var rgb = RenderRegionToBuffer(mesh, atlas, side, centre, radius);
            var path = regionIndex == 0
                ? outPath
                : Path.Combine(
                    Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? ".",
                    $"{Path.GetFileNameWithoutExtension(outPath)}_r{regionIndex}{Path.GetExtension(outPath)}");
            WritePng(path, side, side, rgb);
            Console.Out.WriteLine($"[MESH][regionrender] {path} faces={mesh.Indices.Length / 6}");
            regionIndex++;
        }

        return 0;
    }

    // ---------- --montage：把 Render 的产物拼成一张网格大图 ----------

    // 与 --preview 走同一条 resolver → mesh → atlas → Render 链路，只是产物不落散图，
    // 而是按行序铺进一张 PNG。用途：快速人检「哪类方块不对劲」——翻几千张散图不现实，
    // 一张 montage 扫一眼就能圈出异常区，再回 --preview 拿单张细看。
    //
    // args：--montage <out.png> [开关...] <资源包...>。开关不分先后，全部可省：
    //   --names        每格左侧让出标签列写状态键（系统字体、纯黄字、按列宽断行不截断，全名另在 .txt 索引里）
    //   --nonames      显式关（默认即关），容错成对写法
    //   --nodup        关掉「模型+旋转」去重，每个解得出画面的状态各占一格
    //   --gap N        格间距像素，默认 0（格子同底色，本来也看不出缝），钳 0..64
    //   --filter S     对状态键做 OrdinalContains 过滤（"iron" 只看铁系），默认不过滤
    //   --cell N       每格像素边长，默认 128，下限 32
    //   --chunk N      每块格数，>0 时满一块就落盘一张再继续（第 1 张原名，之后 _2、_3…），0（默认）＝不分块
    // 旧的位置写法 [过滤子串] [格边长] [每块格数] 仍认，但那套里过滤位必须拿空串占位，
    // 而 PowerShell 会把 "" 整个吞掉、参数整体左移一格（128 顶到过滤位），所以一律改用具名开关。
    // 每张图同行写一个 .txt 索引。
    public static int RunMontage(string[] args)
    {
        // args[1] 是输出路径，已在 MeshSmoke.Main 分流处保证存在。
        var outPath = args[1];
        var names = false;
        var gap = 0;
        var noDup = false;
        var filter = string.Empty;
        var cell = 128;
        var chunkSize = 0;
        var named = false; // 用过 --filter/--cell/--chunk 任一就不认位置写法（--names/--nodup/--gap
                           // 本来就不占位置，不该因此废掉旧命令）。混写时位置串会落到资源包位报错，比静默错位好认。
        var rest = new List<string>();
        for (var i = 2; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--names") { names = true; continue; }
            if (arg == "--nonames") { names = false; continue; }
            if (arg == "--nodup") { noDup = true; continue; }

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                if (i + 1 >= args.Length)
                {
                    Console.Error.WriteLine($"[MESH] 开关 {arg} 缺参数值");
                    return 2;
                }

                var value = args[++i];
                switch (arg)
                {
                    case "--gap" when int.TryParse(value, out var g):
                        gap = Math.Clamp(g, 0, 64);
                        break;
                    case "--filter":
                        filter = value;
                        named = true;
                        break;
                    case "--cell" when int.TryParse(value, out var parsedCell):
                        cell = Math.Max(32, parsedCell);
                        named = true;
                        break;
                    case "--chunk" when int.TryParse(value, out var parsedChunk):
                        chunkSize = parsedChunk <= 0 ? 0 : parsedChunk;
                        named = true;
                        break;
                    default:
                        Console.Error.WriteLine($"[MESH] 未知开关或参数非法: {arg} {value}");
                        Console.Error.WriteLine(
                            "[MESH] 可用开关: --names --nonames --nodup --gap N --filter S --cell N --chunk N");
                        return 2;
                }

                continue;
            }

            rest.Add(arg);
        }

        if (!named)
        {
            // 兼容旧的位置写法：[过滤子串] [格边长] [每块格数]，都可选，遇到第一个资源包路径即停。
            // （资源包路径若与数字同名会被吃掉——真实包名没有纯数字的，值得为省解析器不设转义。）
            var scan = 0;
            if (scan < rest.Count && !File.Exists(rest[scan]) && !Directory.Exists(rest[scan])) filter = rest[scan++];

            if (scan < rest.Count && int.TryParse(rest[scan], out var posCell))
            {
                cell = Math.Max(32, posCell);
                scan++;
            }

            // 每块格数：0（或负）＝不分块，全部铺一张。>0 时满一块就落盘一张再继续。
            if (scan < rest.Count && int.TryParse(rest[scan], out var posChunk))
            {
                chunkSize = posChunk <= 0 ? 0 : posChunk;
                scan++;
            }

            rest = [.. rest.Skip(scan)];
        }

        var montageDir = Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? ".";
        return RunCore(montageDir, new MontageOptions(outPath, filter, cell, chunkSize, names, gap, noDup), [.. rest]);
    }

    public static int RunContainerMontage(string outDir, string[] packPaths)
    {
        Directory.CreateDirectory(outDir);
        using PackStack packs = new();
        foreach (var path in packPaths)
            packs.Add(Directory.Exists(path) ? ResourcePack.OpenFolder(path) : ResourcePack.OpenZip(path));
        BlockStateResolver resolver = new(packs);
        var facings = new[] { "north", "east", "south", "west" };
        string[] chestStates = [.. facings.Select(static facing =>
            $"minecraft:chest[facing={facing},type=single,waterlogged=false]")];
        string[] shulkerStates =
        [
            .. new[] { "up", "down", "north", "south", "east", "west" }
                .Select(static facing => $"minecraft:purple_shulker_box[facing={facing}]")
        ];
        RenderChest(Path.Combine(outDir, "chest-matrix.png"), chestStates, facings);
        Render(Path.Combine(outDir, "shulker-matrix.png"), shulkerStates);
        RenderSign(Path.Combine(outDir, "sign-debug.png"));
        return 0;

        void RenderSign(string path)
        {
            var state = ParseStateKey("minecraft:oak_sign[rotation=0,waterlogged=false]");
            var front = MapNbt(("messages", SeqNbt(
                    new NbtText("{\"text\":\"告示牌文字\"}"),
                    new NbtText("{\"text\":\"正面第二行\"}"),
                    new NbtText("{\"text\":\"像素字体测试\"}"),
                    new NbtText("{\"text\":\"0123456789\"}"))),
                ("color", new NbtText("black")));
            var back = MapNbt(("messages", SeqNbt(
                    new NbtText("{\"text\":\"BACK SIDE\"}"),
                    new NbtText("{\"text\":\"Line Two\"}"),
                    new NbtText("{\"text\":\"Glow Text\"}"),
                    new NbtText("{\"text\":\"ABC 123\"}"))),
                ("color", new NbtText("red")), ("has_glowing_text", new NbtInteger(1)));
            var region = MakeSingleBlockRegion(state) with
            {
                BlockEntities = ImmutableDictionary<Vector3I, BlockEntityData>.Empty.Add(
                    Vector3I.Zero, new BlockEntityData("minecraft:sign", Vector3I.Zero,
                        MapNbt(("front_text", front), ("back_text", back))))
            };
            HashSet<string> sprites = new(StringComparer.Ordinal);
            List<GeneratedSprite> generated = [];
            var collector = new BlockMeshBuilder(resolver, TextureAtlas.Build(packs, []));
            collector.CollectSprites([region], sprites, packs, generated);
            var atlas = TextureAtlas.Build(packs, sprites, generated);
            var mesh = new BlockMeshBuilder(resolver, atlas).BuildRegion(region);
            var rgb = RenderToBuffer(mesh, atlas, 300, BackgroundR).Rgb;
            var size = WriteMontage(path, [rgb], 300, ["oak_sign front/back"], true, 4);
            Console.WriteLine($"[MESH][containers] {Path.GetFullPath(path)} {size.Width}x{size.Height} sign");
        }

        static NbtMap MapNbt(params (string Key, NbtData Value)[] values) =>
            new(values.ToImmutableDictionary(static x => x.Key, static x => x.Value, StringComparer.Ordinal));

        static NbtSequence SeqNbt(params NbtData[] values) => new([.. values]);

        void Render(string path, IReadOnlyList<string> keys)
        {
            var states = keys.Select(ParseStateKey).ToArray();
            var regions = states.Select(MakeSingleBlockRegion).ToArray();
            HashSet<string> sprites = new(StringComparer.Ordinal);
            BlockMeshBuilder collector = new(resolver, TextureAtlas.Build(packs, []));
            collector.CollectSprites(regions, sprites);
            var atlas = TextureAtlas.Build(packs, sprites);
            BlockMeshBuilder builder = new(resolver, atlas);
            List<byte[]> tiles = [];
            foreach (var region in regions)
            {
                var mesh = builder.BuildRegion(region);
                tiles.Add(RenderToBuffer(mesh, atlas, 220, BackgroundR).Rgb);
            }
            var labels = keys.Select(static key => key.Replace("minecraft:", string.Empty, StringComparison.Ordinal)).ToList();
            var size = WriteMontage(path, tiles, 220, labels, true, 4);
            Console.WriteLine($"[MESH][containers] {Path.GetFullPath(path)} {size.Width}x{size.Height} states={keys.Count}");
        }

        void RenderChest(string path, IReadOnlyList<string> singles, IReadOnlyList<string> directions)
        {
            List<LitematicRegion> regions = [.. singles.Select(key => MakeSingleBlockRegion(ParseStateKey(key)))];
            List<string> labels = [.. singles.Select(static key => key.Replace("minecraft:", string.Empty, StringComparison.Ordinal))];
            foreach (var facing in directions)
            {
                // 摆位按原版：left 落在 facing 逆时针那一块（south/west 时是高坐标块）。
                // 详情与交叉核对见 --chest-dump。
                regions.Add(MakeDoubleChestRegion(facing, leftAtHigh: facing is "south" or "west"));
                labels.Add($"chest[facing={facing},double]");
            }
            HashSet<string> sprites = new(StringComparer.Ordinal);
            BlockMeshBuilder collector = new(resolver, TextureAtlas.Build(packs, []));
            collector.CollectSprites(regions, sprites);
            var atlas = TextureAtlas.Build(packs, sprites);
            BlockMeshBuilder builder = new(resolver, atlas);
            List<byte[]> tiles = [];
            foreach (var region in regions)
                tiles.Add(RenderToBuffer(builder.BuildRegion(region), atlas, 260, BackgroundR).Rgb);
            var size = WriteMontage(path, tiles, 260, labels, true, 4);
            Console.WriteLine($"[MESH][containers] {Path.GetFullPath(path)} {size.Width}x{size.Height} states={regions.Count}");
        }
    }

    // --chest-dump <out.png> <资源包...>：双箱「type=left 落在坐标大的那块还是小的那块」
    // 的对账图。三段：单箱参考 / 左半块放低坐标 / 左半块放高坐标。
    // 两段双箱在画面上必须能分辨：半张贴图的透明段是接缝，接缝朝里才连成一张大盖面，
    // 朝外就是一整块空面加中间一条缝。
    public static int RunChestDump(string outPath, string[] packPaths)
    {
        using PackStack packs = new();
        foreach (var path in packPaths)
            packs.Add(Directory.Exists(path) ? ResourcePack.OpenFolder(path) : ResourcePack.OpenZip(path));
        BlockStateResolver resolver = new(packs);
        var facings = new[] { "north", "east", "south", "west" };
        List<LitematicRegion> regions = [];
        List<string> labels = [];
        foreach (var facing in facings)
        {
            regions.Add(MakeSingleBlockRegion(
                ParseStateKey($"minecraft:chest[facing={facing},type=single,waterlogged=false]")));
            labels.Add($"single facing={facing}");
        }

        foreach (var facing in facings)
        {
            regions.Add(MakeDoubleChestRegion(facing, leftAtHigh: false));
            labels.Add($"double facing={facing} left@low");
        }

        foreach (var facing in facings)
        {
            regions.Add(MakeDoubleChestRegion(facing, leftAtHigh: true));
            labels.Add($"double facing={facing} left@high");
        }

        // 半块单独摆：双箱里的半个到底缺没缺面，只有把它单独拿出来才排得掉「被另一半遮住」。
        foreach (var type in new[] { "left", "right" })
        {
            regions.Add(MakeSingleBlockRegion(
                ParseStateKey($"minecraft:chest[facing=north,type={type},waterlogged=false]")));
            labels.Add($"half {type} alone facing=north");
        }

        HashSet<string> sprites = new(StringComparer.Ordinal);
        BlockMeshBuilder collector = new(resolver, TextureAtlas.Build(packs, []));
        collector.CollectSprites(regions, sprites);
        var atlas = TextureAtlas.Build(packs, sprites);
        BlockMeshBuilder builder = new(resolver, atlas);
        List<byte[]> tiles = [];
        var stateDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? ".", "states");
        Directory.CreateDirectory(stateDir);
        for (var ri = 0; ri < regions.Count; ri++)
        {
            var mesh = builder.BuildRegion(regions[ri]);
            // 临时探针：箱子半块的世界范围只有这一步能直接量到，别再靠推理。
            Vector3 lo = new(float.MaxValue), hi = new(float.MinValue);
            for (var i = 0; i < mesh.Vertices.Length / MeshData.FloatsPerVertex; i++)
            {
                Vector3 v = new(
                    mesh.Vertices[i * MeshData.FloatsPerVertex + MeshData.PositionOffset],
                    mesh.Vertices[i * MeshData.FloatsPerVertex + MeshData.PositionOffset + 1],
                    mesh.Vertices[i * MeshData.FloatsPerVertex + MeshData.PositionOffset + 2]);
                lo = Vector3.Min(lo, v);
                hi = Vector3.Max(hi, v);
            }

            Console.WriteLine($"[MESH][chest.dump] {labels[ri],-32} X {lo.X * 16:F2}..{hi.X * 16:F2}  Y {lo.Y * 16:F2}..{hi.Y * 16:F2}  Z {lo.Z * 16:F2}..{hi.Z * 16:F2}");
            tiles.Add(RenderToBuffer(mesh, atlas, 300, BackgroundR).Rgb);
            // 中缝/洞只有放大到看得清单像素才判得动，montage 的 300px 格不够——每格再单独落一张 800px。
            WriteMontage(Path.Combine(stateDir, $"s{ri:00}.png"),
                [RenderToBuffer(mesh, atlas, 800, BackgroundR).Rgb], 800, [labels[ri]], true, 6);
        }

        var size = WriteMontage(outPath, tiles, 300, labels, true, 6);
        Console.WriteLine($"[MESH][chest-dump] {Path.GetFullPath(outPath)} {size.Width}x{size.Height} states={regions.Count}");
        return 0;
    }

    // leftAtHigh：type=left 落在坐标大的那一块。
    // 原版反编译（写在 FallbackModels 箱子上方的注释里）：ChestType.LEFT 的搭档在 facing
    // 顺时针方向 → canonical（正面朝南）下 left 是东侧半块、接缝在西缘；北/东朝向反过来。
    // 也就是 facing 为 south/west 时 left 在高坐标块，north/east 时在低坐标块。
    private static LitematicRegion MakeDoubleChestRegion(string facing, bool leftAtHigh)
    {
        var left = ParseStateKey($"minecraft:chest[facing={facing},type=left,waterlogged=false]");
        var right = ParseStateKey($"minecraft:chest[facing={facing},type=right,waterlogged=false]");
        var alongX = facing is "north" or "south";
        Vector3I size = alongX ? new(2, 1, 1) : new(1, 1, 2);
        var firstIsLeft = !leftAtHigh;
        var palette = ImmutableArray.Create(firstIsLeft ? left : right, firstIsLeft ? right : left);
        return new LitematicRegion("double-chest", Vector3I.Zero, size,
            IntBounds.FromPositionSize(Vector3I.Zero, size), palette, ImmutableArray.Create(0, 1));
    }

    private static int RunCore(string outDir, MontageOptions? montage, string[] packPaths)
    {
        Directory.CreateDirectory(outDir);
        using TextWriterTraceListener listener = new(Console.Out);
        Trace.Listeners.Add(listener);
        Trace.AutoFlush = true;

        using PackStack packs = new();
        List<string> packNames = [];
        foreach (var path in packPaths)
        {
            // 路径不存在时 OpenZip 会抛 FileNotFoundException，Release 下用户只看到进程退出，
            // 没有上下文。这里点名道姓再退，比栈回溯好认。
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                Console.Error.WriteLine($"[MESH] 资源包不存在: {path}");
                return 2;
            }

            var pack = Directory.Exists(path) ? ResourcePack.OpenFolder(path) : ResourcePack.OpenZip(path);
            packs.Add(pack);
            packNames.Add(pack.Name);
            Debug.WriteLine($"[MESH][preview] 装包 {pack.Name}");
        }

        // 下面这组 Console.Out 是给命令行用户的：Debug.WriteLine 在 Release 下被整个移除，
        // 光靠它跑 montage 时终端一片空白，会被当成「没跑」。Console 只打关键节点，不打细节。
        Console.Out.WriteLine(packNames.Count == 0
            ? "[MESH] 未装资源包（图集会全空）"
            : $"[MESH] 装包 {packNames.Count} 个（栈底→栈顶）: {string.Join(" | ", packNames)}");

        BlockStateResolver resolver = new(packs);

        // 1) 枚举状态：variants 的每个键就是一个状态（键串即属性）；
        //    multipart 只留空属性一档——材质检查用不到连接态的几何差异。
        //    montage 的过滤在枚举后立刻做：过滤掉的状态连 resolve 都省了。
        var states = EnumerateStates(packs);
        if (montage is { Filter.Length: > 0 } m)
            states = states.Where(s =>
                s.Id.Contains(m.Filter, StringComparison.Ordinal) ||
                s.Props.Contains(m.Filter, StringComparison.Ordinal)).ToList();

        Debug.WriteLine($"[MESH][preview] 状态总数 {states.Count}");
        Console.Out.WriteLine(montage is { Filter.Length: > 0 } fm
            ? $"[MESH] 状态 {states.Count}（已按过滤 \"{fm.Filter}\" 筛过）"
            : $"[MESH] 状态 {states.Count}");

        // 2) 第一遍 resolve：收集 sprite，顺带定下要出图的格子。默认按「模型+旋转」去重——
        //    台阶 128 个状态的画面只有 8 种，铺 128 张只会淹掉真正异常的那张。
        //    montage 的 --nodup 关掉去重：每个解得出画面的状态各占一格，索引因此没有缺口
        //    （代价是格子数翻倍——26.3 是 6885 → 9179）。去重丢掉的是「同画面的重复」和
        //    「被别的方块先占了签名的那一行」，像素不丢，但查「某方块在哪一格」会查不到。
        var dedup = montage is null || !montage.NoDup;
        Dictionary<string, string> unique = new(StringComparer.Ordinal);
        List<string> renderKeys = [];
        HashSet<string> sprites = new(StringComparer.Ordinal);
        List<string> resolveFailures = [];
        foreach (var (id, props) in states)
        {
            var key = props.Length == 0 ? id : $"{id}[{props}]";
            ResolvedBlockState resolved;
            try
            {
                resolved = resolver.Resolve(key);
            }
            catch (Exception ex)
            {
                resolveFailures.Add($"{key} ({ex.GetType().Name}: {ex.Message})");
                continue;
            }

            if (resolved.Variants.Count == 0)
            {
                resolveFailures.Add($"{key} (无 variant)");
                continue;
            }

            if (dedup)
            {
                var signature = string.Join(
                    ';',
                    resolved.Variants.Select(v => $"{v.ModelId}@{v.XDegrees},{v.YDegrees}"));
                if (!unique.TryAdd(signature, key)) continue;
            }

            renderKeys.Add(key);

            var spriteEnumerable = from variant in resolved.Variants
                from element in variant.Model.Elements
                from face in element.Faces
                where face.Sprite.Length > 0
                select face.Sprite;
            sprites.AddMany(spriteEnumerable);
        }

        Debug.WriteLine(
            $"[MESH][preview] 出图格 {renderKeys.Count}（去重 {(dedup ? "开" : "关")}）" +
            $" sprites={sprites.Count} 解析失败={resolveFailures.Count}");
        Console.Out.WriteLine(
            $"[MESH] 出图格 {renderKeys.Count}（去重 {(dedup ? "开" : "关")}） sprite {sprites.Count} 解析失败 {resolveFailures.Count}"
            + (montage is { ChunkSize: > 0 } lm ? $"，每块 {lm.ChunkSize} 格" : string.Empty));

        var atlas = TextureAtlas.Build(packs, sprites);
        Debug.WriteLine(
            $"[MESH][preview] 图集 {atlas.Width}x{atlas.Height} 缺图={atlas.MissingCount}");
        Console.Out.WriteLine(
            $"[MESH] 图集 {atlas.Width}x{atlas.Height}（sprite {atlas.Rects.Count}）缺图 {atlas.MissingCount}");

        // 图集装了哪些 sprite、缺了哪些：排查「方块整块发白/品红」时先看这里，
        // 比翻图集 PNG 快。落盘到输出目录，两种模式都写。
        File.WriteAllLines(Path.Combine(outDir, "_sprites.txt"),
            sprites.OrderBy(s => s, StringComparer.Ordinal));

        // 每层 mip 直接落盘（RGBA 丢 alpha 通道）：深层 mip 的跨 sprite 串色
        // 在整图视角下一眼就能认出来，是排查「远处方块换色」的第一现场。
        // montage 模式不落这些中间产物。
        if (montage is null)
            for (var level = 0; level < atlas.Levels.Length; level++)
            {
                var lw = Math.Max(1, atlas.Width >> level);
                var lh = Math.Max(1, atlas.Height >> level);
                var rgbaLevel = atlas.Levels[level];
                var rgb = new byte[lw * lh * 3];
                for (var i = 0; i < lw * lh; i++)
                {
                    rgb[i * 3 + 0] = rgbaLevel[i * 4 + 0];
                    rgb[i * 3 + 1] = rgbaLevel[i * 4 + 1];
                    rgb[i * 3 + 2] = rgbaLevel[i * 4 + 2];
                }

                WritePng(Path.Combine(outDir, $"_atlas_L{level}.png"), lw, lh, rgb);
            }

        // 程序查 mip 串色：每层 cell 的均色要留在 L0 自身均色的邻域里。
        // padding 复制自己的边缘、下采样按 alpha 加权、膨胀借的也是自己的颜色——
        // 正常链路均色漂移很小；一旦混进邻居的颜色，均色立刻跳走。
        // 这是独立于生成器的复核（阈值宽松，只抓大面积污染，不做报警器做体检）。
        var mipDrift = AuditMipColorDrift(atlas);

        BlockMeshBuilder builder = new(resolver, atlas);

        // 3) 逐状态渲染。
        List<string> hollowCubes = [];
        List<string> emptyMeshes = [];
        var written = 0;
        List<string> montageKeys = [];
        List<byte[]>? tiles = montage is null ? null : [];
        var cell = montage?.Cell ?? Side;
        // 分块落盘的产物路径，供收尾汇总。chunkBase 是当前块第一格的全局序号，
        // 所以索引里的序号跨块连续（格在块内的位置 = 序号 % ChunkSize），按序号能反推块。
        List<string> chunkFiles = [];
        var chunkBase = 0;
        foreach (var key in renderKeys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var state = ParseStateKey(key);
            var mesh = builder.BuildRegion(MakeSingleBlockRegion(state));

            byte[] rgb;
            bool isFullCube;
            int holePixels;
            if (tiles is not null)
            {
                (rgb, isFullCube, holePixels) = RenderToBuffer(mesh, atlas, cell, BackgroundR);
                tiles.Add(rgb);
                montageKeys.Add(key);
                if (montage!.ChunkSize > 0 && tiles.Count >= montage.ChunkSize)
                {
                    chunkFiles.Add(FlushChunk(montage, tiles, montageKeys, cell, chunkBase, chunkFiles.Count));
                    chunkBase += tiles.Count;
                    tiles.Clear();
                    montageKeys.Clear();
                }
            }
            else
            {
                var path = Path.Combine(outDir, SafeFileName(key) + ".png");
                (rgb, isFullCube, holePixels) = RenderToBuffer(mesh, atlas, Side, 0);
                WritePng(path, Side, Side, rgb);
            }

            written++;
            if (written % 1000 == 0)
                Console.Out.WriteLine($"[MESH]   …渲染 {written}/{renderKeys.Count}");

            if (mesh.Indices.Length == 0) emptyMeshes.Add(key);

            if (isFullCube && holePixels > 4) hollowCubes.Add($"{key} holes={holePixels}");
        }

        // 收尾：最后不满一块的余量（ChunkSize=0 时就是全部）。
        if (tiles is { Count: > 0 } && montage is not null)
            chunkFiles.Add(FlushChunk(montage, tiles, montageKeys, cell, chunkBase, chunkFiles.Count));

        if (chunkFiles.Count > 0 && montage is not null)
            Console.Out.WriteLine(montage.ChunkSize > 0
                ? $"[MESH] 共 {chunkFiles.Count} 张，每块 {montage.ChunkSize} 格；索引为各同名 .txt（序号跨块连续）"
                : "[MESH] 单张全量（未分块），索引为同名 .txt");

        // 4) 汇总报告：人查 PNG，程序查两类铁律故障。
        var report = Path.Combine(outDir, "_report.txt");
        using (StreamWriter writer = new(report))
        {
            writer.WriteLine($"状态总数 {states.Count} / 出图格 {renderKeys.Count}（去重 {(dedup ? "开" : "关")}）" +
                $" / sprite {sprites.Count} / 输出 {written}");
            writer.WriteLine(
                $"图集 {atlas.Width}x{atlas.Height} 缺图 {atlas.MissingCount}: {string.Join(", ", atlas.MissingSprites)}");
            writer.WriteLine();
            writer.WriteLine($"== 解析失败 {resolveFailures.Count} ==");
            foreach (var line in resolveFailures) writer.WriteLine(line);

            writer.WriteLine();
            writer.WriteLine($"== 零面状态 {emptyMeshes.Count} ==");
            foreach (var line in emptyMeshes) writer.WriteLine(line);

            writer.WriteLine();
            writer.WriteLine($"== mip 层均色漂移超阈（跨 sprite 串色的量化信号，供参考）{mipDrift.Count} ==");
            foreach (var line in mipDrift) writer.WriteLine(line);

            writer.WriteLine();
            writer.WriteLine($"== 完整方块内部透洞（面材质空了的最直接证据）{hollowCubes.Count} ==");
            foreach (var line in hollowCubes) writer.WriteLine(line);

            if (chunkFiles.Count > 0)
            {
                writer.WriteLine();
                writer.WriteLine($"== montage 分块 {chunkFiles.Count} 张（每块 {(montage?.ChunkSize ?? 0)} 格）==");
                foreach (var line in chunkFiles) writer.WriteLine(line);
            }
        }

        Debug.WriteLine(
            $"[MESH][preview] 完成 输出={written} 零面={emptyMeshes.Count} " +
            $"透洞完整方块={hollowCubes.Count} 报告={report}");
        Console.Out.WriteLine(
            $"[MESH] 完成：出图 {written} 格，零面 {emptyMeshes.Count}，透洞完整方块 {hollowCubes.Count}"
            + $"，解析失败 {resolveFailures.Count}");
        Console.Out.WriteLine($"[MESH] 报告: {Path.GetFullPath(report)}");
        return 0;
    }

    // ---------- 状态枚举 ----------

    private static List<(string Id, string Props)> EnumerateStates(PackStack packs)
    {
        List<(string Id, string Props)> states = [];
        foreach (var path in packs.Enumerate("assets/"))
        {
            if (!path.Contains("/blockstates/", StringComparison.Ordinal) ||
                !path.EndsWith(".json", StringComparison.Ordinal))
                continue;

            var nsStart = "assets/".Length;
            var marker = path.IndexOf("/blockstates/", nsStart, StringComparison.Ordinal);
            var ns = path[nsStart..marker];
            var name = path[(marker + "/blockstates/".Length)..^".json".Length];
            var id = $"{ns}:{name}";
            if (BlockStateDefinition.IsAirName(id)) continue;

            if (!packs.TryRead(path, out var content)) continue;

            try
            {
                using var document = JsonDocument.Parse(content);
                if (document.RootElement.TryGetProperty("variants", out var variants) &&
                    variants.ValueKind == JsonValueKind.Object)
                    foreach (var variant in variants.EnumerateObject())
                        states.Add((id, variant.Name));
                else if (document.RootElement.TryGetProperty("multipart", out _)) states.Add((id, string.Empty));
            }
            catch (JsonException)
            {
                // 坏 JSON 走 resolve 时会以异常形式进失败名单，这里不重复记。
            }
        }

        return states;
    }

    private static BlockStateDefinition ParseStateKey(string key)
    {
        var bracket = key.IndexOf('[');
        if (bracket < 0) return new BlockStateDefinition(key, BlockStateDefinition.NoProperties);

        var id = key[..bracket];
        Dictionary<string, string> props = new(StringComparer.Ordinal);
        foreach (var part in key[(bracket + 1)..^1].Split(','))
        {
            var equals = part.IndexOf('=');
            if (equals > 0) props[part[..equals]] = part[(equals + 1)..];
        }

        return new BlockStateDefinition(id, props.ToImmutableDictionary(StringComparer.Ordinal));
    }

    private static LitematicRegion MakeSingleBlockRegion(BlockStateDefinition state)
    {
        Vector3I position = new(0, 0, 0);
        Vector3I size = new(1, 1, 1);
        return new LitematicRegion(
            "preview",
            position,
            size,
            IntBounds.FromPositionSize(position, size),
            ImmutableArray.Create(state),
            ImmutableArray.Create(0));
    }

    private static string SafeFileName(string key)
    {
        return key.Replace(':', '_');
    }

    // 8 个包围盒角在屏幕两轴上的最大投影半展宽。两轴共用一个缩放（保形），所以取两者的大值，
    // 横向与纵向都不会溢出。半径必须按屏幕展宽算，不能拿世界空间半径（对角半径 0.87、
    // 或干脆写 0.5）——等距投影下单位立方体的纵向展宽是 0.79，写 0.5 必然切掉顶/底面。
    private static float FitRadius(Vector3 min, Vector3 max, Vector3 centre, Vector3 right, Vector3 up)
    {
        var radius = 0f;
        for (var corner = 0; corner < 8; corner++)
        {
            Vector3 c = new(
                (corner & 1) == 0 ? min.X : max.X,
                (corner & 2) == 0 ? min.Y : max.Y,
                (corner & 4) == 0 ? min.Z : max.Z);
            var rel = c - centre;
            radius = MathF.Max(radius,
                MathF.Max(MathF.Abs(Vector3.Dot(rel, right)), MathF.Abs(Vector3.Dot(rel, up))));
        }

        return radius;
    }

    // region 模式入口：按传入的 region 包围盒取景，不做自适应。
    private static byte[] RenderRegionToBuffer(MeshData mesh, TextureAtlas atlas, int side, Vector3 centre,
        float radius)
    {
        return RenderGeneral(mesh, atlas, side, centre, radius, false, false).Rgb;
    }

    // 单方块入口：取景由网格自身的包围盒推得（autoFit）。
    // 曾经写死 centre=(0.5,0.5,0.5)、radius=0.5，等于假定「球半径 0.5」——但等距投影下
    // 单位立方体的屏纵向半展宽是 0.79（横向 0.71），顶面/底面本就溢出格子被切；高度
    // 1.5 的栅栏、2 的门、1/16 的地毯更惨。自适应后每个方块都完整装进格子。
    private static (byte[] Rgb, bool IsFullCube, int HolePixels) RenderToBuffer(MeshData mesh, TextureAtlas atlas,
        int side, byte background)
    {
        return RenderGeneral(mesh, atlas, side, default, 0f, true, true, background);
    }

    // 通用软件光栅化：等距视角（yaw 45°、pitch≈33.7°），正交投影。
    // autoFit=true 时 centre/radius 由网格包围盒推得（单方块用）；false 时按传入值取景（region 用）。
    // keepFullCubeInfo 只在单方块模式下有意义，region 模式恒 false。
    private static (byte[] Rgb, bool IsFullCube, int HolePixels) RenderGeneral(
        MeshData mesh, TextureAtlas atlas, int side, Vector3 centre, float radius, bool autoFit,
        bool keepFullCubeInfo, byte background = 0)
    {
        var rgb = new byte[side * side * 3];
        if (background != 0)
            for (var i = 0; i < side * side; i++)
            {
                rgb[i * 3 + 0] = background;
                rgb[i * 3 + 1] = background;
                rgb[i * 3 + 2] = background;
            }

        var zbuf = new float[side * side];
        Array.Fill(zbuf, float.NegativeInfinity);

        var vertexCount = mesh.Vertices.Length / MeshData.FloatsPerVertex;
        if (vertexCount == 0) return (rgb, false, 0);

        // right = up × eyeDir（lookAt 的 x 轴）；up 由两者叉积闭合。
        var right = Vector3.Normalize(Vector3.Cross(new Vector3(0, 1, 0), EyeDir));
        var up = Vector3.Cross(EyeDir, right);

        if (autoFit)
        {
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            for (var i = 0; i < vertexCount; i++)
            {
                Vector3 p = new(
                    mesh.Vertices[i * MeshData.FloatsPerVertex + MeshData.PositionOffset],
                    mesh.Vertices[i * MeshData.FloatsPerVertex + MeshData.PositionOffset + 1],
                    mesh.Vertices[i * MeshData.FloatsPerVertex + MeshData.PositionOffset + 2]);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }

            centre = (min + max) * 0.5f;
            // 尺度下限锚在单位立方体的实际投影展宽上：≤1 格的方块彼此保持同尺（矮的看起来
            // 就是矮的、小的就是小的），只有更高的方块（栅栏 1.5、门 2）才为装下而整体缩小。
            // 没有这个下限，火把/按钮会被放大到和方块一样大，失去尺寸参照。
            radius = MathF.Max(
                FitRadius(Vector3.Zero, Vector3.One, new Vector3(0.5f), right, up),
                FitRadius(min, max, centre, right, up));
        }

        // 边距按比例留（5%）：montage 的 96px 格和单图的 192px 共用同一个取景逻辑。
        var scale = side / 2f * 0.9f / MathF.Max(radius, 1e-3f);

        // 这里不能 stackalloc：顶点数随网格规模走，单方块入口只有几十个（栈上无所谓），
        // 但 --regionrender 走的是同一个函数、顶点上到几十万——12 字节一个直接撑爆默认
        // 1MB 栈（bars16.litematic 就是死在这里的 Stack overflow）。一律堆分配。
        var screen = new Vector3[vertexCount];
        for (var i = 0; i < vertexCount; i++)
        {
            Vector3 p = new(
                mesh.Vertices[i * MeshData.FloatsPerVertex + MeshData.PositionOffset],
                mesh.Vertices[i * MeshData.FloatsPerVertex + MeshData.PositionOffset + 1],
                mesh.Vertices[i * MeshData.FloatsPerVertex + MeshData.PositionOffset + 2]);
            var rel = p - centre;
            screen[i] = new Vector3(
                side / 2f + Vector3.Dot(rel, right) * scale,
                side / 2f - Vector3.Dot(rel, up) * scale,
                Vector3.Dot(rel, EyeDir));
        }

        var isFullCube = false;
        if (keepFullCubeInfo)
        {
            // 完整方块判据：恰好 6 个面、法线全部轴向对齐、包围盒占满单位立方体。
            isFullCube = mesh.Indices.Length / 6 == 6 && vertexCount == 24;
            if (isFullCube)
            {
                Vector3 min = new(float.MaxValue), max = new(float.MinValue);
                for (var i = 0; i < vertexCount; i++)
                {
                    Vector3 normal = new(
                        mesh.Vertices[i * MeshData.FloatsPerVertex + 3],
                        mesh.Vertices[i * MeshData.FloatsPerVertex + 4],
                        mesh.Vertices[i * MeshData.FloatsPerVertex + 5]);
                    var length = normal.Length();
                    if (length < 0.9f || length > 1.1f ||
                        !(MathF.Abs(normal.X) is > 0.9f or < 0.1f) ||
                        !(MathF.Abs(normal.Y) is > 0.9f or < 0.1f) ||
                        !(MathF.Abs(normal.Z) is > 0.9f or < 0.1f))
                    {
                        isFullCube = false;
                        break;
                    }

                    min = Vector3.Min(min, new Vector3(
                        mesh.Vertices[i * MeshData.FloatsPerVertex + 0],
                        mesh.Vertices[i * MeshData.FloatsPerVertex + 1],
                        mesh.Vertices[i * MeshData.FloatsPerVertex + 2]));
                    max = Vector3.Max(max, new Vector3(
                        mesh.Vertices[i * MeshData.FloatsPerVertex + 0],
                        mesh.Vertices[i * MeshData.FloatsPerVertex + 1],
                        mesh.Vertices[i * MeshData.FloatsPerVertex + 2]));
                }

                if (isFullCube &&
                    (min.X < -0.01f || min.Y < -0.01f || min.Z < -0.01f ||
                     max.X > 1.01f || max.Y > 1.01f || max.Z > 1.01f ||
                     max.X - min.X < 0.98f || max.Y - min.Y < 0.98f || max.Z - min.Z < 0.98f))
                    isFullCube = false;
            }
        }

        for (var tri = 0; tri < mesh.Indices.Length; tri += 3)
        {
            var i0 = mesh.Indices[tri];
            var i1 = mesh.Indices[tri + 1];
            var i2 = mesh.Indices[tri + 2];
            var s0 = screen[i0];
            var s1 = screen[i1];
            var s2 = screen[i2];

            var minX = MathF.Max(0, MathF.Floor(MathF.Min(s0.X, MathF.Min(s1.X, s2.X))));
            var maxX = MathF.Min(side - 1, MathF.Ceiling(MathF.Max(s0.X, MathF.Max(s1.X, s2.X))));
            var minY = MathF.Max(0, MathF.Floor(MathF.Min(s0.Y, MathF.Min(s1.Y, s2.Y))));
            var maxY = MathF.Min(side - 1, MathF.Ceiling(MathF.Max(s0.Y, MathF.Max(s1.Y, s2.Y))));
            if (maxX < minX || maxY < minY) continue;

            // 平面属性取首顶点：quads 是平的，法线与 tint 本来就逐面一份。
            Vector3 normal = new(
                mesh.Vertices[i0 * MeshData.FloatsPerVertex + 3],
                mesh.Vertices[i0 * MeshData.FloatsPerVertex + 4],
                mesh.Vertices[i0 * MeshData.FloatsPerVertex + 5]);
            var shade = 0.62f + 0.38f * MathF.Max(Vector3.Dot(normal, Light), 0f);
            var tintSlot = mesh.Vertices[i0 * MeshData.FloatsPerVertex + 8];
            var ao = mesh.Vertices[i0 * MeshData.FloatsPerVertex + MeshData.AoOffset];
            var tint = TintOf(tintSlot);

            var area = Edge(s0, s1, s2.X, s2.Y);
            if (MathF.Abs(area) < 1e-9f) continue;

            for (var y = (int)minY; y <= (int)maxY; y++)
            for (var x = (int)minX; x <= (int)maxX; x++)
            {
                var px = x + 0.5f;
                var py = y + 0.5f;
                // 重心坐标用循环边函数：l_k = E(下一顶点对, p) / area，
                // 循环不变式保证 l_k 在第 k 个顶点处取 1。第一版把 E01 的值
                // 配给了 i0 的属性——那实际是 i2 的权重，整张贴图沿对角线
                // 镜像错位，四边形两个三角形各错各的，看起来就是「面混一起」。
                var l0 = Edge(s1, s2, px, py) / area;
                var l1 = Edge(s2, s0, px, py) / area;
                var l2 = Edge(s0, s1, px, py) / area;
                if (l0 < 0 || l1 < 0 || l2 < 0) continue;

                var depth = l0 * s0.Z + l1 * s1.Z + l2 * s2.Z;
                var index = y * side + x;
                if (depth <= zbuf[index]) continue;

                var u = l0 * UvX(mesh, i0) + l1 * UvX(mesh, i1) + l2 * UvX(mesh, i2);
                var v = l0 * UvY(mesh, i0) + l1 * UvY(mesh, i1) + l2 * UvY(mesh, i2);

                // 与 GL 一致：v 不翻转，t=0 对应上传数据第一行（图集顶行），行 = v*H。
                var tx = Math.Clamp((int)(u * atlas.Width), 0, atlas.Width - 1);
                var ty = Math.Clamp((int)(v * atlas.Height), 0, atlas.Height - 1);
                var texel = (ty * atlas.Width + tx) * 4;
                if (atlas.Pixels[texel + 3] < 128) continue; // alpha cutout，与片元着色器同阈值

                zbuf[index] = depth;
                rgb[index * 3 + 0] = (byte)Math.Clamp(atlas.Pixels[texel + 0] * tint.X * shade * ao, 0, 255);
                rgb[index * 3 + 1] = (byte)Math.Clamp(atlas.Pixels[texel + 1] * tint.Y * shade * ao, 0, 255);
                rgb[index * 3 + 2] = (byte)Math.Clamp(atlas.Pixels[texel + 2] * tint.Z * shade * ao, 0, 255);
            }
        }

        // 完整方块的轮廓里不该有任何背景洞：内部一个背景像素四周全是画面，
        // 就是「面材质被 cutout 丢光」的直接形状证据。
        var holes = 0;
        if (isFullCube)
            for (var y = 1; y < side - 1; y++)
            for (var x = 1; x < side - 1; x++)
            {
                var index = y * side + x;
                if (zbuf[index] != float.NegativeInfinity) continue;

                if (zbuf[index - 1] != float.NegativeInfinity &&
                    zbuf[index + 1] != float.NegativeInfinity &&
                    zbuf[index - side] != float.NegativeInfinity &&
                    zbuf[index + side] != float.NegativeInfinity)
                    holes++;
            }

        return (rgb, isFullCube, holes);
    }

    private static float UvX(MeshData mesh, int vertex)
    {
        return mesh.Vertices[(vertex * MeshData.FloatsPerVertex) + MeshData.UvOffset];
    }

    private static float UvY(MeshData mesh, int vertex)
    {
        return mesh.Vertices[(vertex * MeshData.FloatsPerVertex) + MeshData.UvOffset + 1];
    }

    // 有向边函数 = 叉积 (b-a)×(p-a)，三角形内三点同号，绝对值之和 = 2×面积。
    private static float Edge(Vector3 a, Vector3 b, float px, float py)
    {
        return ((b.X - a.X) * (py - a.Y)) - ((b.Y - a.Y) * (px - a.X));
    }

    // 与片元着色器同一套固定色板（plains 群系）：0 不染 / 1 草 / 2 叶 / 3 水。
    // 两处必须同步改：这里是逐方块预览，那边是真渲染。
    private static Vector3 TintOf(float slot)
    {
        return slot switch
        {
            < 0.5f => new Vector3(1f),
            < 1.5f => new Vector3(0.569f, 0.741f, 0.349f),
            < 2.5f => new Vector3(0.467f, 0.671f, 0.184f),
            _ => new Vector3(0.247f, 0.463f, 0.894f)
        };
    }

    // ---------- 最小 PNG 编码：RGB8、无滤波、ZLibStream ----------

    // 每层 mip cell 的均色与 L0 整个 cell（含 padding）的均色比对，仅限「近全不透明」
    // 的 sprite（不透明覆盖 ≥95%）。基线必须取 cell 不能取内区：外扩边缘复制的是边缘色，
    // 边缘色≠内部色的贴图（原木年轮）会让比对产生与层无关的恒定假漂移（实测 44）。
    // 挖孔 sprite（花/叶/轨道）不参审：它们的深层形态由 alpha 膨胀主导（黑底被花色
    // 占据是设计行为，实测 allium 漂移 200+），均色比对对它们没有意义，靠 montage 人查。
    // 阈值宽松只抓大面积污染：不透明 sprite 实测正常漂移 ≤1，混进邻居颜色直接爆表。
    private static List<string> AuditMipColorDrift(TextureAtlas atlas)
    {
        List<string> flagged = [];
        foreach (var rect in atlas.Rects)
        {
            var cellX = rect.X - TexturePad;
            var cellY = rect.Y - TexturePad;
            var cellW = rect.Width + TexturePad * 2;
            var cellH = rect.Height + TexturePad * 2;
            if (OpaqueCoverage(atlas.Levels[0], atlas.Width, cellX, cellY, cellW, cellH) < 0.95f) continue;

            var (baseMeanR, baseMeanG, baseMeanB) = MeanColor(
                atlas.Levels[0], atlas.Width, cellX, cellY, cellW, cellH);
            if (baseMeanR < 0) continue; // 全透明 sprite 没有颜色可比

            for (var level = 1; level < atlas.Levels.Length; level++)
            {
                var lw = atlas.Width >> level;
                var lh = atlas.Height >> level;
                var lx = Math.Max(0, Math.Min(lw - 1, cellX >> level));
                var ly = Math.Max(0, Math.Min(lh - 1, cellY >> level));
                var cw = Math.Max(1, cellW >> level);
                var ch = Math.Max(1, cellH >> level);
                if (OpaqueCoverage(atlas.Levels[level], lw, lx, ly, cw, ch) < 0.95f) continue;

                var (r, g, b) = MeanColor(atlas.Levels[level], lw, lx, ly, cw, ch);
                if (r < 0) continue;

                var drift = Math.Max(Math.Max(Math.Abs(r - baseMeanR), Math.Abs(g - baseMeanG)),
                    Math.Abs(b - baseMeanB));
                if (drift > 30)
                    flagged.Add(
                        $"{rect.Sprite} L{level} drift={drift} base=({baseMeanR},{baseMeanG},{baseMeanB}) actual=({r},{g},{b})");
            }
        }

        return flagged.OrderBy(s => s, StringComparer.Ordinal).ToList();
    }

    // 区域内不透明纹素（alpha>=128）占比。
    private static float OpaqueCoverage(byte[] rgba, int width, int x, int y, int w, int h)
    {
        var opaque = 0;
        var total = 0;
        for (var row = y; row < y + h; row++)
        for (var col = x; col < x + w; col++)
        {
            var index = (row * width + col) * 4;
            if (index + 3 >= rgba.Length) continue;

            total++;
            if (rgba[index + 3] >= 128) opaque++;
        }

        return total == 0 ? 0f : opaque / (float)total;
    }

    private static (int R, int G, int B) MeanColor(byte[] rgba, int width, int x, int y, int w, int h)
    {
        long r = 0, g = 0, b = 0, count = 0;
        for (var row = y; row < y + h; row++)
        for (var col = x; col < x + w; col++)
        {
            var index = (row * width + col) * 4;
            if (index + 3 >= rgba.Length) continue;

            if (rgba[index + 3] >= 128)
            {
                r += rgba[index];
                g += rgba[index + 1];
                b += rgba[index + 2];
                count++;
            }
        }

        return count == 0 ? (-1, -1, -1) : ((int)(r / count), (int)(g / count), (int)(b / count));
    }

    // 把若干已渲染的方格拼成一张网格大图。列数按「总数开方向上取整」，接近正方最好扫视。
    // 背景与格子同色、格间距默认 0：格子之间没有缝也没有边框，方块之间靠自身的留白分开。
    // --names 时每格左侧让出一条标签列（状态键有自己的地盘，不压方块、也不截断）；
    // 标签列同时把相邻两格的方块隔开，所以没有间距也不会挤在一起。
    // 分块落盘：把一个 chunk 的格子写成 PNG + 同名 .txt 索引，返回 PNG 的绝对路径。
    // baseIndex 是该块第一格的全局序号——索引里的序号跨块连续，所以「某状态在哪一格」
    // 不因分块而改变（块号 = 序号 / ChunkSize，块内位置 = 序号 % ChunkSize）。
    private static string FlushChunk(MontageOptions montage, List<byte[]> tiles, List<string> keys, int cell,
        int baseIndex, int chunkIndex)
    {
        var path = ChunkPath(montage.OutPath, chunkIndex);
        var (width, height) = WriteMontage(path, tiles, cell, keys, montage.Names, montage.Gap);
        var indexPath = Path.ChangeExtension(path, ".txt");
        List<string> index = new(tiles.Count);
        for (var i = 0; i < keys.Count; i++) index.Add($"{baseIndex + i}: {keys[i]}");
        File.WriteAllLines(indexPath, index);
        Console.Out.WriteLine(
            $"[MESH] 第 {chunkIndex + 1} 张 {tiles.Count} 格 → {width}x{height}: {Path.GetFullPath(path)}");
        return Path.GetFullPath(path);
    }

    // 第 1 张沿用调用者给的原名，之后 _2、_3…（与 --regionrender 的多 region 命名一致）。
    private static string ChunkPath(string outPath, int chunkIndex)
    {
        if (chunkIndex == 0) return outPath;
        var dir = Path.GetDirectoryName(outPath);
        var name = Path.GetFileNameWithoutExtension(outPath);
        var ext = Path.GetExtension(outPath);
        var file = $"{name}_{chunkIndex + 1}{ext}";
        return dir is null ? file : Path.Combine(dir, file);
    }

    // 返回整图尺寸：RunCore 要把它打进 stdout（Release 下 Debug.WriteLine 全被裁掉，
    // 用户跑命令行时没有这条就完全看不到反馈，看起来像「没跑」）。
    private static (int Width, int Height) WriteMontage(string path, List<byte[]> tiles, int cell,
        List<string>? keys = null, bool names = false, int gap = 0)
    {
        var labelled = names && keys is not null;
        var labelW = labelled ? Math.Max(48, cell * 5 / 8) : 0;
        var tileW = cell + labelW;

        var columns = Math.Max(1, (int)MathF.Ceiling(MathF.Sqrt(tiles.Count)));
        var rows = Math.Max(1, (int)MathF.Ceiling(tiles.Count / (float)columns));
        var width = columns * tileW + (columns + 1) * gap;
        var height = rows * cell + (rows + 1) * gap;

        // 逐格行（band）写出，不落整图缓冲：--nodup 全量是 9179 格 = 20176x12288，
        // 整图 rgb 745MB + PNG 的 raw 再来 745MB，2G 起步；格子本身已经在 tiles 里，
        // 再存一份整图纯属浪费。一格行只有 width*cell*3 ≈ 7.7MB，压完就丢。
        using var file = File.Create(path);
        file.Write([137, 80, 78, 71, 13, 10, 26, 10]);

        var ihdr = new byte[13];
        Be32(ihdr, 0, (uint)width);
        Be32(ihdr, 4, (uint)height);
        ihdr[8] = 8; // 位深
        ihdr[9] = 2; // 真彩色
        WriteChunk(file, "IHDR", ihdr);

        byte[] idat;
        using (MemoryStream compressed = new())
        {
            using (ZLibStream zlib = new(compressed, CompressionLevel.Fastest))
            {
                var row = new byte[1 + width * 3];
                var gapRow = new byte[1 + width * 3];
                for (var p = 0; p < width; p++)
                {
                    gapRow[1 + p * 3 + 0] = BackgroundR;
                    gapRow[1 + p * 3 + 1] = BackgroundG;
                    gapRow[1 + p * 3 + 2] = BackgroundB;
                }

                var band = new byte[width * cell * 3];
                for (var ty = 0; ty < rows; ty++)
                {
                    for (var p = 0; p < width * cell; p++)
                    {
                        band[p * 3 + 0] = BackgroundR;
                        band[p * 3 + 1] = BackgroundG;
                        band[p * 3 + 2] = BackgroundB;
                    }

                    var first = ty * columns;
                    var last = Math.Min(first + columns, tiles.Count);
                    for (var tile = first; tile < last; tile++)
                    {
                        var tileX = gap + (tile - first) * (tileW + gap);
                        var originX = tileX + labelW;
                        var pixels = tiles[tile];
                        for (var y = 0; y < cell; y++)
                            Buffer.BlockCopy(pixels, y * cell * 3, band, (y * width + originX) * 3, cell * 3);

                        // 标签用 band 局部坐标（band 首行就是该格行的第一行，所以 y0=3 贴着上沿），
                        // band 宽即整图宽，所以横坐标不用换算。
                        if (labelled) DrawLabel(band, width, tileX + 3, 3, labelW - 8, cell - 6, keys![tile]);
                    }

                    for (var g = 0; g < gap; g++) zlib.Write(gapRow); // 行上留白
                    for (var y = 0; y < cell; y++)
                    {
                        row[0] = 0; // filter: None
                        Buffer.BlockCopy(band, y * width * 3, row, 1, width * 3);
                        zlib.Write(row);
                    }
                }

                for (var g = 0; g < gap; g++) zlib.Write(gapRow); // 行下留白
            }

            idat = compressed.ToArray();
        }

        WriteChunk(file, "IDAT", idat);
        WriteChunk(file, "IEND", []);
        return (width, height);
    }

    // 状态键标注：系统字体（GDI 默认 UI 字体 Segoe UI）、纯黄字、无底条。放在每格左侧的
    // 标签列里——不压方块、不截断（按列宽断行，字号在列宽×列高内自适应）。
    private static void DrawLabel(byte[] rgb, int imageWidth, int x0, int y0, int maxWidth, int maxHeight,
        string key)
    {
        // minecraft: 在 montage 语境是噪音，截掉省宽度。
        var text = key.StartsWith("minecraft:", StringComparison.Ordinal) ? key["minecraft:".Length..] : key;
        GdiText.DrawInto(rgb, imageWidth, x0, y0, maxWidth, maxHeight, text);
    }

    private static void WritePng(string path, int width, int height, byte[] rgb)
    {
        var raw = new byte[height * (1 + width * 3)];
        for (var y = 0; y < height; y++)
        {
            var row = y * (1 + width * 3);
            Buffer.BlockCopy(rgb, y * width * 3, raw, row + 1, width * 3);
        }

        using var file = File.Create(path);
        file.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

        var ihdr = new byte[13];
        Be32(ihdr, 0, (uint)width);
        Be32(ihdr, 4, (uint)height);
        ihdr[8] = 8; // 位深
        ihdr[9] = 2; // 真彩色
        WriteChunk(file, "IHDR", ihdr);

        byte[] idat;
        using (MemoryStream compressed = new())
        {
            using (ZLibStream zlib = new(compressed, CompressionLevel.Fastest))
            {
                zlib.Write(raw);
            }

            idat = compressed.ToArray();
        }

        WriteChunk(file, "IDAT", idat);
        WriteChunk(file, "IEND", []);
    }

    private static void Be32(byte[] target, int offset, uint value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var header = new byte[8];
        Be32(header, 0, (uint)data.Length);
        for (var i = 0; i < 4; i++) header[4 + i] = (byte)type[i];

        stream.Write(header);
        stream.Write(data);

        // PNG 的 CRC 只覆盖 type+data，header 里的 4 字节长度前缀不参与；
        // 算进去所有图 CRC 全错，严格解析器（PIL/浏览器）直接拒读。
        var crc = Crc32(header.AsSpan(4).ToArray(), data);
        var tail = new byte[4];
        Be32(tail, 0, crc);
        stream.Write(tail);
    }

    private static uint Crc32(byte[] a, byte[] b)
    {
        var table = CrcTable;
        var crc = 0xFFFFFFFF;
        foreach (var value in a) crc = table[(crc ^ value) & 0xFF] ^ (crc >> 8);

        foreach (var value in b) crc = table[(crc ^ value) & 0xFF] ^ (crc >> 8);

        return crc ^ 0xFFFFFFFF;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;

            table[i] = c;
        }

        return table;
    }

    private static void SetPx(byte[] rgb, int imageWidth, int x, int y, byte r, byte g, byte b)
    {
        if (x < 0 || y < 0) return;
        var i = (y * imageWidth + x) * 3;
        if (i + 2 >= rgb.Length) return;
        rgb[i] = r;
        rgb[i + 1] = g;
        rgb[i + 2] = b;
    }

    // GDI 文本绘制。不用 System.Drawing：Meshing 被 ShellPreview 以 NativeAOT 引用，
    // 托管 GDI+ 对 AOT/裁剪不友好，而目标只有 Windows，P/Invoke 更轻更可控。
    // 渲染到 32bpp DIB、以品红为透明键——字体必须关抗锯齿（NONANTIALIASED），
    // 否则字边混出的品红-黄过渡色会在方块上留一道粉边。
    private static class GdiText
    {
        private const string Gdi = "gdi32.dll";
        private const string User = "user32.dll";

        private const int BkTransparent = 1;
        private const uint TextYellow = 0x0000FFFF; // COLORREF 0x00BBGGRR
        private const byte KeyB = 255;
        private const byte KeyG = 0;
        private const byte KeyR = 255;
        private const uint NonAntialiased = 3;

        private static readonly IntPtr Dc = CreateCompatibleDC(IntPtr.Zero);
        private static IntPtr _font;
        private static int _fontPx;

        // 每种磅值只建一次字体；旧对象不回收——整轮最多十来个尺寸，进程随即退出。
        private static void SelectFont(int px)
        {
            if (_fontPx == px && _font != IntPtr.Zero) return;
            _font = CreateFontW(-px, 0, 0, 0, 400, 0, 0, 0, 0, 0, 0, NonAntialiased, 0, "Segoe UI");
            SelectObject(Dc, _font);
            _fontPx = px;
        }

        private static int Measure(string text) =>
            text.Length == 0 ? 0 : (GetTextExtentPoint32W(Dc, text, text.Length, out var size) ? size.Width : 0);

        // 断行：优先在 [ ] , : _ - = 后断（属性段、命名段天然成组），段内超宽按实测宽度硬切。
        // 下划线与等号也算边界：mod 的方块名多是 snake_case（linear_chassis），属性是
        // k=v（facing=south），只按标点断会把名字/属性从中间劈开，读起来很难受。
        private static List<string> Wrap(string text, int maxWidth)
        {
            List<string> tokens = [];
            var current = "";
            foreach (var ch in text)
            {
                current += ch;
                if (ch is ',' or '[' or ']' or ':' or '_' or '-' or '=')
                {
                    tokens.Add(current);
                    current = "";
                }
            }

            if (current.Length > 0) tokens.Add(current);

            List<string> lines = [];
            var line = "";
            foreach (var token in tokens)
            {
                var rest = token;
                if (Measure(rest) > maxWidth && line.Length > 0)
                {
                    lines.Add(line);
                    line = "";
                }

                while (rest.Length > 1 && Measure(rest) > maxWidth)
                {
                    var cut = rest.Length - 1;
                    while (cut > 1 && Measure(rest[..cut]) > maxWidth) cut--;
                    lines.Add(rest[..cut]);
                    rest = rest[cut..];
                }

                if (line.Length > 0 && Measure(line + rest) > maxWidth)
                {
                    lines.Add(line);
                    line = rest;
                }
                else
                {
                    line += rest;
                }
            }

            if (line.Length > 0) lines.Add(line);
            return lines;
        }

        // 文本按列宽自适应字号（列宽才是真约束：字号定得比列能容纳的大，只会立刻缩回来）、
        // 断行后按透明键叠进目标 RGB 缓冲。
        public static void DrawInto(byte[] rgb, int imageWidth, int x0, int y0, int maxWidth, int maxHeight,
            string text)
        {
            if (maxWidth < 8 || maxHeight < 8) return;

            var fontPx = Math.Clamp(maxWidth / 5, 11, 26);
            List<string> lines;
            int boxW;
            int boxH;
            while (true)
            {
                SelectFont(fontPx);
                lines = Wrap(text, maxWidth);
                boxH = lines.Count * (fontPx + 2);
                boxW = Math.Min(maxWidth, lines.Max(Measure));
                if (boxH <= maxHeight || fontPx <= 11) break;
                fontPx -= 2;
            }

            var info = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = boxW,
                    Height = -boxH, // 负高 = 自上而下，省一次翻转
                    Planes = 1,
                    BitCount = 32,
                },
            };

            var dib = CreateDIBSection(Dc, ref info, 0, out var bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero) return;

            var previous = SelectObject(Dc, dib);
            var stride = boxW * 4;
            var buffer = new byte[stride * boxH];
            for (var i = 0; i < buffer.Length; i += 4)
            {
                buffer[i + 0] = KeyB;
                buffer[i + 1] = KeyG;
                buffer[i + 2] = KeyR;
            }

            Marshal.Copy(buffer, 0, bits, buffer.Length);
            SetBkMode(Dc, BkTransparent);
            SetTextColor(Dc, TextYellow);
            for (var i = 0; i < lines.Count; i++)
                TextOutW(Dc, 0, i * (fontPx + 2), lines[i], lines[i].Length);

            Marshal.Copy(bits, buffer, 0, buffer.Length);
            SelectObject(Dc, previous);
            DeleteObject(dib);

            for (var y = 0; y < boxH; y++)
            for (var x = 0; x < boxW; x++)
            {
                var i = y * stride + x * 4;
                if (buffer[i] == KeyB && buffer[i + 1] == KeyG && buffer[i + 2] == KeyR) continue;
                SetPx(rgb, imageWidth, x0 + x, y0 + y, buffer[i + 2], buffer[i + 1], buffer[i]);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfoHeader
        {
            public uint Size;
            public int Width;
            public int Height;
            public ushort Planes;
            public ushort BitCount;
            public uint Compression;
            public uint SizeImage;
            public int XPelsPerMeter;
            public int YPelsPerMeter;
            public uint ClrUsed;
            public uint ClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfo
        {
            public BitmapInfoHeader Header;
            public uint Colors;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TextSize
        {
            public int Width;
            public int Height;
        }

        [DllImport(Gdi)] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport(Gdi)] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfo info,
            uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport(Gdi)] private static extern IntPtr CreateFontW(int height, int width, int escapement,
            int orientation, int weight, uint italic, uint underline, uint strikeOut, uint charSet,
            uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);
        [DllImport(Gdi)] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport(Gdi)] private static extern bool DeleteObject(IntPtr obj);
        [DllImport(Gdi)] private static extern uint SetTextColor(IntPtr hdc, uint color);
        [DllImport(Gdi)] private static extern int SetBkMode(IntPtr hdc, int mode);
        [DllImport(Gdi, CharSet = CharSet.Unicode)] private static extern bool TextOutW(IntPtr hdc, int x, int y,
            string text, int length);
        [DllImport(Gdi, CharSet = CharSet.Unicode)] private static extern bool GetTextExtentPoint32W(IntPtr hdc,
            string text, int length, out TextSize size);
    }

    // ChunkSize：每张图的格数，>0 时分块出多张（第 1 张用原文件名，之后 _2、_3…），
    // 0 = 不分块，全部铺进一张。分块不只是省内存（2000 格约 98MB tile，9265 格约 455MB），
    // 也让每张图小到能直接打开看。
    private sealed record MontageOptions(string OutPath, string Filter, int Cell, int ChunkSize, bool Names, int Gap,
        bool NoDup);
}
