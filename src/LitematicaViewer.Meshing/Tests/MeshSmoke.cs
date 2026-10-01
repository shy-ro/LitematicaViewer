using System.Diagnostics;
using System.Collections.Immutable;
using System.Numerics;
using LitematicaViewer.Assets;
using LitematicaViewer.Core.Model;
using LitematicaViewer.Core.Parsing;

namespace LitematicaViewer.Meshing.Tests;

// 网格层的打桩验收：合成方块钉不变量（顶点数、面剔除、旋转约定），
// 真实文件看量级与耗时。args：资源包（jar/zip/文件夹）若干 + .litematic 文件若干（按扩展名分流）。
public static class MeshSmoke
{
    private static int _checks;
    private static PackStack _packs = null!;
    private static BlockStateResolver _resolver = null!;

    private static readonly BlockStateDefinition Stone = new("minecraft:stone", BlockStateDefinition.NoProperties);
    private static readonly BlockStateDefinition Air = new("minecraft:air", BlockStateDefinition.NoProperties);

    public static int Main(string[] args)
    {
        // 中文 Windows 的 Console 默认走 OEM 代码页 936（GBK），而 Git Bash / Windows Terminal
        // 按 UTF-8 解码，中文提示会全变乱码。统一成 UTF-8。重定向到文件时这行会抛
        // IOException（句柄不是控制台），所以吞掉——那种场景下一律是 UTF-8 字节，也正确。
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.InputEncoding = System.Text.Encoding.UTF8;
        }
        catch (IOException)
        {
        }

        if (args.Length >= 3 && args[0] == "--entity-corpus")
            return AuditEntityCorpus(args[1], args.Skip(2));

        if (args.Length >= 3 && args[0] == "--container-montage")
            return PreviewBlocks.RunContainerMontage(args[1], [.. args.Skip(2)]);

        if (args.Length >= 3 && args[0] == "--chest-dump")
            return PreviewBlocks.RunChestDump(args[1], [.. args.Skip(2)]);

        if (args.Length >= 3 && args[0] == "--entity-audit")
        {
            using TextWriterTraceListener auditListener = new(Console.Out);
            Trace.Listeners.Add(auditListener);
            Trace.AutoFlush = true;
            _packs = new PackStack();
            foreach (var packPath in args.Skip(2))
                _packs.Add(Directory.Exists(packPath)
                    ? ResourcePack.OpenFolder(packPath)
                    : ResourcePack.OpenZip(packPath));
            _resolver = new BlockStateResolver(_packs);
            CheckRealFile(args[1]);
            Debug.WriteLine($"[MESH][entity.audit] completed file={args[1]} checks={_checks}");
            return 0;
        }

        // --resolve <blockId> <资源包...>：打印一个状态串的解析结果（命中哪些模型、
        // 每个模型的盒子与面数），排查 variant/when 匹配问题用。
        if (args.Length >= 3 && args[0] == "--resolve")
        {
            using TextWriterTraceListener resolveListener = new(Console.Out);
            Trace.Listeners.Add(resolveListener);
            Trace.AutoFlush = true;
            PackStack resolvePacks = new();
            foreach (var packPath in args.Skip(2))
                resolvePacks.Add(Directory.Exists(packPath)
                    ? ResourcePack.OpenFolder(packPath)
                    : ResourcePack.OpenZip(packPath));
            BlockStateResolver resolveResolver = new(resolvePacks);
            var resolved = resolveResolver.Resolve(args[1]);
            Debug.WriteLine($"[MESH][resolve] {args[1]} -> {resolved.Variants.Count} variant(s)");
            foreach (var variant in resolved.Variants)
            {
                var boxes = variant.Model.Elements.Count;
                var faces = variant.Model.Elements.Sum(e => e.Faces.Count);
                Debug.WriteLine(
                    $"[MESH][resolve]   model={variant.ModelId} rot=({variant.XDegrees},{variant.YDegrees}) elements={boxes} faces={faces}");
                foreach (var element in variant.Model.Elements)
                    Debug.WriteLine($"[MESH][resolve]     box from={element.From} to={element.To}");
            }

            return 0;
        }

        // --preview <outDir> <资源包...>：逐方块状态出三面视图 PNG（见 PreviewBlocks）。
        if (args.Length >= 2 && args[0] == "--preview") return PreviewBlocks.Run(args[1], [.. args.Skip(2)]);

        // --montage <out.png> [开关...] <资源包...>：同一条 Render 链路拼成网格大图，
        // 快速人检某类方块。开关（--names/--nodup/--gap N/--filter S/--cell N/--chunk N）
        // 不分先后，见 PreviewBlocks.RunMontage。
        if (args.Length >= 2 && args[0] == "--montage") return PreviewBlocks.RunMontage(args);

        // --probe <litematic> <资源包...>：按网格层的读序打印角上 5x5x5 的状态串，
        // 给「渲染器和第三方解码器谁对」做逐坐标对账用。资源包参数其实用不上，
        // 接着只是让分流统一。
        if (args.Length >= 2 && args[0] == "--probe")
        {
            using TextWriterTraceListener probeListener = new(Console.Out);
            Trace.Listeners.Add(probeListener);
            Trace.AutoFlush = true;
            var probeResult = LitematicLoader.TryLoadFile(args[1]);
            if (!probeResult.Success)
            {
                Debug.WriteLine($"[MESH][probe] 载入失败 {probeResult.Error}");
                return 1;
            }

            var probeRegion = probeResult.Document!.Regions[0];
            var probeSize = probeRegion.Bounds.Size;
            // 全区域状态计数：与第三方解码器的逐状态计数对账。
            // 非空气计数一致不证明空间映射一致（计数与布局无关），逐状态计数才抓得住错位。
            // 逐非空气坐标 dump：第三方解码器同序 dump 后 diff，
            // 抓「计数一致但空间错位」这类布局 bug 的唯一硬证据。
            for (var py = 0; py < probeSize.Y; py++)
            for (var pz = 0; pz < probeSize.Z; pz++)
            for (var px = 0; px < probeSize.X; px++)
            {
                var st = probeRegion.GetState(new Vector3I(px, py, pz));
                if (!st.IsAir) Debug.WriteLine($"[MESH][probe] {px},{py},{pz}={st}");
            }

            return 0;
        }

        // --regionrender <out.png> <litematic> [--size N] <资源包...>：真实 region 的软件光栅化，
        // 与 GPU 截帧对照用（见 PreviewBlocks.RunRegionRender）。
        if (args.Length >= 3 && args[0] == "--regionrender") return PreviewBlocks.RunRegionRender(args);

