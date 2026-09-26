using System.Diagnostics;
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

    public static int Main(string[] args)
    {
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
        // v 已翻转：rect 越靠上（Y 小）翻转后数值越大，区间要按 min/max 摆正。
        float vLow = 1f - ((float)(topRect.Y + topRect.Height) / atlas.Height);
        float vHigh = 1f - ((float)topRect.Y / atlas.Height);

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
