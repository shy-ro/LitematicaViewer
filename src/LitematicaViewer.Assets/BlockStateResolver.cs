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
    // 投影文件 id（旧版本）→ 资产 id（新版本）的改名桥。键值都是 blockstates/ 下的名字。
    private static readonly Dictionary<string, string> BlockstateAliases = new(StringComparer.Ordinal)
    {
        ["chain"] = "iron_chain"
    };

    private readonly Dictionary<string, ResolvedBlockModel> _models = new(StringComparer.Ordinal);
    private readonly PackStack _packs;
    private readonly Dictionary<string, ModelSource?> _sources = new(StringComparer.Ordinal);
    private int _missCount;

    private int _resolveCount;

    public BlockStateResolver(PackStack packs)
    {
        _packs = packs;
    }

    public string Stats => $"resolveCount={_resolveCount} missCount={_missCount}";

    // blockId 形如 "minecraft:stone" 或 "minecraft:oak_stairs[facing=east,half=bottom]"。
    public ResolvedBlockState Resolve(string blockId)
    {
        var bracket = blockId.IndexOf('[');
        var name = bracket < 0 ? blockId : blockId[..bracket];
        var properties = ImmutableDictionary<string, string>.Empty;
        if (bracket >= 0) properties = ParseProperties(blockId[(bracket + 1)..blockId.IndexOf(']')]);

        var (ns, path) = SplitId(name);
        var statePath = $"assets/{ns}/blockstates/{path}.json";
        var found = _packs.TryRead(statePath, out var stateBytes);
        if (!found && ns == "minecraft"
                   && BlockstateAliases.TryGetValue(path, out var aliased))
        {
            // 投影文件按旧版本 id 写，资产按新版本存：26.x 把 chain 改名成了 iron_chain，
            // 老 id 直接 miss 的方块整个消失（模型一个面都没有），比缺贴图难看得多。
            // 表只收已实测改名的项；别名的 blockstate 模型同构，直接当原名解析。
            var aliasPath = $"assets/{ns}/blockstates/{aliased}.json";
            if (_packs.TryRead(aliasPath, out var aliasBytes))
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

        using var doc = JsonDocument.Parse(stateBytes);
        var root = doc.RootElement;

        List<ResolvedVariant> variants = [];
        if (root.TryGetProperty("variants", out var variantsElement))
            ResolveVariants(variantsElement, properties, name, variants);
        else if (root.TryGetProperty("multipart", out var multipartElement))
            ResolveMultipart(multipartElement, properties, name, variants);
        else
            Debug.WriteLine($"[ASSETS][resolve] blockstate 既无 variants 也无 multipart {statePath}");

        if (variants.Count == 0) _missCount++;

        _resolveCount++;
        return new ResolvedBlockState(blockId, variants, root.TryGetProperty("multipart", out _));
    }

    // ---------- blockstate 层 ----------

    private void ResolveVariants(JsonElement variants, ImmutableDictionary<string, string> properties, string blockName,
        List<ResolvedVariant> output)
    {
        foreach (var entry in variants.EnumerateObject())
        {
            // variants 的键是 "k=v,k=v"（可空）。缺某属性 = 通配；命中即收，MC 同一状态
            // 不会匹配两个键，多收是我们解析器的错，交给断言盯着。
            if (!Matches(entry.Name, properties)) continue;

            switch (entry.Value.ValueKind)
            {
                case JsonValueKind.Object:
                    AddVariant(entry.Value, blockName, output);
                    break;
                case JsonValueKind.Array:
                    foreach (var item in entry.Value.EnumerateArray()) AddVariant(item, blockName, output);
                    break;
                default:
                    Debug.WriteLine($"[ASSETS][resolve] variants 值类型异常 kind={entry.Value.ValueKind} key={entry.Name}");
                    break;
            }
        }
    }

    private void ResolveMultipart(JsonElement multipart, ImmutableDictionary<string, string> properties,
        string blockName, List<ResolvedVariant> output)
    {
        foreach (var part in multipart.EnumerateArray())
        {
            if (part.TryGetProperty("when", out var when) && !WhenMatches(when, properties)) continue;

            if (part.TryGetProperty("apply", out var apply))
            {
                if (apply.ValueKind == JsonValueKind.Object)
                    AddVariant(apply, blockName, output);
                else if (apply.ValueKind == JsonValueKind.Array)
                    foreach (var item in apply.EnumerateArray())
                        AddVariant(item, blockName, output);
            }
        }
    }

    private void AddVariant(JsonElement variant, string blockName, List<ResolvedVariant> output)
    {
        if (!variant.TryGetProperty("model", out var modelElement))
        {
            Debug.WriteLine("[ASSETS][resolve] variant 缺 model 字段，跳过");
            return;
        }

        var x = variant.TryGetProperty("x", out var xElement) ? xElement.GetSingle() : 0f;
        var y = variant.TryGetProperty("y", out var yElement) ? yElement.GetSingle() : 0f;
        var modelId = NormalizeId(modelElement.GetString()!, "models");
        var model = LoadModel(modelId);

        // builtin/entity（方块实体渲染的方块）与流体模型的 elements 是空的，
        // 原样返回就是「整块消失」；按方块名换一个占位几何再出去。
        if (FallbackModels.TryGet(blockName, modelId, model, out var fallback)) model = fallback;

        output.Add(new ResolvedVariant(modelId, model, x, y));
    }

    private static ImmutableDictionary<string, string> ParseProperties(string inner)
    {
        return inner.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .Aggregate(
                ImmutableDictionary<string, string>.Empty,
                (acc, parts) => acc.SetItem(parts[0], parts[1]));
    }

    private static bool Matches(string key, ImmutableDictionary<string, string> properties)
    {
        if (key.Length == 0) return true;

        foreach (var pair in key.Split(','))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2 || !properties.TryGetValue(parts[0], out var value) || value != parts[1])
                return false;
        }

        return true;
    }

    private static bool WhenMatches(JsonElement when, ImmutableDictionary<string, string> properties)
    {
        if (when.TryGetProperty("OR", out var orElement))
            return orElement.EnumerateArray().Any(item => WhenMatches(item, properties));

        if (when.TryGetProperty("AND", out var andElement))
            return andElement.EnumerateArray().All(item => WhenMatches(item, properties));

        foreach (var entry in when.EnumerateObject())
        {
            if (!properties.TryGetValue(entry.Name, out var value)) return false;

            var matched = entry.Value.ValueKind == JsonValueKind.Array
                ? entry.Value.EnumerateArray().Any(v => v.ValueKind == JsonValueKind.String && v.GetString() == value)
                : entry.Value.ValueKind == JsonValueKind.String && entry.Value.GetString() == value;
            if (!matched) return false;
        }

        return true;
    }

    private ModelSource? LoadSource(string modelId)
    {
        if (_sources.TryGetValue(modelId, out var cached)) return cached;

        var (ns, path) = SplitId(modelId);
        var modelPath = $"assets/{ns}/models/{path}.json";

        ModelSource? source;
        if (!_packs.TryRead(modelPath, out var bytes))
        {
            Debug.WriteLine($"[ASSETS][resolve] 找不到 model {modelPath}");
            source = null;
        }
        else
        {
            using var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;

            var parent = root.TryGetProperty("parent", out var parentElement)
                ? NormalizeId(parentElement.GetString()!, "models")
                : null;

            Dictionary<string, string> textures = [];
            if (root.TryGetProperty("textures", out var texturesElement))
                foreach (var entry in texturesElement.EnumerateObject())
                    if (entry.Value.ValueKind == JsonValueKind.String)
                        textures[entry.Name] = entry.Value.GetString()!;
                    else if (entry.Value.ValueKind == JsonValueKind.Object &&
                             entry.Value.TryGetProperty("sprite", out var spriteElement) &&
                             spriteElement.ValueKind == JsonValueKind.String)
                        // 26.3 的新写法：值可以是 {"sprite": "...", "force_translucent": ...}
                        // 这样的对象（原版 glass 就在用）。贴图表里只关心 sprite 一项；
                        // 对象值静默丢弃的话，引用它的一条链全断，画面上是一块品红。
                        textures[entry.Name] = spriteElement.GetString()!;

            JsonElement? elements = root.TryGetProperty("elements", out var elementsElement)
                ? elementsElement.Clone()
                : null;

            source = new ModelSource(parent, textures, elements);
        }

        _sources[modelId] = source;
        return source;
    }

    private ResolvedBlockModel LoadModel(string modelId)
    {
        if (_models.TryGetValue(modelId, out var cached)) return cached;

        // 父链：合并贴图（子覆盖父），elements 取链上最近的一份。
        List<ModelSource> chain = [];
        var cursor = modelId;
        while (cursor is not null && chain.Count < 16)
        {
            var source = LoadSource(cursor);
            if (source is null) break;

            chain.Add(source);
            cursor = source.Parent;
        }

        Dictionary<string, string> textures = new(StringComparer.Ordinal);
        foreach (var source in chain.AsEnumerable().Reverse())
        foreach (var (key, value) in source.Textures)
            textures[key] = value;

        var elementsElement = chain.FirstOrDefault(s => s.Elements is not null)?.Elements;
        var elements = elementsElement is null ? [] : ParseElements(modelId, elementsElement.Value, textures);

        ResolvedBlockModel model = new(modelId, elements);
        _models[modelId] = model;
        return model;
    }

    private List<ModelElement> ParseElements(string modelId, JsonElement elements, Dictionary<string, string> textures)
    {
        List<ModelElement> result = [];
        foreach (var element in elements.EnumerateArray())
        {
            var from = element.TryGetProperty("from", out var fromElement)
                ? ReadVector3(fromElement)
                : new Vector3(0, 0, 0);
            var to = element.TryGetProperty("to", out var toElement) ? ReadVector3(toElement) : new Vector3(16, 16, 16);

            ElementRotation? rotation = null;
            if (element.TryGetProperty("rotation", out var rotationElement))
            {
                var axis = rotationElement.TryGetProperty("axis", out var axisElement)
                    ? axisElement.GetString() switch
                    {
                        "x" => 0,
                        "y" => 1,
                        "z" => 2,
                        _ => -1
                    }
                    : -1;
                var angle = rotationElement.TryGetProperty("angle", out var angleElement)
                    ? angleElement.GetSingle()
                    : 0f;
                var origin = rotationElement.TryGetProperty("origin", out var originElement)
                    ? ReadVector3(originElement)
                    : new Vector3(8, 8, 8);
                rotation = new ElementRotation(axis, angle, origin);
            }

            List<ElementFace> faces = [];
            if (element.TryGetProperty("faces", out var facesElement))
                foreach (var entry in facesElement.EnumerateObject())
                {
                    if (entry.Value.ValueKind != JsonValueKind.Object ||
                        !TryParseFaceName(entry.Name, out var face))
                        continue;

                    var faceElement = entry.Value;
                    var textureRef = faceElement.TryGetProperty("texture", out var textureElement)
                        ? textureElement.GetString()
                        : null;
                    if (textureRef is null) continue;

                    // #引用在合并后的贴图表里解；解不开的（模型写错或 mod 资产残缺）
                    // 记日志给空 sprite，网格阶段把它当「这面没有贴图」处理，不炸整个模型。
                    string? sprite;
                    if (!textureRef.StartsWith('#'))
                    {
                        // 不带 # 的值先查贴图表：26.3 的 heavy_core 面引用写的是裸 "all"，
                        // 意图是表里的 "all" 键；查不到才当直接贴图路径（mod 资产常见）。
                        sprite = textures.TryGetValue(textureRef, out var tabled) ? tabled : textureRef;
                        var hops = 0;
                        while (sprite is not null && sprite.StartsWith('#') &&
                               textures.TryGetValue(sprite[1..], out var next) && hops++ < 8) sprite = next;
                    }
                    else
                    {
                        // 表的键不带 #（"#down" 的键是 "down"），值才可能是下一层引用
                        // （cube_all 把 down/up/... 全指向 #all），迭代解到头。
                        var key = textureRef[1..];
                        sprite = textures.TryGetValue(key, out var value) ? value : null;
                        var hops = 0;
                        while (sprite is not null && sprite.StartsWith('#') &&
                               textures.TryGetValue(sprite[1..], out var next) && hops++ < 8) sprite = next;
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

                    // face 不写 uv 时不是全贴图：MC 按元素盒在面上的投影生成
                    // （BlockElement.uvsByFace，侧面 v 还要按 16-y 镜像）。cake 系列
                    // 的所有 face 都不写 uv，元素又是 [1,0,1]→[15,8,15] 不满格——
                    // 默认 0..16 会让侧面上半采到贴图的透明段被 cutout 丢光、
                    // 顶面整体错位放大。cake_side.png 的白霜就烙在 v 8..11，
                    // 贴图本身按「投影取下半」设计，公式与外观互相印证。
                    var uv = faceElement.TryGetProperty("uv", out var uvElement)
                        ? ReadVector4(uvElement)
                        : ProjectedUv(face, from, to);

                    var cullface = faceElement.TryGetProperty("cullface", out var cullElement)
                        ? cullElement.GetString()
                        : null;

                    var tintIndex = faceElement.TryGetProperty("tintindex", out var tintElement)
                        ? tintElement.GetInt32()
                        : -1;

                    var uvRotation = faceElement.TryGetProperty("rotation", out var faceRotationElement)
                        ? faceRotationElement.GetInt32()
                        : 0;

                    faces.Add(new ElementFace(face, uv, NormalizeSprite(sprite), cullface, tintIndex, uvRotation));
                }

            result.Add(new ModelElement(from, to, rotation, faces));
        }

        return result;
    }

    // 照抄 BlockElement.uvsByFace（纹素坐标 0..16）：从面外侧看，纹理左上
    // 对齐面的左上——north/east/down 的 u/v 因此带 16-x 镜像。侧面 v 取
    // 16-to.y..16-from.y 是「元素在方块里的绝对高度段对应贴图同段」的语义，
    // 不是把贴图缩放进面：蛋糕元素高 0..8，贴图下半段才有内容。
    private static Vector4 ProjectedUv(FaceName face, Vector3 from, Vector3 to)
    {
        return face switch
        {
            FaceName.Down => new Vector4(from.X, 16 - to.Z, to.X, 16 - from.Z),
            FaceName.Up => new Vector4(from.X, from.Z, to.X, to.Z),
            FaceName.North => new Vector4(16 - to.X, 16 - to.Y, 16 - from.X, 16 - from.Y),
            FaceName.South => new Vector4(from.X, 16 - to.Y, to.X, 16 - from.Y),
            FaceName.West => new Vector4(from.Z, 16 - to.Y, to.Z, 16 - from.Y),
            FaceName.East => new Vector4(16 - to.Z, 16 - to.Y, 16 - from.Z, 16 - from.Y),
            _ => new Vector4(0, 0, 16, 16)
        };
    }

    private static bool TryParseFaceName(string name, out FaceName face)
    {
        switch (name)
        {
            case "down":
                face = FaceName.Down;
                return true;
            case "up":
                face = FaceName.Up;
                return true;
            case "north":
                face = FaceName.North;
                return true;
            case "south":
                face = FaceName.South;
                return true;
            case "west":
                face = FaceName.West;
                return true;
            case "east":
                face = FaceName.East;
                return true;
            default:
                face = FaceName.Down;
                return false;
        }
    }

    private static Vector3 ReadVector3(JsonElement element)
    {
        var e = element.EnumerateArray();
        e.MoveNext();
        var x = e.Current.GetSingle();
        e.MoveNext();
        var y = e.Current.GetSingle();
        e.MoveNext();
        var z = e.Current.GetSingle();
        return new Vector3(x, y, z);
    }

    private static Vector4 ReadVector4(JsonElement element)
    {
        var e = element.EnumerateArray();
        e.MoveNext();
        var x = e.Current.GetSingle();
        e.MoveNext();
        var y = e.Current.GetSingle();
        e.MoveNext();
        var z = e.Current.GetSingle();
        e.MoveNext();
        var w = e.Current.GetSingle();
        return new Vector4(x, y, z, w);
    }

    // "block/stone" → ("minecraft", "block/stone")；"minecraft:stone" 原样拆。
    private static (string Ns, string Path) SplitId(string raw)
    {
        var colon = raw.IndexOf(':');
        return colon < 0 ? ("minecraft", raw) : (raw[..colon], raw[(colon + 1)..]);
    }

    private static string NormalizeId(string raw, string kind)
    {
        var (ns, path) = SplitId(raw);
        // 模型引用有时带 "models/" 前缀（少见但合法），归一化掉，路径统一在调用处拼。
        if (kind == "models" && path.StartsWith("models/", StringComparison.Ordinal)) path = path["models/".Length..];

        return $"{ns}:{path}";
    }

    private static string NormalizeSprite(string raw)
    {
        if (raw.Length == 0) return "";

        var (ns, path) = SplitId(raw);
        return $"{ns}:{path}";
    }

    // ---------- model 层 ----------

    private sealed record ModelSource(string? Parent, Dictionary<string, string> Textures, JsonElement? Elements);
}
