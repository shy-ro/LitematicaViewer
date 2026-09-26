using System.Numerics;
using LitematicaViewer.Assets.Model;

namespace LitematicaViewer.Assets;

// builtin/entity 与流体的占位模型。MC 把箱子、潜影盒、旗帜、头颅、潮涌核心、
// 饰纹陶罐这些交给方块实体渲染，blockstate 里只有 "builtin/entity"（一个 element
// 都没有）；水与熔岩的模型 elements 也是空的，本体交给流体渲染器。对网格预览器
// 来说这些方块原本整块消失。这里按方块名给一个简化几何 + 包栈里找得到的最贴切
// 贴图（entity/ 目录的贴图与 block/ 同一套读取路径）：形状在、颜色对，够辨认，
// 不追求还原实体渲染的细节。
internal static class FallbackModels
{
    // parsed 是按原 modelId 正常解析出的模型：非 builtin、elements 非空时原样放行。
    public static bool TryGet(string blockName, string modelId, ResolvedBlockModel parsed, out ResolvedBlockModel fallback)
    {
        if (modelId == "minecraft:block/water")
        {
            // 水面比方块低 2/16；不染时贴图本身就是灰白，槽号交给方块 id 归类。
            fallback = Box(modelId, new Vector3(0, 0, 0), new Vector3(16, 14, 16), "minecraft:block/water_still", waterTint: true);
            return true;
        }

        if (modelId == "minecraft:block/lava")
        {
            fallback = Box(modelId, new Vector3(0, 0, 0), new Vector3(16, 14, 16), "minecraft:block/lava_still");
            return true;
        }

        // builtin/entity 是老写法；26.3 的方块实体方块改成了正常 model id 但
        // elements 为空（block/chest、block/banner……）。两者都按方块名回退。
        if (modelId != "minecraft:builtin/entity" && parsed.Elements.Count != 0)
        {
            fallback = parsed;
            return false;
        }

        string id = blockName.StartsWith("minecraft:", StringComparison.Ordinal) ? blockName["minecraft:".Length..] : blockName;
        string sprite = SpriteFor(id);
        if (sprite.Length == 0)
        {
            fallback = parsed;
            return false;
        }

        if (id.Contains("banner", StringComparison.Ordinal))
        {
            // 旗是一块竖板：宽度全格、厚度 2/16。
            fallback = Box(modelId, new Vector3(0, 0, 7), new Vector3(16, 16, 9), sprite);
            return true;
        }

        if (id.Contains("head", StringComparison.Ordinal) || id.Contains("skull", StringComparison.Ordinal))
        {
            fallback = Box(modelId, new Vector3(4, 0, 4), new Vector3(12, 8, 12), sprite);
            return true;
        }

        if (id.Contains("conduit", StringComparison.Ordinal))
        {
            fallback = Box(modelId, new Vector3(5, 5, 5), new Vector3(11, 11, 11), sprite);
            return true;
        }

        if (id.Contains("chest", StringComparison.Ordinal))
        {
            // 箱子比方块矮 2/16。
            fallback = Box(modelId, new Vector3(0, 0, 0), new Vector3(16, 14, 16), sprite);
            return true;
        }

        // 潜影盒、饰纹陶罐、戈勒姆雕像这类「接近整格」的，直接整格画。
        fallback = Box(modelId, new Vector3(0, 0, 0), new Vector3(16, 16, 16), sprite);
        return true;
    }

    // 按方块 id 挑一张「画出来认得出这是它」的贴图；挑不出的返回空串（不回退，
    // 保持原状不渲染——一个随机棋盘格的立方体比消失更难排查）。
    private static string SpriteFor(string id)
    {
        if (id.Contains("banner", StringComparison.Ordinal))
        {
            return "minecraft:entity/banner/base";
        }

        if (id.Contains("shulker", StringComparison.Ordinal))
        {
            // entity/shulker/shulker.png 与 shulker_{color}.png；方块 id 是
            // shulker_box 或 {color}_shulker_box，剥掉后缀就是颜色段。
            string color = id == "shulker_box" ? "" : id.Replace("_shulker_box", "", StringComparison.Ordinal);
            return color.Length == 0 ? "minecraft:entity/shulker/shulker" : $"minecraft:entity/shulker/shulker_{color}";
        }

        if (id.Contains("chest", StringComparison.Ordinal))
        {
            return id switch
            {
                var s when s.Contains("ender", StringComparison.Ordinal) => "minecraft:entity/chest/ender",
                var s when s.Contains("trapped", StringComparison.Ordinal) => "minecraft:entity/chest/trapped",
                var s when s.Contains("oxidized", StringComparison.Ordinal) => "minecraft:entity/chest/copper_oxidized",
                var s when s.Contains("weathered", StringComparison.Ordinal) => "minecraft:entity/chest/copper_weathered",
                var s when s.Contains("exposed", StringComparison.Ordinal) => "minecraft:entity/chest/copper_exposed",
                var s when s.Contains("copper", StringComparison.Ordinal) => "minecraft:entity/chest/copper",
                _ => "minecraft:entity/chest/normal",
            };
        }

        if (id.Contains("creeper", StringComparison.Ordinal))
        {
            return "minecraft:entity/creeper/creeper";
        }

        if (id.Contains("dragon", StringComparison.Ordinal))
        {
            return "minecraft:entity/enderdragon/dragon";
        }

        if (id.Contains("piglin", StringComparison.Ordinal))
        {
            return "minecraft:entity/piglin/piglin";
        }

        if (id.Contains("skeleton", StringComparison.Ordinal))
        {
            return id.Contains("wither", StringComparison.Ordinal)
                ? "minecraft:entity/skeleton/wither_skeleton"
                : "minecraft:entity/skeleton/skeleton";
        }

        if (id.Contains("zombie", StringComparison.Ordinal))
        {
            return "minecraft:entity/zombie/zombie";
        }

        if (id.Contains("player", StringComparison.Ordinal))
        {
            return "minecraft:entity/player/wide/steve";
        }

        if (id.Contains("conduit", StringComparison.Ordinal))
        {
            return "minecraft:block/conduit";
        }

        if (id.Contains("decorated_pot", StringComparison.Ordinal))
        {
            return "minecraft:entity/decorated_pot/decorated_pot_side";
        }

        // 戈勒姆雕像、moving_piston 等没有专属贴图：用 id 当 sprite 名去缺，
        // 走 missingno 棋盘——至少形状在，缺图名单里也报得出名字。
        if (id.Contains("golem_statue", StringComparison.Ordinal) || id.Contains("moving_piston", StringComparison.Ordinal))
        {
            return $"minecraft:block/{id}";
        }

        return "";
    }

    // 六面同贴图的盒子：uv 全 0..16、无 cullface（回退体不参与面剔除，
    // 宁可多画也不跟邻居的剔除语义再纠缠一次）、tint 只给水用。
    private static ResolvedBlockModel Box(string modelId, Vector3 from, Vector3 to, string sprite, bool waterTint = false)
    {
        List<ElementFace> faces = [];
        foreach (FaceName face in new[] { FaceName.Down, FaceName.Up, FaceName.North, FaceName.South, FaceName.West, FaceName.East })
        {
            faces.Add(new ElementFace(face, new Vector4(0, 0, 16, 16), sprite, null, waterTint ? 0 : -1, 0));
        }

        return new ResolvedBlockModel(modelId, [new ModelElement(from, to, null, faces)]);
    }
}
