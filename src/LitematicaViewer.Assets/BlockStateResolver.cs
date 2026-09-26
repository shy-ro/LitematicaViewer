using System.Collections.Immutable;
using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using LitematicaViewer.Assets.Model;

namespace LitematicaViewer.Assets;

// blockstate json → variants/multipart 命中 → model parent 链 → 每面 sprite。
// 输出的所有坐标保持 MC 资产约定（1/16 方块、uv 0..16），变换是网格阶段的事。
public sealed class BlockStateResolver
{
    private readonly PackStack _packs;
    private readonly Dictionary<string, ModelSource?> _sources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ResolvedBlockModel> _models = new(StringComparer.Ordinal);

    public BlockStateResolver(PackStack packs) => _packs = packs;

    private int _resolveCount;
    private int _missCount;

    // blockId 形如 "minecraft:stone" 或 "minecraft:oak_stairs[facing=east,half=bottom]"。
    public ResolvedBlockState Resolve(string blockId)
    {
        int bracket = blockId.IndexOf('[');
        string name = bracket < 0 ? blockId : blockId[..bracket];
        ImmutableDictionary<string, string> properties = ImmutableDictionary<string, string>.Empty;
        if (bracket >= 0)
        {
            properties = ParseProperties(blockId[(bracket + 1)..blockId.IndexOf(']')]);
        }

        (string ns, string path) = SplitId(name);
        string statePath = $"assets/{ns}/blockstates/{path}.json";
        bool found = _packs.TryRead(statePath, out byte[] stateBytes);
        if (!found && ns == "minecraft"
            && BlockstateAliases.TryGetValue(path, out string? aliased))
        {
            // 投影文件按旧版本 id 写，资产按新版本存：26.x 把 chain 改名成了 iron_chain，
            // 老 id 直接 miss 的方块整个消失（模型一个面都没有），比缺贴图难看得多。
            // 表只收已实测改名的项；别名的 blockstate 模型同构，直接当原名解析。
            string aliasPath = $"assets/{ns}/blockstates/{aliased}.json";
            if (_packs.TryRead(aliasPath, out byte[] aliasBytes))
            {
                stateBytes = aliasBytes;
                found = true;
                Debug.WriteLine($"[ASSETS][resolve] blockstate 别名 {statePath} -> {aliasPath}");
            }
        }

        if (!found)
        {
            _missCount++;
            Debug.WriteLine($"[ASSETS][resolve] 找不到 blockstate {statePath}");
            return new ResolvedBlockState(blockId, []);
        }

        using JsonDocument doc = JsonDocument.Parse(stateBytes);
        JsonElement root = doc.RootElement;

        List<ResolvedVariant> variants = [];
        if (root.TryGetProperty("variants", out JsonElement variantsElement))
        {
            ResolveVariants(variantsElement, properties, variants);
        }
        else if (root.TryGetProperty("multipart", out JsonElement multipartElement))
        {
            ResolveMultipart(multipartElement, properties, variants);
        }
        else
        {
            Debug.WriteLine($"[ASSETS][resolve] blockstate 既无 variants 也无 multipart {statePath}");
        }

        if (variants.Count == 0)
        {
            _missCount++;
        }

        _resolveCount++;
        return new ResolvedBlockState(blockId, variants);
    }

    public string Stats => $"resolveCount={_resolveCount} missCount={_missCount}";

    // 投影文件 id（旧版本）→ 资产 id（新版本）的改名桥。键值都是 blockstates/ 下的名字。
    private static readonly Dictionary<string, string> BlockstateAliases = new(StringComparer.Ordinal)
    {
        ["chain"] = "iron_chain",
    };

    // ---------- blockstate 层 ----------

    private void ResolveVariants(JsonElement variants, ImmutableDictionary<string, string> properties, List<ResolvedVariant> output)
    {
        foreach (JsonProperty entry in variants.EnumerateObject())
        {
            // variants 的键是 "k=v,k=v"（可空）。缺某属性 = 通配；命中即收，MC 同一状态
            // 不会匹配两个键，多收是我们解析器的错，交给断言盯着。
            if (!Matches(entry.Name, properties))
            {
                continue;
            }

            switch (entry.Value.ValueKind)
            {
                case JsonValueKind.Object:
                    AddVariant(entry.Value, output);
                    break;
                case JsonValueKind.Array:
                    foreach (JsonElement item in entry.Value.EnumerateArray())
                    {
                        AddVariant(item, output);
                    }
                    break;
                default:
                    Debug.WriteLine($"[ASSETS][resolve] variants 值类型异常 kind={entry.Value.ValueKind} key={entry.Name}");
                    break;
            }
        }
    }

