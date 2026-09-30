using System.Numerics;
using System.Runtime.InteropServices;
using LitematicaViewer.Assets;
using LitematicaViewer.Core.Model;
using LitematicaViewer.Core.Parsing;
using LitematicaViewer.Meshing;

namespace LitematicaViewer.ShellPreview;

// 场景快照：合并网格 + 图集 mip 链 + 展台取景数据。后台线程整块构建，
// 通过消息交给 STA 线程装进 GPU 渲染器，之后只读（R6）。
// Centre/Radius/BaseY 的语义与 Sample 的 ShowcaseTarget 一致：
// 半径是水平半对角线（光环躺在水平面上），BaseY 是包围盒最低点。
internal sealed record Scene(
    float[] Vertices,
    int[] Indices,
    byte[][] AtlasLevels,
    int AtlasWidth,
    int AtlasHeight,
    Vector3 Centre,
    float Radius,
    float BaseY);

// 把 .litematic 变成 GPU 渲染器的装填数据。链路与 Sample 的 DocumentSource 同一条：
// packs 栈 → resolver → CollectSprites → 图集 → BuildRegion → 合并网格。
internal static class SceneLoader
{
    public static Scene Load(string litematicPath)
    {
        Exports.Log($"packs search: self={SelfModulePathForLog()}");
        var packsDirectory = FindPacksDirectory();
        if (packsDirectory is null)
        {
            Exports.Log("packs search: MISS after 7 levels up");
            throw new DirectoryNotFoundException(
                "找不到 packs/ 资源包目录（把原版 client.jar 与材质包放进去，与预览器同一套目录约定）");
        }

        Exports.Log($"packs search: HIT {packsDirectory}");

        var result = LitematicLoader.TryLoadFile(litematicPath);
        if (!result.Success || result.Document is null)
            throw new InvalidDataException($"{result.Error} {result.Message}");
        var document = result.Document;

        using PackStack packs = BuildPackStack(packsDirectory);
        BlockStateResolver resolver = new(packs);

        // 空图集的 builder 只为 CollectSprites 占位：收集直走 resolver，用不到落位。
        BlockMeshBuilder collector = new(resolver, TextureAtlas.Build(packs, []));
        HashSet<string> sprites = [];
        List<GeneratedSprite> generatedSprites = [];
        collector.CollectSprites(document.Regions, sprites, packs, generatedSprites);

        var atlas = TextureAtlas.Build(packs, sprites, generatedSprites);
        BlockMeshBuilder builder = new(resolver, atlas);

        List<MeshData> meshes = [];
        foreach (var region in document.Regions)
        {
            meshes.Add(builder.BuildRegion(region));
        }

        var (vertices, indices) = MergeMeshes(meshes);

        // 展台取景数据从合并网格的实际顶点算，不从 region bounds 算：
        // BuildRegion 的输出坐标空间与 region.Bounds 不保证一致（实测机甲文件
        // 两者错位，光环浮到模型顶上），而网格顶点是渲染用的唯一事实。
        // 步长必须取 MeshData.FloatsPerVertex（10），不能写死——顶点布局加过
        // ao 之后从 9 变 10，写死 9 会在末尾多走一步，读越界直接抛
        // IndexOutOfRangeException（黄色轿车.litematic 就是这样载入失败的）。
        Vector3 meshMin = new(float.MaxValue, float.MaxValue, float.MaxValue);
        Vector3 meshMax = new(float.MinValue, float.MinValue, float.MinValue);
        for (var i = 0; i < vertices.Length; i += MeshData.FloatsPerVertex)
        {
            var x = vertices[i + MeshData.PositionOffset];
            var y = vertices[i + MeshData.PositionOffset + 1];
            var z = vertices[i + MeshData.PositionOffset + 2];
            if (x < meshMin.X) meshMin.X = x;
            if (y < meshMin.Y) meshMin.Y = y;
            if (z < meshMin.Z) meshMin.Z = z;
            if (x > meshMax.X) meshMax.X = x;
            if (y > meshMax.Y) meshMax.Y = y;
            if (z > meshMax.Z) meshMax.Z = z;
        }

        Vector3 centre = (meshMin + meshMax) * 0.5f;
        var hx = (meshMax.X - meshMin.X) * 0.5f;
        var hz = (meshMax.Z - meshMin.Z) * 0.5f;
        // 水平半对角线：展台取景与光环都由它定大小（语义对齐 Sample 的 ShowcaseTarget）。
        var radius = MathF.Max(1f, MathF.Sqrt(hx * hx + hz * hz));

        Exports.Log($"scene merged vertices={vertices.Length / MeshData.FloatsPerVertex} indices={indices.Length} " +
                    $"atlas={atlas.Width}x{atlas.Height} levels={atlas.Levels.Length}");
        Exports.Log($"bbox min=({meshMin.X:F1},{meshMin.Y:F1},{meshMin.Z:F1}) " +
                    $"max=({meshMax.X:F1},{meshMax.Y:F1},{meshMax.Z:F1}) " +
                    $"centre=({centre.X:F1},{centre.Y:F1},{centre.Z:F1}) radius={radius:F1} baseY={meshMin.Y:F1}");

        return new Scene(vertices, indices, atlas.Levels, atlas.Width, atlas.Height,
            centre, radius, meshMin.Y);
    }

