using System.Diagnostics;
using System.IO.Compression;

namespace LitematicaViewer.Assets;

// 一个资源包的统一读法：zip（jar 就是 zip）、文件夹都算包。
// 条目路径相对包根，如 "assets/minecraft/blockstates/stone.json"。
// 谁也不在这里解释 MC 资产格式——这里是纯粹的「路径到字节」。
public sealed class ResourcePack : IDisposable
{
    private readonly string? _folderRoot;
    private readonly ZipArchive? _zip;
    private readonly Dictionary<string, ZipArchiveEntry> _zipEntries;
    private readonly FileStream? _zipStream;

    private ResourcePack(string name, FileStream zipStream, ZipArchive zip, Dictionary<string, ZipArchiveEntry> entries)
    {
        Name = name;
        _zipStream = zipStream;
        _zip = zip;
        _zipEntries = entries;
    }

    private ResourcePack(string name, string folderRoot)
    {
        Name = name;
        _folderRoot = folderRoot;
        _zipEntries = [];
    }

    public string Name { get; }

    public void Dispose()
    {
        _zip?.Dispose();
        _zipStream?.Dispose();
    }

    // jar 与 zip 同构，一个入口就够；文件夹另算。
    public static ResourcePack OpenZip(string path)
    {
        FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        ZipArchive zip = new(stream, ZipArchiveMode.Read);
        Dictionary<string, ZipArchiveEntry> entries = new(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
            // 目录条目（以 / 结尾）没有内容，跳过；大小写不归一：MC 资产路径本身就是小写约定，
            // 在这里做大小写折叠会掩盖真正的拼写问题。
            if (!entry.FullName.EndsWith('/') && !entries.TryAdd(entry.FullName, entry))
                Debug.WriteLine($"[ASSETS][pack] 重复条目被忽略 pack={path} entry={entry.FullName}");

        return new ResourcePack(Path.GetFileName(path), stream, zip, entries);
    }

    public static ResourcePack OpenFolder(string path)
    {
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"资源包文件夹不存在：{path}");

        return new ResourcePack(new DirectoryInfo(path).Name, path);
    }

    public bool TryRead(string path, out byte[] content)
    {
        if (_zip is not null)
        {
            if (_zipEntries.TryGetValue(path, out var entry))
            {
                using var stream = entry.Open();
                using MemoryStream buffer = new((int)entry.Length);
                stream.CopyTo(buffer);
                content = buffer.ToArray();
                return true;
            }

            content = [];
            return false;
        }

        var full = Path.Combine(_folderRoot!, path);
        if (File.Exists(full))
        {
            content = File.ReadAllBytes(full);
            return true;
        }

        content = [];
        return false;
    }

    // 前缀下的全部条目。zip 与文件夹在这里的行为天然一致；
    // 图集收集（列出某个命名空间全部贴图）是它目前唯一的用途。
    public IEnumerable<string> Enumerate(string prefix)
    {
        if (_zip is not null) return _zipEntries.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal));

        var root = Path.Combine(_folderRoot!, prefix);
        if (!Directory.Exists(root)) return [];

        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(_folderRoot!, f).Replace('\\', '/'));
    }
}