        // --dumpverts <litematic> <资源包...>：打印头几个面的顶点原始数据，
        // 查面退化（四角几乎重合）时用。
        if (args.Length >= 3 && args[0] == "--dumpverts")
        {
            using TextWriterTraceListener dvListener = new(Console.Out);
            Trace.Listeners.Add(dvListener);
            Trace.AutoFlush = true;
            var dvResult = LitematicLoader.TryLoadFile(args[1]);
            if (!dvResult.Success)
            {
                Debug.WriteLine($"[MESH][dumpverts] 载入失败 {dvResult.Error}");
                return 1;
            }

            _packs = new PackStack();
            foreach (var path in args.Skip(2))
                _packs.Add(Directory.Exists(path) ? ResourcePack.OpenFolder(path) : ResourcePack.OpenZip(path));

            _resolver = new BlockStateResolver(_packs);
            BlockMeshBuilder dvCollector = new(_resolver, TextureAtlas.Build(_packs, []));
            HashSet<string> dvSprites = [];
            dvCollector.CollectSprites(dvResult.Document!.Regions, dvSprites);
            var dvAtlas = TextureAtlas.Build(_packs, dvSprites);
            BlockMeshBuilder dvBuilder = new(_resolver, dvAtlas);
            var dvMesh = dvBuilder.BuildRegion(dvResult.Document.Regions[0]);
            var fpv = MeshData.FloatsPerVertex;
            Debug.WriteLine(
                $"[MESH][dumpverts] verts={dvMesh.Vertices.Length / fpv} indices={dvMesh.Indices.Length} atlas={dvAtlas.Width}x{dvAtlas.Height}");
            for (var face = 0; face < dvMesh.Indices.Length; face += 6)
            for (var k = 0; k < 4; k++)
            {
                var v = dvMesh.Indices[face + k] * fpv;
                Debug.WriteLine(
                    $"[MESH][dumpverts] face{face / 6} v{k} pos=({dvMesh.Vertices[v]:F3},{dvMesh.Vertices[v + 1]:F3},{dvMesh.Vertices[v + 2]:F3}) n=({dvMesh.Vertices[v + 3]:F2},{dvMesh.Vertices[v + 4]:F2},{dvMesh.Vertices[v + 5]:F2}) uv=({dvMesh.Vertices[v + 6]:F5},{dvMesh.Vertices[v + 7]:F5}) tint={dvMesh.Vertices[v + 8]:F0}");
            }

            return 0;
        }

        // --uvhist <litematic> <资源包...>：复刻 Sample 的 LoadCore 全流程
        // （CollectSprites → 建图集 → BuildRegion → MergeMeshes），然后逐面取 uv 中心，
        // 统计落在每个 sprite rect 内的面数。铁块 rect 应该有几千个面；
        // 如果铁块名下只有个位数，就是 uv 映射错位，而不是 GPU 的问题。
        if (args.Length >= 3 && args[0] == "--uvhist")
        {
            using TextWriterTraceListener uvListener = new(Console.Out);
            Trace.Listeners.Add(uvListener);
            Trace.AutoFlush = true;
            var uvResult = LitematicLoader.TryLoadFile(args[1]);
            if (!uvResult.Success)
            {
                Debug.WriteLine($"[MESH][uvhist] 载入失败 {uvResult.Error}");
                return 1;
            }

            _packs = new PackStack();
            foreach (var path in args.Skip(2))
                _packs.Add(Directory.Exists(path) ? ResourcePack.OpenFolder(path) : ResourcePack.OpenZip(path));

            _resolver = new BlockStateResolver(_packs);
            BlockMeshBuilder uvCollector = new(_resolver, TextureAtlas.Build(_packs, []));
            HashSet<string> uvSprites = [];
            uvCollector.CollectSprites(uvResult.Document!.Regions, uvSprites);
            var uvAtlas = TextureAtlas.Build(_packs, uvSprites);
            BlockMeshBuilder uvBuilder = new(_resolver, uvAtlas);

            List<float> uvVerts = [];
            List<int> uvIndices = [];
            var uvBase = 0;
            foreach (var uvRegion in uvResult.Document.Regions)
            {
                var uvMesh = uvBuilder.BuildRegion(uvRegion);
                uvVerts.AddRange(uvMesh.Vertices);
                foreach (var i in uvMesh.Indices) uvIndices.Add(i + uvBase);

                uvBase += uvMesh.Vertices.Length / MeshData.FloatsPerVertex;
            }

            // 每 4 个连续顶点是一个面（四边形），uv 中心取四顶点平均。
            Dictionary<string, int> rectHits = new(StringComparer.Ordinal);
            var straddleFaces = 0;
            for (var face = 0; face < uvIndices.Count; face += 6)
            {
                float uSum = 0, vSum = 0;
                for (var k = 0; k < 6; k++)
                {
                    var vertex = uvIndices[face + k];
                    uSum += uvVerts[vertex * MeshData.FloatsPerVertex + MeshData.UvOffset];
                    vSum += uvVerts[vertex * MeshData.FloatsPerVertex + MeshData.UvOffset + 1];
                }

                // 6 个索引只有 4 个独立顶点；四点平均直接用头 4 个索引去重即可，
                // 这里偷懒用 6 个索引的平均（对中心位置没有影响，权重差可忽略）。
                var u = uSum / 6f;
                // v 不翻转（v = 图集 y/H），反推图集行：py = v*H。
                var v = vSum / 6f;
                var px = u * uvAtlas.Width;
                var py = v * uvAtlas.Height;
                string? hit = null;
                foreach (var rect in uvAtlas.Rects)
                    if (px >= rect.X && px < rect.X + rect.Width && py >= rect.Y && py < rect.Y + rect.Height)
                    {
                        hit = rect.Sprite;
                        break;
                    }

                if (hit is null)
                    straddleFaces++;
                else
                    rectHits[hit] = rectHits.GetValueOrDefault(hit) + 1;
            }

            Debug.WriteLine($"[MESH][uvhist] 图集 {uvAtlas.Width}x{uvAtlas.Height} sprites={uvAtlas.Rects.Count}");
            foreach (var (sprite, count) in rectHits.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                Debug.WriteLine($"[MESH][uvhist] {sprite} faces={count}");

            Debug.WriteLine($"[MESH][uvhist] rect 外的面={straddleFaces}");
            return 0;
        }

        using TextWriterTraceListener listener = new(Console.Out);
        Trace.Listeners.Add(listener);
        Trace.AutoFlush = true;

        _packs = new PackStack();
        List<string> litematics = [];
        foreach (var path in args)
            if (path.EndsWith(".litematic", StringComparison.OrdinalIgnoreCase))
            {
                litematics.Add(path);
            }
            else
            {
                var pack = Directory.Exists(path) ? ResourcePack.OpenFolder(path) : ResourcePack.OpenZip(path);
                _packs.Add(pack);
                Debug.WriteLine($"[MESH][smoke] 装包 {pack.Name}");
            }

        _resolver = new BlockStateResolver(_packs);

        CheckSingleBlock();
        CheckFaceCulling();
        CheckAmbientOcclusion();
        CheckLogAxisRotation();
        CheckWallTorchElementRotation();
        CheckFluidRules();
        CheckSignFrontAndBack();
        CheckSignGeometry();
        CheckPaintingGeometry();
        CheckArmorStandGeometry();
        CheckDroppedItemAndBoat();
        CheckStaticMobs();
        foreach (var path in litematics) CheckRealFile(path);

        Debug.WriteLine($"[MESH][smoke] 完成 checks={_checks}");
        return 0;
    }