    // 多个 region 的网格拼成一份：顶点直接接起来，索引补上各自的基础顶点号。
    private static (float[] Vertices, int[] Indices) MergeMeshes(List<MeshData> parts)
    {
        var vertexFloats = parts.Sum(p => p.Vertices.Length);
        var indexCount = parts.Sum(p => p.Indices.Length);
        var vertices = new float[vertexFloats];
        var indices = new int[indexCount];

        var vertexCursor = 0;
        var indexCursor = 0;
        var baseVertex = 0;
        foreach (var part in parts)
        {
            Array.Copy(part.Vertices, 0, vertices, vertexCursor, part.Vertices.Length);
            for (var i = 0; i < part.Indices.Length; i++) indices[indexCursor + i] = part.Indices[i] + baseVertex;

            vertexCursor += part.Vertices.Length;
            indexCursor += part.Indices.Length;
            baseVertex += part.VertexCount;
        }

        return (vertices, indices);
    }

    // packs/ 在安装目录（installer 解包到 <dll 目录>\packs\），仓库里开发时在根：
    // 从 dll 自身目录往上最多七层，谁先命中用谁。不能用 AppContext.BaseDirectory——
    // 那是宿主进程 exe 的目录（prevhost/python/...），COM dll 里只有模块路径是可靠的。
    private static string? FindPacksDirectory()
    {
        if (!GetSelfModulePath(out var dllPath))
        {
            Exports.Log("packs search: self module path lookup failed");
            return null;
        }

        DirectoryInfo? dir = new(Path.GetDirectoryName(Path.GetFullPath(dllPath))!);
        for (var i = 0; i < 7 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "packs");
            var hit = Directory.Exists(candidate);
            Exports.Log($"packs search: lv{i} {candidate} -> {(hit ? "HIT" : "no")}");
            if (hit)
                return candidate;
        }

        return null;
    }

    // 供取证日志用：模块自身路径；拿不到就标注失败原因。
    internal static string SelfModulePathForLog() =>
        GetSelfModulePath(out var p) ? p : "<module lookup failed>";

    // 模块安装目录（取证日志的可靠落点：装到哪就写哪，不依赖宿主进程环境）。
    internal static string SelfModuleDirectory()
    {
        if (!GetSelfModulePath(out var p))
            return Path.GetTempPath();
        return Path.GetDirectoryName(p) ?? Path.GetTempPath();
    }

    // 用本程序集内的函数地址反查 dll 模块，再取模块文件路径。
    private static unsafe bool GetSelfModulePath(out string path)
    {
        GetModuleHandleExW(
            0x4 /*FROM_ADDRESS*/ | 0x1 /*UNCHANGED_REFCOUNT*/,
            (nint)(delegate* unmanaged<int, int, int>)&Probe,
            out var module);
        if (module == 0)
        {
            path = "";
            return false;
        }

        var buffer = new char[1024];
        int length;
        do
        {
            length = GetModuleFileNameW(module, buffer, (uint)buffer.Length);
        }
        while (length == buffer.Length && buffer.Length < 8192);

        path = new string(buffer, 0, length);
        return length > 0;
    }

    // UnmanagedCallersOnly 保证 &Probe 取到的是真 unmanaged 入口——托管函数指针
    // 在 AOT 下转 delegate* unmanaged 不安全，GetModuleHandleExW 要的是裸地址。
    [UnmanagedCallersOnly]
    private static int Probe(int a, int b) => 0;

    [DllImport("kernel32.dll")]
    private static extern int GetModuleHandleExW(uint flags, nint address, out nint module);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetModuleFileNameW(nint module, char[] buffer, uint size);

    // 原版 jar 必须先入栈（栈底），材质包在后覆盖：按「先 jar 后其余」分组排序。
    // 不靠名字字典序——ordinal 下 "XK…" 排在 "vanilla…" 前面，会把覆盖关系压反。
    private static PackStack BuildPackStack(string packsDirectory)
    {
        PackStack packs = new();
        List<string> jars = [];
        List<string> others = [];
        foreach (var entry in Directory.EnumerateFileSystemEntries(packsDirectory))
            if (Directory.Exists(entry) ||
                entry.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                entry.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                (entry.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ? jars : others).Add(entry);

        jars.Sort(StringComparer.Ordinal);
        others.Sort(StringComparer.Ordinal);

        foreach (var path in jars.Concat(others))
        {
            var pack = Directory.Exists(path)
                ? ResourcePack.OpenFolder(path)
                : ResourcePack.OpenZip(path);
            packs.Add(pack);
        }

        return packs;
    }
}
