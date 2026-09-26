using System.Diagnostics;
using System.Numerics;
using LitematicaViewer.Assets;
using LitematicaViewer.Assets.Model;
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

    public static int Main(string[] args)
    {
        // --preview <outDir> <资源包...>：逐方块状态出三面视图 PNG（见 PreviewBlocks）。
        if (args.Length >= 2 && args[0] == "--preview")
        {
            return PreviewBlocks.Run(args[1], [.. args.Skip(2)]);
        }

        // --montage <out.png> [过滤] [格边长] [上限] <资源包...>：同一条 Render 链路
        // 拼成网格大图，快速人检某类方块（见 PreviewBlocks.RunMontage）。
        if (args.Length >= 2 && args[0] == "--montage")
        {
            return PreviewBlocks.RunMontage(args);
        }

        // --probe <litematic> <资源包...>：按网格层的读序打印角上 5x5x5 的状态串，
        // 给「渲染器和第三方解码器谁对」做逐坐标对账用。资源包参数其实用不上，
        // 接着只是让分流统一。
        if (args.Length >= 2 && args[0] == "--probe")
        {
            using TextWriterTraceListener probeListener = new(Console.Out);
            Trace.Listeners.Add(probeListener);
            Trace.AutoFlush = true;
            LoadResult probeResult = LitematicLoader.TryLoadFile(args[1]);
            if (!probeResult.Success)
            {
                Debug.WriteLine($"[MESH][probe] 载入失败 {probeResult.Error}");
                return 1;
            }

            LitematicRegion probeRegion = probeResult.Document!.Regions[0];
            Vector3I probeSize = probeRegion.Bounds.Size;
            // 全区域状态计数：与第三方解码器的逐状态计数对账。
            // 非空气计数一致不证明空间映射一致（计数与布局无关），逐状态计数才抓得住错位。
            // 逐非空气坐标 dump：第三方解码器同序 dump 后 diff，
            // 抓「计数一致但空间错位」这类布局 bug 的唯一硬证据。
            for (int py = 0; py < probeSize.Y; py++)
            {
                for (int pz = 0; pz < probeSize.Z; pz++)
                {
                    for (int px = 0; px < probeSize.X; px++)
                    {
                        BlockStateDefinition st = probeRegion.GetState(new Vector3I(px, py, pz));
                        if (!st.IsAir)
                        {
                            Debug.WriteLine($"[MESH][probe] {px},{py},{pz}={st}");
                        }
                    }
                }
            }

            return 0;
        }

        // --regionrender <out.png> <litematic> [边长] <资源包...>：真实 region 的软件光栅化，
        // 与 GPU 截帧对照用（见 PreviewBlocks.RunRegionRender）。
        if (args.Length >= 3 && args[0] == "--regionrender")
        {
            return PreviewBlocks.RunRegionRender(args);
        }

        // --dumpverts <litematic> <资源包...>：打印头几个面的顶点原始数据，
        // 查面退化（四角几乎重合）时用。
        if (args.Length >= 3 && args[0] == "--dumpverts")
        {
            using TextWriterTraceListener dvListener = new(Console.Out);
            Trace.Listeners.Add(dvListener);
            Trace.AutoFlush = true;
            LoadResult dvResult = LitematicLoader.TryLoadFile(args[1]);
            if (!dvResult.Success)
            {
                Debug.WriteLine($"[MESH][dumpverts] 载入失败 {dvResult.Error}");
                return 1;
            }

            _packs = new PackStack();
            foreach (string path in args.Skip(2))
            {
                _packs.Add(Directory.Exists(path) ? ResourcePack.OpenFolder(path) : ResourcePack.OpenZip(path));
            }

            _resolver = new BlockStateResolver(_packs);
            BlockMeshBuilder dvCollector = new(_resolver, TextureAtlas.Build(_packs, []));
            HashSet<string> dvSprites = [];
            dvCollector.CollectSprites(dvResult.Document!.Regions, dvSprites);
            TextureAtlas dvAtlas = TextureAtlas.Build(_packs, dvSprites);
            BlockMeshBuilder dvBuilder = new(_resolver, dvAtlas);
            MeshData dvMesh = dvBuilder.BuildRegion(dvResult.Document.Regions[0]);
            int fpv = MeshData.FloatsPerVertex;
            Debug.WriteLine($"[MESH][dumpverts] verts={dvMesh.Vertices.Length / fpv} indices={dvMesh.Indices.Length} atlas={dvAtlas.Width}x{dvAtlas.Height}");
            for (int face = 0; face < Math.Min(48, dvMesh.Indices.Length); face += 6)
            {
                for (int k = 0; k < 4; k++)
                {
                    int v = dvMesh.Indices[face + k] * fpv;
                    Debug.WriteLine($"[MESH][dumpverts] face{face / 6} v{k} pos=({dvMesh.Vertices[v]:F3},{dvMesh.Vertices[v + 1]:F3},{dvMesh.Vertices[v + 2]:F3}) n=({dvMesh.Vertices[v + 3]:F2},{dvMesh.Vertices[v + 4]:F2},{dvMesh.Vertices[v + 5]:F2}) uv=({dvMesh.Vertices[v + 6]:F5},{dvMesh.Vertices[v + 7]:F5}) tint={dvMesh.Vertices[v + 8]:F0}");
                }
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
            LoadResult uvResult = LitematicLoader.TryLoadFile(args[1]);
            if (!uvResult.Success)
            {
                Debug.WriteLine($"[MESH][uvhist] 载入失败 {uvResult.Error}");
                return 1;
            }

            _packs = new PackStack();
            foreach (string path in args.Skip(2))
            {
                _packs.Add(Directory.Exists(path) ? ResourcePack.OpenFolder(path) : ResourcePack.OpenZip(path));
            }

            _resolver = new BlockStateResolver(_packs);
            BlockMeshBuilder uvCollector = new(_resolver, TextureAtlas.Build(_packs, []));
            HashSet<string> uvSprites = [];
            uvCollector.CollectSprites(uvResult.Document!.Regions, uvSprites);
            TextureAtlas uvAtlas = TextureAtlas.Build(_packs, uvSprites);
            BlockMeshBuilder uvBuilder = new(_resolver, uvAtlas);

            List<float> uvVerts = [];
            List<int> uvIndices = [];
            int uvBase = 0;
            foreach (LitematicRegion uvRegion in uvResult.Document.Regions)
            {
                MeshData uvMesh = uvBuilder.BuildRegion(uvRegion);
                uvVerts.AddRange(uvMesh.Vertices);
                foreach (int i in uvMesh.Indices)
                {
                    uvIndices.Add(i + uvBase);
                }

                uvBase += uvMesh.Vertices.Length / MeshData.FloatsPerVertex;
            }

            // 每 4 个连续顶点是一个面（四边形），uv 中心取四顶点平均。
            Dictionary<string, int> rectHits = new(StringComparer.Ordinal);
            int straddleFaces = 0;
            for (int face = 0; face < uvIndices.Count; face += 6)
            {
                float uSum = 0, vSum = 0;
                for (int k = 0; k < 6; k++)
                {
                    int vertex = uvIndices[face + k];
                    uSum += uvVerts[(vertex * MeshData.FloatsPerVertex) + MeshData.UvOffset];
                    vSum += uvVerts[(vertex * MeshData.FloatsPerVertex) + MeshData.UvOffset + 1];
                }

                // 6 个索引只有 4 个独立顶点；四点平均直接用头 4 个索引去重即可，
                // 这里偷懒用 6 个索引的平均（对中心位置没有影响，权重差可忽略）。
                float u = uSum / 6f;
                // v 不翻转（v = 图集 y/H），反推图集行：py = v*H。
                float v = vSum / 6f;
                float px = u * uvAtlas.Width;
                float py = v * uvAtlas.Height;
                string? hit = null;
                foreach (SpriteRect rect in uvAtlas.Rects)
                {
                    if (px >= rect.X && px < rect.X + rect.Width && py >= rect.Y && py < rect.Y + rect.Height)
                    {
                        hit = rect.Sprite;
                        break;
                    }
                }

                if (hit is null)
                {
                    straddleFaces++;
                }
                else
                {
                    rectHits[hit] = rectHits.GetValueOrDefault(hit) + 1;
                }
            }

            Debug.WriteLine($"[MESH][uvhist] 图集 {uvAtlas.Width}x{uvAtlas.Height} sprites={uvAtlas.Rects.Count}");
            foreach ((string sprite, int count) in rectHits.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                Debug.WriteLine($"[MESH][uvhist] {sprite} faces={count}");
            }

            Debug.WriteLine($"[MESH][uvhist] rect 外的面={straddleFaces}");
            return 0;
        }

        using TextWriterTraceListener listener = new(Console.Out);
        Trace.Listeners.Add(listener);
        Trace.AutoFlush = true;

        _packs = new PackStack();
        List<string> litematics = [];
        foreach (string path in args)
        {
            if (path.EndsWith(".litematic", StringComparison.OrdinalIgnoreCase))
            {
                litematics.Add(path);
            }
            else
            {
                ResourcePack pack = Directory.Exists(path) ? ResourcePack.OpenFolder(path) : ResourcePack.OpenZip(path);
                _packs.Add(pack);
                Debug.WriteLine($"[MESH][smoke] 装包 {pack.Name}");
            }
        }

        _resolver = new BlockStateResolver(_packs);

        CheckSingleBlock();
        CheckFaceCulling();
        CheckLogAxisRotation();
        foreach (string path in litematics)
        {
            CheckRealFile(path);
        }

        Debug.WriteLine($"[MESH][smoke] 完成 checks={_checks}");
        return 0;
    }

    private static BlockMeshBuilder CreateBuilder(params IEnumerable<string> extraSprites)
    {
        HashSet<string> sprites = [];
        foreach (string sprite in extraSprites)
        {
            sprites.Add(sprite);
        }

        TextureAtlas atlas = TextureAtlas.Build(_packs, sprites);
        Debug.WriteLine(
            $"[MESH][smoke] 图集 {atlas.Width}x{atlas.Height} sprites={atlas.Rects.Count} 缺失={atlas.MissingCount}");
        return new BlockMeshBuilder(_resolver, atlas);
    }

    private static LitematicRegion MakeRegion(Vector3I size, BlockStateDefinition[] palette, int[] blocks)
    {
        Vector3I position = Vector3I.Zero;
        return new LitematicRegion(
            "smoke",
            position,
            size,
            IntBounds.FromPositionSize(position, size),
            [.. palette],
            [.. blocks]);
    }

    private static readonly BlockStateDefinition Stone = new("minecraft:stone", BlockStateDefinition.NoProperties);
    private static readonly BlockStateDefinition Air = new("minecraft:air", BlockStateDefinition.NoProperties);

    private static void CheckSingleBlock()
    {
        BlockMeshBuilder builder = CreateBuilder(["minecraft:block/stone"]);
        MeshData mesh = builder.BuildRegion(MakeRegion(new Vector3I(1, 1, 1), [Stone], [0]));

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
        BlockMeshBuilder builder = CreateBuilder(["minecraft:block/stone"]);
        MeshData mesh = builder.BuildRegion(MakeRegion(new Vector3I(2, 1, 1), [Stone], [0, 0]));

        Debug.Assert(mesh.Indices.Length == 60, $"[MESH][smoke] 双方块索引={mesh.Indices.Length} expected=60（剔除失效会是 72）");
        Debug.WriteLine("[MESH][smoke] 双方块相接: 10 面 60 索引，共享面被剔除 ✓");
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
        TextureAtlas atlas = TextureAtlas.Build(_packs, ["minecraft:block/oak_log", "minecraft:block/oak_log_top"]);
        BlockMeshBuilder builder = new(_resolver, atlas);
        MeshData mesh = builder.BuildRegion(MakeRegion(new Vector3I(1, 1, 1), [logX], [0]));

        Debug.Assert(mesh.VertexCount == 24, $"[MESH][smoke] 原木顶点={mesh.VertexCount} expected=24（旋转不应增删面）");

        atlas.TryGetRect("minecraft:block/oak_log_top", out SpriteRect topRect);
        float uLow = (float)topRect.X / atlas.Width;
        float uHigh = (float)(topRect.X + topRect.Width) / atlas.Width;
        // v 不翻转（v = 图集 y/H）：Y 小的 rect v 也小，区间直接按 Y 摆。
        float vLow = (float)topRect.Y / atlas.Height;
        float vHigh = (float)(topRect.Y + topRect.Height) / atlas.Height;

        // 按「面的 uv 中心」分类，不按单顶点：两个 rect 上下堆叠时共享 v=0.5 这条边，
        // 侧面底边的顶点恰好压线，按顶点判会把侧面误判成端帽。
        // 面中心永远落在 rect 内部，压不了线。网格生成保证每 4 个连续顶点是一个面。
        int endCapFaces = 0;
        for (int faceStart = 0; faceStart < mesh.VertexCount; faceStart += 4)
        {
            Vector2 uvSum = Vector2.Zero;
            Vector3 normalSum = Vector3.Zero;
            for (int i = faceStart; i < faceStart + 4; i++)
            {
                uvSum += ReadUv(mesh, i);
                normalSum += ReadNormal(mesh, i);
            }

            Vector2 centre = uvSum / 4f;
            bool inTopRect = centre.X >= uLow && centre.X < uHigh && centre.Y >= vLow && centre.Y < vHigh;
            if (!inTopRect)
            {
                continue;
            }

            endCapFaces++;
            Vector3 normal = Vector3.Normalize(normalSum);
            Debug.Assert(
                MathF.Abs(normal.X) > 0.999f && MathF.Abs(normal.Y) < 1e-3f && MathF.Abs(normal.Z) < 1e-3f,
                $"[MESH][smoke] 横放原木的端面帽朝向不对 normal={normal} expected=±X " +
                "note=variant 旋转的符号或顺序错了，改 Rotate/ApplyVariantRotation");
        }

        Debug.Assert(endCapFaces == 2, $"[MESH][smoke] 端面帽应 2 个面（两端）实得 {endCapFaces}");
        Debug.WriteLine("[MESH][smoke] oak_log[axis=x]: 端面帽 ±X ✓（旋转约定钉住）");
        _checks++;
    }

    private static void CheckRealFile(string path)
    {
        LoadResult result = LitematicLoader.TryLoadFile(path);
        Debug.Assert(result.Success && result.Document is not null, $"[MESH][smoke] 打不开 {path}: {result.Error} {result.Message}");
        LitematicDocument document = result.Document!;

        HashSet<string> sprites = [];
        BlockMeshBuilder collector = CreateBuilder();
        collector.CollectSprites(document.Regions, sprites);
        Debug.WriteLine($"[MESH][smoke] {Path.GetFileName(path)}: {document.Regions.Length} 个 region、" +
                        $"{document.TotalBlocks} 方块、{sprites.Count} 张 sprite");

        TextureAtlas atlas = TextureAtlas.Build(_packs, sprites);
        Debug.WriteLine(
            $"[MESH][smoke] 图集 {atlas.Width}x{atlas.Height}（缺 {atlas.MissingCount} 张" +
            (atlas.MissingCount > 0 ? $"：{string.Join(", ", atlas.MissingSprites)}）" : "）"));

        BlockMeshBuilder builder = new(_resolver, atlas);
        long totalFaces = 0;
        long totalVerts = 0;
        Stopwatch clock = Stopwatch.StartNew();
        foreach (LitematicRegion region in document.Regions)
        {
            // 诊断：调色板里解不出任何面的状态。这种方块整块消失，和剔除误删长得一样，
            // 但关剔除救不了它，所以单独列出来。
            SortedSet<string> zeroFaceStates = [];
            foreach (BlockStateDefinition def in region.Palette)
            {
                if (def.IsAir)
                {
                    continue;
                }

                ResolvedBlockState resolved = _resolver.Resolve(def.ToString());
                int faceCount = resolved.Variants.Sum(v => v.Model.Elements.Sum(e => e.Faces.Count));
                if (faceCount == 0)
                {
                    zeroFaceStates.Add(def.ToString());
                }
            }

            if (zeroFaceStates.Count > 0)
            {
                Debug.WriteLine(
                    $"[MESH][smoke] {region.Name} 有 {zeroFaceStates.Count} 个零面状态: " +
                    string.Join(", ", zeroFaceStates));
            }

            MeshData mesh = builder.BuildRegion(region);
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
        for (int i = 0; i < mesh.VertexCount; i++)
        {
            Vector3 normal = ReadNormal(mesh, i);
            Debug.Assert(
                MathF.Abs(normal.Length() - 1f) < 1e-3f,
                $"[MESH][smoke] 顶点 {i} 法线非单位 normal={normal}");

            Vector2 uv = ReadUv(mesh, i);
            Debug.Assert(
                uv.X >= -1e-4f && uv.X <= 1f + 1e-4f && uv.Y >= -1e-4f && uv.Y <= 1f + 1e-4f,
                $"[MESH][smoke] 顶点 {i} uv 越界 uv={uv}");
        }
    }

    private static Vector3 ReadNormal(MeshData mesh, int vertex) => new(
        mesh.Vertices[(vertex * MeshData.FloatsPerVertex) + MeshData.NormalOffset],
        mesh.Vertices[(vertex * MeshData.FloatsPerVertex) + MeshData.NormalOffset + 1],
        mesh.Vertices[(vertex * MeshData.FloatsPerVertex) + MeshData.NormalOffset + 2]);

    private static Vector2 ReadUv(MeshData mesh, int vertex) => new(
        mesh.Vertices[(vertex * MeshData.FloatsPerVertex) + MeshData.UvOffset],
        mesh.Vertices[(vertex * MeshData.FloatsPerVertex) + MeshData.UvOffset + 1]);
}
