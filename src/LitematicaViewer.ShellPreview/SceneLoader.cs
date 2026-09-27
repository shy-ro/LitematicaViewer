using System.Numerics;
using System.Runtime.InteropServices;
using LitematicaViewer.Assets;
using LitematicaViewer.Core.Model;
using LitematicaViewer.Core.Parsing;
using LitematicaViewer.Meshing;

namespace LitematicaViewer.ShellPreview;

// 把 .litematic 变成可光栅化的 Scene。链路与 Sample/PreviewBlocks 同一条：
// packs 栈 → resolver → CollectSprites → 图集 → BuildRegion。
internal static class SceneLoader
{
    public static Scene Load(string litematicPath)
    {
        var packsDirectory = FindPacksDirectory()
            ?? throw new DirectoryNotFoundException(
                "找不到 packs/ 资源包目录（把原版 client.jar 与材质包放进去，与预览器同一套目录约定）");

        var result = LitematicLoader.TryLoadFile(litematicPath);
        if (!result.Success || result.Document is null)
            throw new InvalidDataException($"{result.Error} {result.Message}");
        var document = result.Document;

        using PackStack packs = BuildPackStack(packsDirectory);
        BlockStateResolver resolver = new(packs);

        // 空图集的 builder 只为 CollectSprites 占位：收集直走 resolver，用不到落位。
        BlockMeshBuilder collector = new(resolver, TextureAtlas.Build(packs, []));
        HashSet<string> sprites = [];
        collector.CollectSprites(document.Regions, sprites);

        var atlas = TextureAtlas.Build(packs, sprites);
        BlockMeshBuilder builder = new(resolver, atlas);

        List<MeshData> meshes = [];
        Vector3I wholeMin = new(int.MaxValue, int.MaxValue, int.MaxValue);
        Vector3I wholeMax = new(int.MinValue, int.MinValue, int.MinValue);
        foreach (var region in document.Regions)
        {
            meshes.Add(builder.BuildRegion(region));
            wholeMin = Vector3I.ComponentMin(wholeMin, region.Bounds.Min);
            wholeMax = Vector3I.ComponentMax(wholeMax, region.Bounds.Max);
        }

        // Max 是闭区间端点：那个方块自己占一格，几何边界要到 +1。
        Vector3 centre = new(
            (wholeMin.X + wholeMax.X + 1) * 0.5f,
            (wholeMin.Y + wholeMax.Y + 1) * 0.5f,
            (wholeMin.Z + wholeMax.Z + 1) * 0.5f);
        var sx = wholeMax.X - wholeMin.X + 1;
        var sy = wholeMax.Y - wholeMin.Y + 1;
        var sz = wholeMax.Z - wholeMin.Z + 1;
        // 球形半径：取景用整包围盒的对角半径，不然侧面会竖着出画面。
        var radius = MathF.Max(1f, 0.5f * MathF.Sqrt(sx * sx + sy * sy + sz * sz));

        return new Scene(meshes, centre, radius, atlas);
    }

    // packs/ 在仓库根，dll 在 dist/ShellPreview 之类的地方：从 dll 自身目录往上
    // 最多七层，谁先命中用谁。不能用 AppContext.BaseDirectory——那是宿主进程
    // exe 的目录（prevhost/python/...），COM dll 里只有模块路径是可靠的。
    private static string? FindPacksDirectory()
    {
        if (!GetSelfModulePath(out var dllPath))
            return null;

        DirectoryInfo? dir = new(Path.GetDirectoryName(Path.GetFullPath(dllPath))!);
        for (var i = 0; i < 7 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "packs");
            if (Directory.Exists(candidate))
                return candidate;
        }

        return null;
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