    private void ResolveMultipart(JsonElement multipart, ImmutableDictionary<string, string> properties, List<ResolvedVariant> output)
    {
        foreach (JsonElement part in multipart.EnumerateArray())
        {
            if (part.TryGetProperty("when", out JsonElement when) && !WhenMatches(when, properties))
            {
                continue;
            }

            if (part.TryGetProperty("apply", out JsonElement apply))
            {
                if (apply.ValueKind == JsonValueKind.Object)
                {
                    AddVariant(apply, output);
                }
                else if (apply.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in apply.EnumerateArray())
                    {
                        AddVariant(item, output);
                    }
                }
            }
        }
    }

    private void AddVariant(JsonElement variant, List<ResolvedVariant> output)
    {
        if (!variant.TryGetProperty("model", out JsonElement modelElement))
        {
            Debug.WriteLine("[ASSETS][resolve] variant 缺 model 字段，跳过");
            return;
        }

        float x = variant.TryGetProperty("x", out JsonElement xElement) ? xElement.GetSingle() : 0f;
        float y = variant.TryGetProperty("y", out JsonElement yElement) ? yElement.GetSingle() : 0f;
        string modelId = NormalizeId(modelElement.GetString()!, "models");
        output.Add(new ResolvedVariant(modelId, LoadModel(modelId), x, y));
    }

    private static ImmutableDictionary<string, string> ParseProperties(string inner) =>
        inner.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .Aggregate(
                ImmutableDictionary<string, string>.Empty,
                (acc, parts) => acc.SetItem(parts[0], parts[1]));

    private static bool Matches(string key, ImmutableDictionary<string, string> properties)
    {
        if (key.Length == 0)
        {
            return true;
        }

        foreach (string pair in key.Split(','))
        {
            string[] parts = pair.Split('=', 2);
            if (parts.Length != 2 || !properties.TryGetValue(parts[0], out string? value) || value != parts[1])
            {
                return false;
            }
        }

        return true;
    }

    private static bool WhenMatches(JsonElement when, ImmutableDictionary<string, string> properties)
    {
        if (when.TryGetProperty("OR", out JsonElement orElement))
        {
            return orElement.EnumerateArray().Any(item => WhenMatches(item, properties));
        }

        if (when.TryGetProperty("AND", out JsonElement andElement))
        {
            return andElement.EnumerateArray().All(item => WhenMatches(item, properties));
        }

        foreach (JsonProperty entry in when.EnumerateObject())
        {
            if (!properties.TryGetValue(entry.Name, out string? value))
            {
                return false;
            }

            bool matched = entry.Value.ValueKind == JsonValueKind.Array
                ? entry.Value.EnumerateArray().Any(v => v.ValueKind == JsonValueKind.String && v.GetString() == value)
                : entry.Value.ValueKind == JsonValueKind.String && entry.Value.GetString() == value;
            if (!matched)
            {
                return false;
            }
        }

        return true;
    }

    // ---------- model 层 ----------

    private sealed record ModelSource(string? Parent, Dictionary<string, string> Textures, JsonElement? Elements);

    private ModelSource? LoadSource(string modelId)
    {
        if (_sources.TryGetValue(modelId, out ModelSource? cached))
        {
            return cached;
        }

        (string ns, string path) = SplitId(modelId);
        string modelPath = $"assets/{ns}/models/{path}.json";

        ModelSource? source;
        if (!_packs.TryRead(modelPath, out byte[] bytes))
        {
            Debug.WriteLine($"[ASSETS][resolve] 找不到 model {modelPath}");
            source = null;
        }
        else
        {
            using JsonDocument doc = JsonDocument.Parse(bytes);
            JsonElement root = doc.RootElement;

            string? parent = root.TryGetProperty("parent", out JsonElement parentElement)
                ? NormalizeId(parentElement.GetString()!, "models")
                : null;

            Dictionary<string, string> textures = [];
            if (root.TryGetProperty("textures", out JsonElement texturesElement))
            {
                foreach (JsonProperty entry in texturesElement.EnumerateObject())
                {
                    if (entry.Value.ValueKind == JsonValueKind.String)
                    {
                        textures[entry.Name] = entry.Value.GetString()!;
                    }
                    else if (entry.Value.ValueKind == JsonValueKind.Object &&
                             entry.Value.TryGetProperty("sprite", out JsonElement spriteElement) &&
                             spriteElement.ValueKind == JsonValueKind.String)
                    {
                        // 26.3 的新写法：值可以是 {"sprite": "...", "force_translucent": ...}
                        // 这样的对象（原版 glass 就在用）。贴图表里只关心 sprite 一项；
                        // 对象值静默丢弃的话，引用它的一条链全断，画面上是一块品红。
                        textures[entry.Name] = spriteElement.GetString()!;
                    }
                }
            }

            JsonElement? elements = root.TryGetProperty("elements", out JsonElement elementsElement)
                ? elementsElement.Clone()
                : null;

            source = new ModelSource(parent, textures, elements);
        }

        _sources[modelId] = source;
        return source;
    }

    private ResolvedBlockModel LoadModel(string modelId)
    {
        if (_models.TryGetValue(modelId, out ResolvedBlockModel? cached))
        {
            return cached;
        }

        // 父链：合并贴图（子覆盖父），elements 取链上最近的一份。
        List<ModelSource> chain = [];
        string? cursor = modelId;
        while (cursor is not null && chain.Count < 16)
        {
            ModelSource? source = LoadSource(cursor);
            if (source is null)
            {
                break;
            }

            chain.Add(source);
            cursor = source.Parent;
        }

        Dictionary<string, string> textures = new(StringComparer.Ordinal);
        foreach (ModelSource source in chain.AsEnumerable().Reverse())
        {
            foreach ((string key, string value) in source.Textures)
            {
                textures[key] = value;
            }
        }

        JsonElement? elementsElement = chain.FirstOrDefault(s => s.Elements is not null)?.Elements;
        List<ModelElement> elements = elementsElement is null ? [] : ParseElements(modelId, elementsElement.Value, textures);

        ResolvedBlockModel model = new(modelId, elements);
        _models[modelId] = model;
        return model;
    }

    private List<ModelElement> ParseElements(string modelId, JsonElement elements, Dictionary<string, string> textures)
    {
        List<ModelElement> result = [];
        foreach (JsonElement element in elements.EnumerateArray())
        {
            Vector3 from = element.TryGetProperty("from", out JsonElement fromElement) ? ReadVector3(fromElement) : new Vector3(0, 0, 0);
            Vector3 to = element.TryGetProperty("to", out JsonElement toElement) ? ReadVector3(toElement) : new Vector3(16, 16, 16);

            ElementRotation? rotation = null;
            if (element.TryGetProperty("rotation", out JsonElement rotationElement))
            {
                int axis = rotationElement.TryGetProperty("axis", out JsonElement axisElement) ? axisElement.GetString() switch
                {
                    "x" => 0,
                    "y" => 1,
                    "z" => 2,
                    _ => -1,
                } : -1;
                float angle = rotationElement.TryGetProperty("angle", out JsonElement angleElement) ? angleElement.GetSingle() : 0f;
                Vector3 origin = rotationElement.TryGetProperty("origin", out JsonElement originElement)
                    ? ReadVector3(originElement)
                    : new Vector3(8, 8, 8);
                rotation = new ElementRotation(axis, angle, origin);
            }

            List<ElementFace> faces = [];
            if (element.TryGetProperty("faces", out JsonElement facesElement))
            {
                foreach (JsonProperty entry in facesElement.EnumerateObject())
                {
                    if (entry.Value.ValueKind != JsonValueKind.Object ||
                        !TryParseFaceName(entry.Name, out FaceName face))
                    {
                        continue;
                    }

                    JsonElement faceElement = entry.Value;
                    string? textureRef = faceElement.TryGetProperty("texture", out JsonElement textureElement)
                        ? textureElement.GetString()
                        : null;
                    if (textureRef is null)
                    {
                        continue;
                    }

                    // #引用在合并后的贴图表里解；解不开的（模型写错或 mod 资产残缺）
                    // 记日志给空 sprite，网格阶段把它当「这面没有贴图」处理，不炸整个模型。
                    string? sprite;
                    if (!textureRef.StartsWith('#'))
                    {
                        // 直接写贴图路径而不是 #引用 的情况（mod 资产里常见）。
                        sprite = textureRef;
                    }
                    else
                    {
                        // 表的键不带 #（"#down" 的键是 "down"），值才可能是下一层引用
                        // （cube_all 把 down/up/... 全指向 #all），迭代解到头。
                        string key = textureRef[1..];
                        sprite = textures.TryGetValue(key, out string? value) ? value : null;
                        int hops = 0;
                        while (sprite is not null && sprite.StartsWith('#') && textures.TryGetValue(sprite[1..], out string? next) && hops++ < 8)
                        {
                            sprite = next;
                        }
                    }

                    if (sprite is null || sprite.StartsWith('#'))
                    {
                        // 两种解不开：表里没有那个键（sprite 保持 null 或仍是原引用），
                        // 或迭代到上限还在引用里打转。统一记日志给空 sprite，
                        // 网格阶段把它当「这面没有贴图」处理，不炸整个模型。
                        // 不加这个判断的话，断在半路的引用会带着 "#" 逃进 NormalizeSprite，
                        // 变成 "minecraft:#all" 这样的假 sprite 名，图集为它放一张棋盘格、
                        // 网格照常出面——画面上是一块品红，而日志里什么都没有。
                        Debug.WriteLine($"[ASSETS][resolve] 贴图引用解不开 model={modelId} ref={textureRef}");
                        sprite = "";
                    }

                    Vector4 uv = faceElement.TryGetProperty("uv", out JsonElement uvElement)
                        ? ReadVector4(uvElement)
                        : new Vector4(0, 0, 16, 16);

                    string? cullface = faceElement.TryGetProperty("cullface", out JsonElement cullElement)
                        ? cullElement.GetString()
                        : null;

                    int tintIndex = faceElement.TryGetProperty("tintindex", out JsonElement tintElement)
                        ? tintElement.GetInt32()
                        : -1;

                    int uvRotation = faceElement.TryGetProperty("rotation", out JsonElement faceRotationElement)
                        ? faceRotationElement.GetInt32()
                        : 0;

                    faces.Add(new ElementFace(face, uv, NormalizeSprite(sprite), cullface, tintIndex, uvRotation));
                }
            }

            result.Add(new ModelElement(from, to, rotation, faces));
        }

        return result;
    }

    private static bool TryParseFaceName(string name, out FaceName face)
    {
        switch (name)
        {
            case "down": face = FaceName.Down; return true;
            case "up": face = FaceName.Up; return true;
            case "north": face = FaceName.North; return true;
            case "south": face = FaceName.South; return true;
            case "west": face = FaceName.West; return true;
            case "east": face = FaceName.East; return true;
            default: face = FaceName.Down; return false;
        }
    }

    private static Vector3 ReadVector3(JsonElement element)
    {
        JsonElement.ArrayEnumerator e = element.EnumerateArray();
        e.MoveNext();
        float x = e.Current.GetSingle();
        e.MoveNext();
        float y = e.Current.GetSingle();
        e.MoveNext();
        float z = e.Current.GetSingle();
        return new Vector3(x, y, z);
    }

    private static Vector4 ReadVector4(JsonElement element)
    {
        JsonElement.ArrayEnumerator e = element.EnumerateArray();
        e.MoveNext();
        float x = e.Current.GetSingle();
        e.MoveNext();
        float y = e.Current.GetSingle();
        e.MoveNext();
        float z = e.Current.GetSingle();
        e.MoveNext();
        float w = e.Current.GetSingle();
        return new Vector4(x, y, z, w);
    }

    // "block/stone" → ("minecraft", "block/stone")；"minecraft:stone" 原样拆。
    private static (string Ns, string Path) SplitId(string raw)
    {
        int colon = raw.IndexOf(':');
        return colon < 0 ? ("minecraft", raw) : (raw[..colon], raw[(colon + 1)..]);
    }

    private static string NormalizeId(string raw, string kind)
    {
        (string ns, string path) = SplitId(raw);
        // 模型引用有时带 "models/" 前缀（少见但合法），归一化掉，路径统一在调用处拼。
        if (kind == "models" && path.StartsWith("models/", StringComparison.Ordinal))
        {
            path = path["models/".Length..];
        }

        return $"{ns}:{path}";
    }

    private static string NormalizeSprite(string raw)
    {
        if (raw.Length == 0)
        {
            return "";
        }

        (string ns, string path) = SplitId(raw);
        return $"{ns}:{path}";
    }
}