    private static BlockMeshBuilder CreateBuilder(params IEnumerable<string> extraSprites)
    {
        HashSet<string> sprites = [];
        foreach (var sprite in extraSprites) sprites.Add(sprite);

        var atlas = TextureAtlas.Build(_packs, sprites);
        Debug.WriteLine(
            $"[MESH][smoke] 图集 {atlas.Width}x{atlas.Height} sprites={atlas.Rects.Count} 缺失={atlas.MissingCount}");
        return new BlockMeshBuilder(_resolver, atlas);
    }

    private static int AuditEntityCorpus(string reportPath, IEnumerable<string> roots)
    {
        Dictionary<string, (int Count, HashSet<string> Files)> blockEntities = new(StringComparer.Ordinal);
        Dictionary<string, (int Count, HashSet<string> Files)> entities = new(StringComparer.Ordinal);
        List<string> failures = [];
        var files = roots.SelectMany(root => Directory.Exists(root)
                ? Directory.EnumerateFiles(root, "*.litematic", SearchOption.AllDirectories)
                : File.Exists(root) ? [root] : [])
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var path in files)
        {
            var result = LitematicLoader.TryLoadFile(path);
            if (!result.Success || result.Document is null)
            {
                failures.Add($"{path}\t{result.Error}\t{result.Message}");
                continue;
            }
            foreach (var region in result.Document.Regions)
            {
                foreach (var item in region.BlockEntities.Values) Add(blockEntities, item.Id, path);
                foreach (var item in region.Entities) Add(entities, item.Id, path);
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath)) ?? ".");
        List<string> lines = ["kind\tid\tcount\tfiles\texamples"];
        Append("block_entity", blockEntities);
        Append("entity", entities);
        if (failures.Count > 0)
        {
            lines.Add("");
            lines.Add("failures");
            lines.AddRange(failures);
        }
        File.WriteAllLines(reportPath, lines);
        Console.WriteLine($"[MESH][entity.corpus] files={files.Length} parsed={files.Length - failures.Count} failures={failures.Count}");
        foreach (var pair in entities.OrderByDescending(static pair => pair.Value.Count))
            Console.WriteLine($"[MESH][entity.corpus] entity {pair.Key} count={pair.Value.Count} files={pair.Value.Files.Count}");
        foreach (var pair in blockEntities.OrderByDescending(static pair => pair.Value.Count).Take(30))
            Console.WriteLine($"[MESH][entity.corpus] blockEntity {pair.Key} count={pair.Value.Count} files={pair.Value.Files.Count}");
        Console.WriteLine($"[MESH][entity.corpus] report={Path.GetFullPath(reportPath)}");
        return failures.Count == 0 ? 0 : 1;

        static void Add(Dictionary<string, (int Count, HashSet<string> Files)> target, string id, string path)
        {
            if (!target.TryGetValue(id, out var current)) current = (0, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            current.Count++;
            current.Files.Add(path);
            target[id] = current;
        }

        void Append(string kind, Dictionary<string, (int Count, HashSet<string> Files)> source)
        {
            foreach (var pair in source.OrderByDescending(static pair => pair.Value.Count).ThenBy(static pair => pair.Key))
                lines.Add($"{kind}\t{pair.Key}\t{pair.Value.Count}\t{pair.Value.Files.Count}\t" +
                          string.Join(" | ", pair.Value.Files.Take(3)));
        }
    }

    private static LitematicRegion MakeRegion(Vector3I size, BlockStateDefinition[] palette, int[] blocks)
    {
        var position = Vector3I.Zero;
        return new LitematicRegion(
            "smoke",
            position,
            size,
            IntBounds.FromPositionSize(position, size),
            [.. palette],
            [.. blocks]);
    }

    private static NbtMap Map(params (string Key, NbtData Value)[] values) =>
        new(values.ToImmutableDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal));

    private static NbtSequence Sequence(params NbtData[] values) => new([.. values]);

    private static void CheckSignFrontAndBack()
    {
        BlockStateDefinition sign = new("minecraft:oak_sign",
            BlockStateDefinition.NoProperties.SetItem("rotation", "0").SetItem("waterlogged", "false"));
        var front = Map(("messages", Sequence(new NbtText("{\"text\":\"正面\"}"))),
            ("color", new NbtText("black")));
        var back = Map(("messages", Sequence(new NbtText("{\"text\":\"背面\"}"))),
            ("color", new NbtText("red")), ("has_glowing_text", new NbtInteger(1)));
        var blockEntity = new BlockEntityData("minecraft:sign", Vector3I.Zero,
            Map(("front_text", front), ("back_text", back)));
        var region = MakeRegion(new Vector3I(1, 1, 1), [sign], [0]) with
        {
            BlockEntities = ImmutableDictionary<Vector3I, BlockEntityData>.Empty.Add(Vector3I.Zero, blockEntity)
        };
        HashSet<string> sprites = [];
        List<GeneratedSprite> generated = [];
        var collector = new BlockMeshBuilder(_resolver, TextureAtlas.Build(_packs, []));
        collector.CollectSprites([region], sprites, _packs, generated);
        Debug.Assert(generated.Count == 2, $"[MESH][smoke] 双面告示牌应生成 2 张文字贴图 实得 {generated.Count}");
        Debug.Assert(generated.All(static sprite => sprite.Width == 192 && sprite.Height == 96),
            "[MESH][smoke] 告示牌文字贴图应为 192x96");
        var atlas = TextureAtlas.Build(_packs, sprites, generated);
        var mesh = new BlockMeshBuilder(_resolver, atlas).BuildRegion(region);
        Debug.Assert(mesh.Indices.Length >= 12, "[MESH][smoke] 双面告示牌文字应至少增加两个 quad");
        CheckVerticesWellFormed(mesh, atlas);
        Debug.WriteLine("[MESH][smoke] 告示牌: front/back 分离、192x96 文字贴图 ✓");
        _checks++;
    }

    // 告示牌几何/朝向回归。数字全部来自原版 SignModel ×2/3 与 WallSignRenderer 的墙牌偏移，
    // 用渲染出的顶点包围盒钉住——只看截图的话牌面长轴装反（rotation 用了正角）在
    // 0/180 两个朝向上完全自逆，看不出来。
    private static void CheckSignGeometry()
    {
        // rotation=0（朝南）：牌面与格子等宽（0..16px）、上沿 17.33px，支柱踩地到 y=0。
        var south = SignBoundingBox("minecraft:oak_sign", ("rotation", "0"), ("waterlogged", "false"));
        Debug.Assert(MathF.Abs(south.Min.X) < 0.01f && MathF.Abs(south.Max.X - 1f) < 0.01f,
            $"[MESH][smoke] 站牌牌面应与格子等宽 实得 x={south.Min.X:F3}..{south.Max.X:F3}");
        Debug.Assert(MathF.Abs(south.Min.Y) < 0.01f && MathF.Abs(south.Max.Y - 17.333f / 16f) < 0.01f,
            $"[MESH][smoke] 站牌应从地面到 17.33px 实得 y={south.Min.Y:F3}..{south.Max.Y:F3}");
        Debug.Assert(south.Max.Z - south.Min.Z < 0.15f,
            $"[MESH][smoke] 朝南站牌应是一片薄板 实得 spanZ={south.Max.Z - south.Min.Z:F3}");

        // rotation=4（朝西）：牌面长轴转到 Z；文字片必须跟着到西侧（x≈7.13px）。
        // 牌面用 element 约定、文字用 blockstate 约定，差一个负号；装反时文字会跑到东面。
        var west = SignBoundingBox("minecraft:oak_sign", ("rotation", "4"), ("waterlogged", "false"));
        Debug.Assert(west.Max.Z - west.Min.Z > 0.99f && west.Max.X - west.Min.X < 0.15f,
            $"[MESH][smoke] rotation=4 牌面长轴应转到 Z 实得 spanX={west.Max.X - west.Min.X:F3} " +
            $"spanZ={west.Max.Z - west.Min.Z:F3}");
        Debug.Assert(MathF.Abs(west.Min.X - 7.133f / 16f) < 0.02f,
            $"[MESH][smoke] rotation=4 文字片应在西侧 实得 minX={west.Min.X:F3}");

        // rotation=8（朝北）：文字必须转到 -Z 面（7.13px）。留在南面就是文字没跟着牌面转，
        // 视角从 +Z 看过去「背面也有字」。
        var north = SignBoundingBox("minecraft:oak_sign", ("rotation", "8"), ("waterlogged", "false"));
        Debug.Assert(MathF.Abs(north.Min.Z - 7.133f / 16f) < 0.02f,
            $"[MESH][smoke] rotation=8 文字片应在北面 实得 minZ={north.Min.Z:F3}");

        // 墙牌 facing=east：牌面贴西墙（0.33..1.67px），文字面朝东（1.867px），且没有支柱。
        var wall = SignBoundingBox("minecraft:oak_wall_sign", ("facing", "east"), ("waterlogged", "false"));
        Debug.Assert(MathF.Abs(wall.Min.X - 0.333f / 16f) < 0.02f && MathF.Abs(wall.Max.X - 1.867f / 16f) < 0.02f,
            $"[MESH][smoke] facing=east 墙牌应贴西墙、文字朝东 实得 x={wall.Min.X:F3}..{wall.Max.X:F3}");
        Debug.Assert(MathF.Abs(wall.Min.Y - 4.333f / 16f) < 0.02f && wall.Max.Z - wall.Min.Z > 0.99f,
            $"[MESH][smoke] facing=east 墙牌应压在 y4.33..12.33、长轴 Z 实得 y0={wall.Min.Y:F3} " +
            $"spanZ={wall.Max.Z - wall.Min.Z:F3}");
        Debug.WriteLine("[MESH][smoke] 告示牌几何: 牌面 16x8、支柱踩地、墙牌贴墙、文字同号 ✓");
        _checks++;
    }

    private static (Vector3 Min, Vector3 Max) SignBoundingBox(string blockId,
        (string Key, string Value) property, (string Key, string Value) second)
    {
        var definition = new BlockStateDefinition(blockId,
            BlockStateDefinition.NoProperties.SetItem(property.Key, property.Value)
                .SetItem(second.Key, second.Value));
        var text = Map(("messages", Sequence(new NbtText("{\"text\":\"牌\"}"))), ("color", new NbtText("black")));
        var blockEntity = new BlockEntityData("minecraft:sign", Vector3I.Zero, Map(("front_text", text)));
        var region = MakeRegion(new Vector3I(1, 1, 1), [definition], [0]) with
        {
            BlockEntities = ImmutableDictionary<Vector3I, BlockEntityData>.Empty.Add(Vector3I.Zero, blockEntity)
        };
        HashSet<string> sprites = [];
        List<GeneratedSprite> generated = [];
        var collector = new BlockMeshBuilder(_resolver, TextureAtlas.Build(_packs, []));
        collector.CollectSprites([region], sprites, _packs, generated);
        var atlas = TextureAtlas.Build(_packs, sprites, generated);
        var mesh = new BlockMeshBuilder(_resolver, atlas).BuildRegion(region);
        CheckVerticesWellFormed(mesh, atlas);
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (var i = 0; i < mesh.VertexCount; i++)
        {
            var point = new Vector3(mesh.Vertices[i * MeshData.FloatsPerVertex],
                mesh.Vertices[i * MeshData.FloatsPerVertex + 1], mesh.Vertices[i * MeshData.FloatsPerVertex + 2]);
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
        }

        Debug.WriteLine($"[MESH][smoke] sign bbox {blockId} {property.Key}={property.Value} " +
                        $"min=({min.X:F3},{min.Y:F3},{min.Z:F3}) max=({max.X:F3},{max.Y:F3},{max.Z:F3}) " +
                        $"verts={mesh.VertexCount}");
        return (min, max);
    }

    private static void CheckPaintingGeometry()
    {
        var painting = new EntityData("minecraft:painting", new Vector3(2, 2, 2),
            Map(("variant", new NbtText("minecraft:donkey_kong")), ("Facing", new NbtInteger(5))));
        var region = MakeRegion(new Vector3I(1, 1, 1), [Air], [0]) with { Entities = [painting] };
        HashSet<string> sprites = [];
        var collector = new BlockMeshBuilder(_resolver, TextureAtlas.Build(_packs, []));
        collector.CollectSprites([region], sprites, _packs, []);
        Debug.Assert(sprites.Contains("minecraft:painting/donkey_kong") &&
                     sprites.Contains("minecraft:painting/back"),
            "[MESH][smoke] 画应收集 motive 与背板贴图");
        var atlas = TextureAtlas.Build(_packs, sprites);
        var mesh = new BlockMeshBuilder(_resolver, atlas).BuildRegion(region);
        Debug.Assert(mesh.VertexCount == 8 && mesh.Indices.Length == 12,
            $"[MESH][smoke] 画应为正背两个 quad 实得 verts={mesh.VertexCount} indices={mesh.Indices.Length}");
        var xs = Enumerable.Range(0, mesh.VertexCount).Select(i => mesh.Vertices[i * MeshData.FloatsPerVertex]).ToArray();
        var ys = Enumerable.Range(0, mesh.VertexCount).Select(i => mesh.Vertices[i * MeshData.FloatsPerVertex + 1]).ToArray();
        Debug.Assert(xs.Max() - xs.Min() < 0.05f && MathF.Abs(ys.Max() - ys.Min() - 3f) < 0.01f,
            $"[MESH][smoke] east-facing donkey_kong 应为竖 3 格、薄 X 平面 spanX={xs.Max()-xs.Min():F3} spanY={ys.Max()-ys.Min():F3}");
        CheckVerticesWellFormed(mesh, atlas);
        Debug.WriteLine("[MESH][smoke] 画: motive 尺寸 4x3、Facing 定向、正背面 ✓");
        _checks++;
    }

    private static void CheckArmorStandGeometry()
    {
        var rotation = Sequence(new NbtFloating(90), new NbtFloating(0));
        var armorStand = new EntityData("minecraft:armor_stand", Vector3.Zero,
            Map(("Rotation", rotation), ("ShowArms", new NbtInteger(1)), ("NoBasePlate", new NbtInteger(1))));
        var region = MakeRegion(new Vector3I(1, 1, 1), [Air], [0]) with { Entities = [armorStand] };
        var atlas = TextureAtlas.Build(_packs, ["minecraft:entity/armorstand/wood"]);
        var mesh = new BlockMeshBuilder(_resolver, atlas).BuildRegion(region);
        // 无底座：脊柱/肩梁/腰梁/头/双腿/双臂共 8 盒，每盒 24 顶点。
        Debug.Assert(mesh.VertexCount == 8 * 24,
            $"[MESH][smoke] 盔甲架 NoBasePlate+ShowArms 应为 8 盒 实得 verts={mesh.VertexCount}");
        CheckVerticesWellFormed(mesh, atlas);
        Debug.WriteLine("[MESH][smoke] 盔甲架: 横梁/腰梁/双臂、yaw、NoBasePlate ✓");
        _checks++;
    }

    private static void CheckDroppedItemAndBoat()
    {
        var item = new EntityData("minecraft:item", new Vector3(0, 0, 0),
            Map(("Item", Map(("id", new NbtText("minecraft:stone")))),
                ("Rotation", Sequence(new NbtFloating(30), new NbtFloating(0)))));
        var boat = new EntityData("minecraft:oak_boat", new Vector3(2, 0, 0),
            Map(("Rotation", Sequence(new NbtFloating(90), new NbtFloating(0)))));
        var region = MakeRegion(new Vector3I(1, 1, 1), [Air], [0]) with { Entities = [item, boat] };
        HashSet<string> sprites = [];
        var collector = new BlockMeshBuilder(_resolver, TextureAtlas.Build(_packs, []));
        collector.CollectSprites([region], sprites, _packs, []);
        Debug.Assert(sprites.Contains("minecraft:block/stone") && sprites.Contains("minecraft:entity/boat/oak"),
            $"[MESH][smoke] 掉落物/船贴图收集不完整 [{string.Join(',', sprites)}]");
        var atlas = TextureAtlas.Build(_packs, sprites);
        var mesh = new BlockMeshBuilder(_resolver, atlas).BuildRegion(region);
        Debug.Assert(mesh.VertexCount == 8 + 5 * 24,
            $"[MESH][smoke] 掉落物交叉双面+船 5 盒顶点数不符 {mesh.VertexCount}");
        CheckVerticesWellFormed(mesh, atlas);
        Debug.WriteLine("[MESH][smoke] 掉落物: 交叉 sprite；船/木筏: 船体静态网格 ✓");
        _checks++;
    }

    private static void CheckStaticMobs()
    {
        EntityData[] entities =
        [
            new("minecraft:villager", Vector3.Zero, Map(("Rotation", Sequence(new NbtFloating(45))))),
            new("minecraft:iron_golem", new Vector3(3, 0, 0), NbtMap.Empty),
            new("minecraft:shulker", new Vector3(6, 0, 0), Map(("Color", new NbtInteger(14)))),
            new("minecraft:pig", new Vector3(9, 0, 0), NbtMap.Empty)
        ];
        var region = MakeRegion(new Vector3I(1, 1, 1), [Air], [0]) with { Entities = [.. entities] };
        HashSet<string> sprites = [];
        var collector = new BlockMeshBuilder(_resolver, TextureAtlas.Build(_packs, []));
        collector.CollectSprites([region], sprites, _packs, []);
        var atlas = TextureAtlas.Build(_packs, sprites);
        Debug.Assert(atlas.MissingCount == 0,
            $"[MESH][smoke] 静态生物缺贴图 [{string.Join(',', atlas.MissingSprites)}]");
        var mesh = new BlockMeshBuilder(_resolver, atlas).BuildRegion(region);
        Debug.Assert(mesh.VertexCount == (6 + 6 + 2 + 6) * 24,
            $"[MESH][smoke] 静态生物盒件顶点数不符 {mesh.VertexCount}");
        CheckVerticesWellFormed(mesh, atlas);
        Debug.WriteLine("[MESH][smoke] 村民/溺尸/傀儡/猪灵/潜影贝/猪静态模型与 yaw ✓");
        _checks++;
    }

    private static void CheckSingleBlock()
    {
        var builder = CreateBuilder("minecraft:block/stone");
        var mesh = builder.BuildRegion(MakeRegion(new Vector3I(1, 1, 1), [Stone], [0]));

        // 一个孤立方块 6 面 24 顶点 36 索引，剔除和旋转约定都以此为基准。
        Debug.Assert(mesh.VertexCount == 24, $"[MESH][smoke] 单方块顶点={mesh.VertexCount} expected=24");
        Debug.Assert(mesh.Indices.Length == 36, $"[MESH][smoke] 单方块索引={mesh.Indices.Length} expected=36");
        CheckVerticesWellFormed(mesh);
        Debug.WriteLine("[MESH][smoke] 单方块: 24 顶点 36 索引，法线单位、uv 在 [0,1] ✓");
        _checks++;
    }

    private static void CheckFaceCulling()
    {
        // 两个相接的方块共享一面：12 - 2 = 10 面，60 索引。剔除失效就会是 72。
        var builder = CreateBuilder("minecraft:block/stone");
        var mesh = builder.BuildRegion(MakeRegion(new Vector3I(2, 1, 1), [Stone], [0, 0]));

        Debug.Assert(mesh.Indices.Length == 60, $"[MESH][smoke] 双方块索引={mesh.Indices.Length} expected=60（剔除失效会是 72）");
        Debug.WriteLine("[MESH][smoke] 双方块相接: 10 面 60 索引，共享面被剔除 ✓");
        _checks++;
    }

    private static void CheckAmbientOcclusion()
    {
        var blocks = Enumerable.Repeat(1, 27).ToArray();
        blocks[(1 * 3 + 1) * 3 + 1] = 0;
        blocks[(2 * 3 + 1) * 3 + 0] = 0;
        var builder = CreateBuilder("minecraft:block/stone");
        var mesh = builder.BuildRegion(MakeRegion(new Vector3I(3, 3, 3), [Stone, Air], blocks));
        var values = Enumerable.Range(0, mesh.VertexCount)
            .Select(vertex => mesh.Vertices[vertex * MeshData.FloatsPerVertex + MeshData.AoOffset]).ToArray();
        Debug.Assert(values.Any(static value => value < 0.99f), "[MESH][smoke] AO 没有压暗任何顶点");
        Debug.Assert(values.All(static value => value is >= 0.55f and <= 1f),
            "[MESH][smoke] AO 超出 [0.55,1]");
        Debug.WriteLine($"[MESH][smoke] AO: range=[{values.Min():F2},{values.Max():F2}] 邻角逐顶点压暗 ✓");
        _checks++;
    }

    private static void CheckLogAxisRotation()
    {
        // 旋转约定的锚点：oak_log[axis=x] 的两个 variant 都带 x=90,y=90，
        // 端面帽（oak_log_top 贴图的面）必须最终朝 ±X。端面帽靠 uv 落位识别——
        // oak_log_top 的 uv 是整张，侧面（oak_log）也是整张，所以按 uv 所在的 rect 分类。
        BlockStateDefinition logX = new(
            "minecraft:oak_log",
            BlockStateDefinition.NoProperties.SetItem("axis", "x"));
        var atlas = TextureAtlas.Build(_packs, ["minecraft:block/oak_log", "minecraft:block/oak_log_top"]);
        BlockMeshBuilder builder = new(_resolver, atlas);
        var mesh = builder.BuildRegion(MakeRegion(new Vector3I(1, 1, 1), [logX], [0]));

        Debug.Assert(mesh.VertexCount == 24, $"[MESH][smoke] 原木顶点={mesh.VertexCount} expected=24（旋转不应增删面）");

        atlas.TryGetRect("minecraft:block/oak_log_top", out var topRect);
        var uLow = (float)topRect.X / atlas.Width;
        var uHigh = (float)(topRect.X + topRect.Width) / atlas.Width;
        // v 不翻转（v = 图集 y/H）：Y 小的 rect v 也小，区间直接按 Y 摆。
        var vLow = (float)topRect.Y / atlas.Height;
        var vHigh = (float)(topRect.Y + topRect.Height) / atlas.Height;

        // 按「面的 uv 中心」分类，不按单顶点：两个 rect 上下堆叠时共享 v=0.5 这条边，
        // 侧面底边的顶点恰好压线，按顶点判会把侧面误判成端帽。
        // 面中心永远落在 rect 内部，压不了线。网格生成保证每 4 个连续顶点是一个面。
        var endCapFaces = 0;
        for (var faceStart = 0; faceStart < mesh.VertexCount; faceStart += 4)
        {
            var uvSum = Vector2.Zero;
            var normalSum = Vector3.Zero;
            for (var i = faceStart; i < faceStart + 4; i++)
            {
                uvSum += ReadUv(mesh, i);
                normalSum += ReadNormal(mesh, i);
            }

            var centre = uvSum / 4f;
            var inTopRect = centre.X >= uLow && centre.X < uHigh && centre.Y >= vLow && centre.Y < vHigh;
            if (!inTopRect) continue;

            endCapFaces++;
            var normal = Vector3.Normalize(normalSum);
            Debug.Assert(
                MathF.Abs(normal.X) > 0.999f && MathF.Abs(normal.Y) < 1e-3f && MathF.Abs(normal.Z) < 1e-3f,
                $"[MESH][smoke] 横放原木的端面帽朝向不对 normal={normal} expected=±X " +
                "note=variant 旋转的符号或顺序错了，改 Rotate/ApplyVariantRotation");
        }

        Debug.Assert(endCapFaces == 2, $"[MESH][smoke] 端面帽应 2 个面（两端）实得 {endCapFaces}");
        Debug.WriteLine("[MESH][smoke] oak_log[axis=x]: 端面帽 ±X ✓（旋转约定钉住）");
        _checks++;
    }

    private static void CheckWallTorchElementRotation()
    {
        BlockStateDefinition torch = new(
            "minecraft:redstone_wall_torch",
            BlockStateDefinition.NoProperties.SetItem("facing", "east").SetItem("lit", "true"));
        var region = MakeRegion(new Vector3I(1, 1, 1), [torch], [0]);
        HashSet<string> sprites = [];
        var collector = new BlockMeshBuilder(_resolver, TextureAtlas.Build(_packs, []));
        collector.CollectSprites([region], sprites);
        var builder = new BlockMeshBuilder(_resolver, TextureAtlas.Build(_packs, sprites));
        var mesh = builder.BuildRegion(region);

        // facing=east 的模板位于 x=0 墙面，element angle=-22.5° 应让火把顶端
        // 向 +X（方块内部/朝东）倾斜。把 element 与 variant 共用取负角函数时，
        // 整体质心会落到 x<0，即火把反向插进墙外。
        var meanX = Enumerable.Range(0, mesh.VertexCount)
            .Average(vertex => mesh.Vertices[vertex * MeshData.FloatsPerVertex + MeshData.PositionOffset]);
        Debug.Assert(meanX > 0f,
            $"[MESH][smoke] east 墙上红石火把应向 +X 倾斜，顶点均值 x={meanX}");
        Debug.WriteLine($"[MESH][smoke] redstone_wall_torch: element -22.5° 保持原符号 meanX={meanX:F3} ✓");
        _checks++;
    }

    private static BlockStateDefinition WaterState(int level)
    {
        return new BlockStateDefinition("minecraft:water",
            BlockStateDefinition.NoProperties.SetItem("level", level.ToString()));
    }

    // 水面高与邻居规则，加上 BuildRegion 集成：高度曲线、贴墙不塌边、相邻水格
    // 连通不画壁（面数守恒）、下落柱满格、waterlogged 宿主双层渲染。
    private static void CheckFluidRules()
    {
        // 高度曲线：源 8/9，随 level 单调降到 level7 的 1/9 薄膜，8+ 下落柱满格。
        var own0 = FluidMesher.OwnHeight(0);
        Debug.Assert(MathF.Abs(own0 - 8f / 9f) < 1e-5f, $"[MESH][smoke] OwnHeight(0)={own0} expected=8/9");
        // 单调只在流动段 0..7 内成立；8..15 是下落柱，设计上就是满格。
        for (var level = 1; level <= 7; level++)
            Debug.Assert(
                FluidMesher.OwnHeight(level) <= FluidMesher.OwnHeight(level - 1) + 1e-6f,
                $"[MESH][smoke] OwnHeight 在 level={level} 回升");

        Debug.Assert(FluidMesher.OwnHeight(7) > 0f, "[MESH][smoke] level7 应剩薄膜");
        Debug.Assert(FluidMesher.OwnHeight(8) == 1f && FluidMesher.OwnHeight(15) == 1f, "[MESH][smoke] 下落柱应满格");

        // palette 0=石头(遮挡) 1=水L0 2=水L2 3=空气；遮挡标记手填，不依赖 resolver。
        BlockStateDefinition[] palette = [Stone, WaterState(0), WaterState(2), Air];
        var water = new bool[palette.Length];
        var levels = new int[palette.Length];
        var occ = new bool[palette.Length];
        for (var i = 0; i < palette.Length; i++)
        {
            water[i] = FluidMesher.IsWaterCell(palette[i]);
            levels[i] = FluidMesher.LevelOf(palette[i]);
            occ[i] = i == 0;
        }

        // 贴墙：3x3x3 全石头，中心一格水。四角两侧都不是流体 → 保持自身高，
        // 池边贴实心墙的池面是平的，不往墙上塌。
        var solidBlocks = new int[27];
        FluidMesher.World solidWorld = new(water, levels, occ, solidBlocks, new Vector3I(3, 3, 3));
        solidBlocks[(1 * 3 + 1) * 3 + 1] = 1;
        foreach (var (dx, dz) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) })
        {
            var corner = FluidMesher.Corner(solidWorld, 1, 1, 1, 0, dx, dz);
            Debug.Assert(
                MathF.Abs(corner - own0) < 1e-5f,
                $"[MESH][smoke] 贴实心墙的角高应保持自身高 dx={dx} dz={dz} got={corner}");
        }

        // 阶梯：L0 与 L2 相邻，共享边的角高是两格平均 → 倾斜水面。
        int[] rowBlocks = [1, 2, 3];
        FluidMesher.World rowWorld = new(water, levels, occ, rowBlocks, new Vector3I(3, 1, 1));
        var slope = FluidMesher.Corner(rowWorld, 0, 0, 0, 0, +1, -1);
        Debug.Assert(
            MathF.Abs(slope - (own0 + FluidMesher.OwnHeight(2)) / 2f) < 1e-5f,
            $"[MESH][smoke] 相邻水格的角高应是平均 got={slope}");

        // 垂直对角连接：两个正交邻居都为空，但对角水列向上连续。
        // 共享角必须满高，否则上层对角水格下方会露出三角缝。
        var diagonalBlocks = Enumerable.Repeat(3, 2 * 2 * 2).ToArray();
        FluidMesher.World diagonalWorld = new(water, levels, occ, diagonalBlocks, new Vector3I(2, 2, 2));
        diagonalBlocks[(0 * 2 + 0) * 2 + 0] = 1; // (0,0,0)
        diagonalBlocks[(0 * 2 + 1) * 2 + 1] = 1; // (1,0,1)
        diagonalBlocks[(1 * 2 + 1) * 2 + 1] = 1; // (1,1,1)
        var diagonalCorner = FluidMesher.Corner(diagonalWorld, 0, 0, 0, 0, +1, +1);
        Debug.Assert(
            MathF.Abs(diagonalCorner - 1f) < 1e-5f,
            $"[MESH][smoke] 向上连续的对角水列应把共享角抬到满高 got={diagonalCorner}");

        // 集成：4x1x1 的 level 0..3 水阶梯。18 面 = 两头各 5 + 中间两格各 4
        // （相邻水格之间不画壁——连通标记失灵就会多出面，交界上两片重合 z-fight）。
        BlockStateDefinition[] wPalette = [WaterState(0), WaterState(1), WaterState(2), WaterState(3)];
        var wRegion = MakeRegion(new Vector3I(4, 1, 1), wPalette, [0, 1, 2, 3]);
        BlockMeshBuilder collector = new(_resolver, TextureAtlas.Build(_packs, []));
        HashSet<string> wSprites = [];
        collector.CollectSprites([wRegion], wSprites);
        Debug.Assert(
            wSprites.Contains("minecraft:block/water_still"),
            $"[MESH][smoke] 水调色板没收集到 water_still: [{string.Join(", ", wSprites)}]");
        var wBuilder = CreateBuilder([.. wSprites]);
        var wMesh = wBuilder.BuildRegion(wRegion);
        Debug.Assert(
            wMesh.Indices.Length == 108,
            $"[MESH][smoke] 水阶梯索引={wMesh.Indices.Length} expected=108");
        CheckVerticesWellFormed(wMesh);
        Debug.Assert(
            MathF.Abs(MaxYInCell(wMesh, 0, 1) - own0) < 1e-4f,
            $"[MESH][smoke] L0 水面应 8/9 got={MaxYInCell(wMesh, 0, 1)}");
        // L3 格：靠 L2 的共享边角高是两格平均（倾斜），远端角保持自身高。
        // 两个值都得在，少了平均就是没向低处倾斜，少了自身高就是角算错了。
        var slopeExpect = (FluidMesher.OwnHeight(2) + FluidMesher.OwnHeight(3)) / 2f;
        bool hasSelf = false, hasSlope = false;
        for (var v = 0; v < wMesh.VertexCount; v++)
        {
            var o = v * MeshData.FloatsPerVertex;
            var vx = wMesh.Vertices[o];
            var vy = wMesh.Vertices[o + 1];
            if (vx >= 3f)
            {
                hasSelf |= MathF.Abs(vy - FluidMesher.OwnHeight(3)) < 1e-4f;
                hasSlope |= MathF.Abs(vy - slopeExpect) < 1e-4f;
            }
        }

        Debug.Assert(hasSelf, $"[MESH][smoke] L3 格缺自身高 {FluidMesher.OwnHeight(3)} 的角顶点");
        Debug.Assert(hasSlope, $"[MESH][smoke] L3 格缺共享边平均高 {slopeExpect} 的角顶点（没倾斜）");
        for (var v = 0; v < wMesh.VertexCount; v++)
        {
            var tint = wMesh.Vertices[v * MeshData.FloatsPerVertex + MeshData.TintOffset];
            Debug.Assert(MathF.Abs(tint - 3f) < 0.5f, $"[MESH][smoke] 水顶点 tint 槽应 3 got={tint}");
        }

        // 下落柱：L8 在下、L0 源在上。10 面：源 top+4 侧（无底，下面是水），
        // 下落格 4 侧+底（下端在区域边界，按空气口径出面）；下落格侧壁顶边满格 y=1.0。
        var fRegion = MakeRegion(new Vector3I(1, 2, 1), [WaterState(8), WaterState(0)], [0, 1]);
        var fMesh = wBuilder.BuildRegion(fRegion);
        Debug.Assert(fMesh.Indices.Length == 60, $"[MESH][smoke] 下落柱索引={fMesh.Indices.Length} expected=60");
        var fallingFull = false;
        for (var v = 0; v < fMesh.VertexCount; v++)
        {
            var vy = fMesh.Vertices[v * MeshData.FloatsPerVertex + 1];
            fallingFull |= MathF.Abs(vy - 1f) < 1e-4f;
        }

        Debug.Assert(fallingFull, "[MESH][smoke] 下落柱侧壁应满格 y=1.0");

        // waterlogged 宿主：本体照画，8/9 水面叠在格里且染水色；
        // CollectSprites 必须补 water_still（只有宿主没有裸水的调色板场景）。
        BlockStateDefinition slab = new(
            "minecraft:oak_slab",
            BlockStateDefinition.NoProperties.SetItem("type", "bottom").SetItem("waterlogged", "true"));
        var sRegion = MakeRegion(new Vector3I(1, 1, 1), [slab], [0]);
        HashSet<string> sSprites = [];
        collector.CollectSprites([sRegion], sSprites);
        Debug.Assert(
            sSprites.Contains("minecraft:block/water_still"),
            $"[MESH][smoke] waterlogged 宿主没补水面贴图: [{string.Join(", ", sSprites)}]");
        var sBuilder = CreateBuilder([.. sSprites]);
        var sMesh = sBuilder.BuildRegion(sRegion);
        bool hasWaterTop = false, hasHost = false;
        for (var v = 0; v < sMesh.VertexCount; v++)
        {
            var o = v * MeshData.FloatsPerVertex;
            var vy = sMesh.Vertices[o + 1];
            var tint = sMesh.Vertices[o + MeshData.TintOffset];
            hasWaterTop |= MathF.Abs(vy - own0) < 1e-4f && MathF.Abs(tint - 3f) < 0.5f;
            hasHost |= vy < 0.5f;
        }

        Debug.Assert(hasWaterTop, "[MESH][smoke] waterlogged 台阶里没有 8/9 水面（tint=3）");
        Debug.Assert(hasHost, "[MESH][smoke] waterlogged 台阶本体丢了");
        Debug.WriteLine("[MESH][smoke] 流体: 高度曲线/贴墙/阶梯/连通面数/下落柱/waterlogged ✓");
        _checks++;
    }

    // 网格坐标 x ∈ [x0, x1) 里的最高顶点 y（流体表面高度断言用）。
    private static float MaxYInCell(MeshData mesh, int x0, int x1)
    {
        var max = float.MinValue;
        for (var v = 0; v < mesh.VertexCount; v++)
        {
            var o = v * MeshData.FloatsPerVertex;
            var vx = mesh.Vertices[o];
            if (vx >= x0 && vx <= x1 && mesh.Vertices[o + 1] > max) max = mesh.Vertices[o + 1];
        }

        return max;
    }

    private static void CheckRealFile(string path)
    {
        var result = LitematicLoader.TryLoadFile(path);
        Debug.Assert(result.Success && result.Document is not null,
            $"[MESH][smoke] 打不开 {path}: {result.Error} {result.Message}");
        var document = result.Document!;

        HashSet<string> sprites = [];
        var collector = CreateBuilder();
        List<GeneratedSprite> generatedSprites = [];
        collector.CollectSprites(document.Regions, sprites, _packs, generatedSprites);
        Debug.WriteLine($"[MESH][smoke] {Path.GetFileName(path)}: {document.Regions.Length} 个 region、" +
                        $"{document.TotalBlocks} 方块、{sprites.Count} 张 sprite");

        var atlas = TextureAtlas.Build(_packs, sprites, generatedSprites);
        Debug.WriteLine(
            $"[MESH][smoke] 图集 {atlas.Width}x{atlas.Height}（缺 {atlas.MissingCount} 张" +
            (atlas.MissingCount > 0 ? $"：{string.Join(", ", atlas.MissingSprites)}）" : "）"));

        BlockMeshBuilder builder = new(_resolver, atlas);
        long totalFaces = 0;
        long totalVerts = 0;
        var clock = Stopwatch.StartNew();
        foreach (var region in document.Regions)
        {
            // 诊断：调色板里解不出任何面的状态。这种方块整块消失，和剔除误删长得一样，
            // 但关剔除救不了它，所以单独列出来。
            SortedSet<string> zeroFaceStates = [];
            foreach (var def in region.Palette)
            {
                if (def.IsAir) continue;

                var resolved = _resolver.Resolve(def.ToString());
                var faceCount = resolved.Variants.Sum(v => v.Model.Elements.Sum(e => e.Faces.Count));
                if (faceCount == 0) zeroFaceStates.Add(def.ToString());
            }

            if (zeroFaceStates.Count > 0)
                Debug.WriteLine(
                    $"[MESH][smoke] {region.Name} 有 {zeroFaceStates.Count} 个零面状态: " +
                    string.Join(", ", zeroFaceStates));

            var mesh = builder.BuildRegion(region);
            totalVerts += mesh.VertexCount;
            totalFaces += mesh.Indices.Length / 6;

            Debug.Assert(mesh.Indices.Length % 6 == 0, "[MESH][smoke] 索引数必须是 6 的倍数");
            Debug.Assert(mesh.VertexCount > 0, $"[MESH][smoke] {region.Name} 网格是空的");
            CheckVerticesWellFormed(mesh, atlas);
        }

        clock.Stop();
        Debug.WriteLine(
            $"[MESH][smoke] {Path.GetFileName(path)}: 共 {totalVerts} 顶点 {totalFaces} 面，" +
            $"耗时 {clock.ElapsedMilliseconds}ms，{builder.Stats}");
        _checks++;
    }

    // uv 必须落进 [0,1]、法线必须单位长——这两条不成立，GL 那边只会看到胡说的画面。
    private static void CheckVerticesWellFormed(MeshData mesh, TextureAtlas? atlas = null)
    {
        for (var i = 0; i < mesh.VertexCount; i++)
        {
            var normal = ReadNormal(mesh, i);
            Debug.Assert(
                MathF.Abs(normal.Length() - 1f) < 1e-3f,
                $"[MESH][smoke] 顶点 {i} 法线非单位 normal={normal}");

            var uv = ReadUv(mesh, i);
            Debug.Assert(
                uv.X >= -1e-4f && uv.X <= 1f + 1e-4f && uv.Y >= -1e-4f && uv.Y <= 1f + 1e-4f,
                $"[MESH][smoke] 顶点 {i} uv 越界 uv={uv}");
        }
    }

    private static Vector3 ReadNormal(MeshData mesh, int vertex)
    {
        return new Vector3(
            mesh.Vertices[(vertex * MeshData.FloatsPerVertex) + MeshData.NormalOffset],
            mesh.Vertices[(vertex * MeshData.FloatsPerVertex) + MeshData.NormalOffset + 1],
            mesh.Vertices[(vertex * MeshData.FloatsPerVertex) + MeshData.NormalOffset + 2]);
    }

    private static Vector2 ReadUv(MeshData mesh, int vertex)
    {
        return new Vector2(
            mesh.Vertices[(vertex * MeshData.FloatsPerVertex) + MeshData.UvOffset],
            mesh.Vertices[(vertex * MeshData.FloatsPerVertex) + MeshData.UvOffset + 1]);
    }
}
