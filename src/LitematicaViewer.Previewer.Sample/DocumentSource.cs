using System.Collections.Immutable;
using System.Diagnostics;
using System.Numerics;
using Avalonia.Threading;
using LitematicaViewer.Assets;
using LitematicaViewer.Core.Model;
using LitematicaViewer.Core.Parsing;
using LitematicaViewer.Meshing;

namespace LitematicaViewer.Previewer.Sample;

// 载入一个 .litematic 并把它变成渲染与展台要的三样东西：
// 一份合并网格（自由视角用）、一张图集、每个 region 一个展台目标。
//
// 解析与网格化全在后台线程；UI 线程只收成品（SetMesh / 相机 / 侧边栏文本）。
// 成品经 Dispatcher 回 UI：不碰共享可变状态，所以全程不需要锁（R3）。
// 连续拖两个文件时后到的请求会把代次 +1，先完成的那个回调一看代次对不上就自己放弃——
// 否则先启动的慢解析会覆盖后启动的快解析，画面停在「先拖的那个文件」上。
internal sealed class DocumentSource
{
    // 载入结果的全部产出（R6）：数组只在这里造一次，之后谁都不改。
    internal sealed record LoadedDocument(
        string FileName,
        int RegionCount,
        long TotalBlocks,
        float[] MergedVertices,
        int[] MergedIndices,
        byte[] AtlasRgba,
        int AtlasWidth,
        int AtlasHeight,
        ImmutableArray<ShowcaseTarget> Targets,
        Vector3 WholeCentre,
        float WholeRadius,
        LitematicDocument Document,
        string DebugNotes);

    private int _generation;

    /// <summary>载入完成或失败。UI 线程回调；失败时 document 为 null、message 给原因。</summary>
    public event Action<string, LoadedDocument?>? Completed;

