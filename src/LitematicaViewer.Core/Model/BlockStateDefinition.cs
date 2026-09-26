using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text;

namespace LitematicaViewer.Core.Model;

public sealed record BlockStateDefinition(string Name, ImmutableDictionary<string, string> Properties)
{
    // 三个空气变体都算空气。只判 "minecraft:air" 会把洞穴与虚空里的方块算成实心，
    // 在 TotalBlocks 上体现为凭空多出来的方块数。
    private static readonly FrozenSet<string> AirNames =
        new[] { "minecraft:air", "minecraft:cave_air", "minecraft:void_air" }
            .ToFrozenSet(StringComparer.Ordinal);

    public static ImmutableDictionary<string, string> NoProperties => ImmutableDictionary<string, string>.Empty;

    public bool IsAir => AirNames.Contains(Name);

    public static bool IsAirName(string name)
    {
        return AirNames.Contains(name);
    }

    // 属性按 key 排序后输出。ImmutableDictionary 的枚举顺序未定义，
    // 不排序的话同一份投影在不同进程里会拼出不同的字符串，
    // 所有以 ToString() 为键的调色板去重与网格缓存都会跟着失效。
    public override string ToString()
    {
        if (Properties.Count == 0) return Name;

        StringBuilder builder = new(Name.Length + Properties.Count * 16);
        builder.Append(Name).Append('[');
        var first = true;
        foreach (var property in Properties.OrderBy(static p => p.Key, StringComparer.Ordinal))
        {
            if (!first) builder.Append(',');

            first = false;
            builder.Append(property.Key).Append('=').Append(property.Value);
        }

        return builder.Append(']').ToString();
    }
}
