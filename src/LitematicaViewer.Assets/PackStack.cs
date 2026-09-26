namespace LitematicaViewer.Assets;

// 分层查找：后加进来的包优先。这就是 MC 资源包栈的语义——
// 用户包盖原版，两个用户包之间按顺序压。原版 jar 是栈底。
public sealed class PackStack : IDisposable
{
    private readonly List<ResourcePack> _packs = [];

    public int Count => _packs.Count;

    public void Dispose()
    {
        foreach (var pack in _packs) pack.Dispose();

        _packs.Clear();
    }

    public void Add(ResourcePack pack)
    {
        _packs.Add(pack);
    }

    public bool TryRead(string path, out byte[] content)
    {
        for (var i = _packs.Count - 1; i >= 0; i--)
            if (_packs[i].TryRead(path, out content))
                return true;

        content = [];
        return false;
    }

    // 所有层里该前缀下条目的并集。同一名字可能出现在多层，去重交给调用方按需处理：
    // 读内容走 TryRead 自然取到最高层，「并集里有哪些名字」和「值是什么」是两个问题。
    public IEnumerable<string> Enumerate(string prefix)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (var pack in _packs)
        foreach (var entry in pack.Enumerate(prefix))
            if (seen.Add(entry))
                yield return entry;
    }
}