    // 找资源包目录：packs/ 在仓库根，而程序跑在 bin/.../Debug/net10.0 下，
    // 从程序目录往上最多七层，谁先命中用谁。
    private static string? FindPacksDirectory()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        for (int i = 0; i < 7 && dir is not null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "packs");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    // 原版 jar 必须先入栈（栈底），材质包在后覆盖：按「先 jar 后其余」分组排序，
    // 不靠文件名字典序——ordinal 下 "XK…" 排在 "vanilla…" 前面，靠名字排序会把材质包压到栈底，
    // 原版贴图盖在它上面，覆盖失效而且不报错。
    internal static PackStack BuildPackStack(string packsDirectory, out string notes)
    {
        PackStack packs = new();
        List<string> jars = [];
        List<string> others = [];
        foreach (string entry in Directory.EnumerateFileSystemEntries(packsDirectory))
        {
            if (Directory.Exists(entry) ||
                entry.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                entry.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            {
                (entry.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ? jars : others).Add(entry);
            }
        }

        jars.Sort(StringComparer.Ordinal);
        others.Sort(StringComparer.Ordinal);

        List<string> loaded = [];
        foreach (string path in jars.Concat(others))
        {
            ResourcePack pack = Directory.Exists(path)
                ? ResourcePack.OpenFolder(path)
                : ResourcePack.OpenZip(path);
            packs.Add(pack);
            loaded.Add(pack.Name);
        }

        notes = $"packs={loaded.Count} [{string.Join(", ", loaded)}]";
        return packs;
    }

    public void Load(string path)
    {
        int generation = ++_generation;
        Debug.WriteLine($"[SAMPLE][source] 开始载入 generation={generation} path={path}");

        Task.Run(() =>
        {
            LoadedDocument? document = null;
            string error = "";
            try
            {
                document = LoadCore(path, out string notes);
                document = document with { DebugNotes = notes };
            }
            catch (Exception ex)
            {
                // 后台线程的异常没人接就是静默失败：这里全部拦下来交给 UI 那边报。
                error = $"{ex.GetType().Name}: {ex.Message}";
                Debug.WriteLine($"[SAMPLE][source] 载入失败 {path} {error}\n{ex.StackTrace}");
            }

            LoadedDocument? result = document;
            string message = error;
            Dispatcher.UIThread.Post(() =>
            {
                // 代次对不上说明已经有更新的请求在后面，这一份作废。
                if (generation != _generation)
                {
                    Debug.WriteLine(
                        $"[SAMPLE][source] 丢弃过期结果 generation={generation} current={_generation} path={path}");
                    return;
                }

                Completed?.Invoke(path, result);
                if (result is null)
                {
                    Debug.WriteLine($"[SAMPLE][source] 载入失败上报 path={path} error={message}");
                }
            });
        });
    }

    private static LoadedDocument LoadCore(string path, out string notes)
    {
        LoadResult result = LitematicLoader.TryLoadFile(path);
        if (!result.Success || result.Document is null)
        {
            throw new InvalidDataException($"{result.Error} {result.Message}");
        }

        LitematicDocument document = result.Document;

        string? packsDirectory = FindPacksDirectory();
        if (packsDirectory is null)
        {
            throw new DirectoryNotFoundException(
                "找不到 packs/ 资源包目录（把原版 client.jar 与材质包放进去）");
        }

        PackStack packs = BuildPackStack(packsDirectory, out string packNotes);
        BlockStateResolver resolver = new(packs);

        // sprite 全集先收齐再建图集：图集建出来之后 mesh 的 uv 才有落位可算。
        // 收集用的 builder 挂一个空图集——CollectSprites 直走 resolver，用不到落位；
        // 空集的 Build 出一张 16x1 的空图集，纯属给构造函数占位。
        BlockMeshBuilder collector = new(resolver, TextureAtlas.Build(packs, []));
        HashSet<string> sprites = [];
        collector.CollectSprites(document.Regions, sprites);

        TextureAtlas atlas = TextureAtlas.Build(packs, sprites);
        BlockMeshBuilder builder = new(resolver, atlas);

        List<MeshData> parts = [];
        List<ShowcaseTarget> targets = [];
        Vector3I wholeMin = new(int.MaxValue, int.MaxValue, int.MaxValue);
        Vector3I wholeMax = new(int.MinValue, int.MinValue, int.MinValue);

        foreach (LitematicRegion region in document.Regions)
        {
            MeshData mesh = builder.BuildRegion(region);
            parts.Add(mesh);

            // 展台目标从 region 的网格包围盒来：中心是几何中心，半径是水平半对角线
            // （光环躺在水平面上，由它决定大小），底面是包围盒的最低点。
            IntBounds bounds = region.Bounds;
            Vector3 min = new(bounds.Min.X, bounds.Min.Y, bounds.Min.Z);
            // Max 是闭区间端点：那个方块自己占一格，几何边界要到 +1。
            Vector3 max = new(bounds.Max.X + 1, bounds.Max.Y + 1, bounds.Max.Z + 1);
            Vector3 centre = (min + max) * 0.5f;
            float radius = MathF.Sqrt(
                ((max.X - min.X) * 0.5f) * ((max.X - min.X) * 0.5f) +
                ((max.Z - min.Z) * 0.5f) * ((max.Z - min.Z) * 0.5f));
            targets.Add(new ShowcaseTarget(region.Name, centre, radius, min.Y));

            wholeMin = Vector3I.ComponentMin(wholeMin, bounds.Min);
            wholeMax = Vector3I.ComponentMax(wholeMax, bounds.Max);
        }

        (float[] vertices, int[] indices) = MergeMeshes(parts);

        Vector3 wholeCentre = new(
            (wholeMin.X + wholeMax.X + 1) * 0.5f,
            (wholeMin.Y + wholeMax.Y + 1) * 0.5f,
            (wholeMin.Z + wholeMax.Z + 1) * 0.5f);
        float wholeRadius = MathF.Sqrt(
            ((wholeMax.X - wholeMin.X + 1) * 0.5f) * ((wholeMax.X - wholeMin.X + 1) * 0.5f) +
            ((wholeMax.Z - wholeMin.Z + 1) * 0.5f) * ((wholeMax.Z - wholeMin.Z + 1) * 0.5f));

        notes = $"{packNotes} atlas={atlas.Width}x{atlas.Height} missingSprites={atlas.MissingCount} " +
                (atlas.MissingCount > 0
                    ? $"[{string.Join(", ", atlas.MissingSprites.Take(8))}] "
                    : "") +
                $"sprites={sprites.Count} {builder.Stats}";

        return new LoadedDocument(
            Path.GetFileName(path),
            document.Regions.Length,
            document.TotalBlocks,
            vertices,
            indices,
            atlas.Pixels,
            atlas.Width,
            atlas.Height,
            [.. targets],
            wholeCentre,
            wholeRadius,
            document,
            notes);
    }

    // 多个 region 的网格拼成一份：顶点直接接起来，索引补上各自的基础顶点号。
    // 合并发生在后台线程的一次性数组里，渲染侧（SetMesh）永远只见一份完整数据。
    private static (float[] Vertices, int[] Indices) MergeMeshes(List<MeshData> parts)
    {
        int vertexFloats = parts.Sum(p => p.Vertices.Length);
        int indexCount = parts.Sum(p => p.Indices.Length);
        float[] vertices = new float[vertexFloats];
        int[] indices = new int[indexCount];

        int vertexCursor = 0;
        int indexCursor = 0;
        int baseVertex = 0;
        foreach (MeshData part in parts)
        {
            Array.Copy(part.Vertices, 0, vertices, vertexCursor, part.Vertices.Length);
            for (int i = 0; i < part.Indices.Length; i++)
            {
                indices[indexCursor + i] = part.Indices[i] + baseVertex;
            }

            vertexCursor += part.Vertices.Length;
            indexCursor += part.Indices.Length;
            baseVertex += part.VertexCount;
        }

        return (vertices, indices);
    }
}
