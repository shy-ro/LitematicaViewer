using System.Collections.Immutable;
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
    public static bool TryGet(string blockName, string modelId, ResolvedBlockModel parsed,
        ImmutableDictionary<string, string> properties, out ResolvedBlockModel fallback)
    {
        if (modelId == "minecraft:block/water")
        {
            // 水面比方块低 2/16；不染时贴图本身就是灰白，槽号交给方块 id 归类。
            fallback = Box(modelId, new Vector3(0, 0, 0), new Vector3(16, 14, 16), "minecraft:block/water_still", true);
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

        var id = blockName.StartsWith("minecraft:", StringComparison.Ordinal)
            ? blockName["minecraft:".Length..]
            : blockName;
        var sprite = SpriteFor(id);
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
            // 实体贴图是 64x64 展开图：整图糊上盒子会采到左上大片空白（cutout 后
            // 只剩碎片）。按原版 ChestModel 的盖+身两部分盒重建；朝向与左右半箱
            // 直接取自 blockstate 属性（箱子的 facing/type 是普通方块属性，
            // 不需要方块实体数据）。
            fallback = Chest(modelId, sprite, properties);
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
        if (id.Contains("banner", StringComparison.Ordinal)) return "minecraft:entity/banner/base";

        if (id.Contains("shulker", StringComparison.Ordinal))
        {
            // 1.20+ 每种颜色的潜影盒都有一张同名 block 贴图（particle 用），
            // block/shulker_box.png、block/white_shulker_box.png……直接按 id 取。
            // 别用 entity/shulker/shulker.png：那是 64x64 实体展开图，左上 16x16
            // 是透明区，占位盒 uv 0..16 正好采进去，cutout 把面全 discard 成碎片框。
            return $"minecraft:block/{id}";
        }

        if (id.Contains("chest", StringComparison.Ordinal))
            return id switch
            {
                var s when s.Contains("ender", StringComparison.Ordinal) => "minecraft:entity/chest/ender",
                var s when s.Contains("trapped", StringComparison.Ordinal) => "minecraft:entity/chest/trapped",
                var s when s.Contains("oxidized", StringComparison.Ordinal) => "minecraft:entity/chest/copper_oxidized",
                var s when s.Contains("weathered", StringComparison.Ordinal) =>
                    "minecraft:entity/chest/copper_weathered",
                var s when s.Contains("exposed", StringComparison.Ordinal) => "minecraft:entity/chest/copper_exposed",
                var s when s.Contains("copper", StringComparison.Ordinal) => "minecraft:entity/chest/copper",
                _ => "minecraft:entity/chest/normal"
            };

        if (id.Contains("creeper", StringComparison.Ordinal)) return "minecraft:entity/creeper/creeper";

        if (id.Contains("dragon", StringComparison.Ordinal)) return "minecraft:entity/enderdragon/dragon";

        if (id.Contains("piglin", StringComparison.Ordinal)) return "minecraft:entity/piglin/piglin";

        if (id.Contains("skeleton", StringComparison.Ordinal))
            return id.Contains("wither", StringComparison.Ordinal)
                ? "minecraft:entity/skeleton/wither_skeleton"
                : "minecraft:entity/skeleton/skeleton";

        if (id.Contains("zombie", StringComparison.Ordinal)) return "minecraft:entity/zombie/zombie";

        if (id.Contains("player", StringComparison.Ordinal)) return "minecraft:entity/player/wide/steve";

        if (id.Contains("conduit", StringComparison.Ordinal)) return "minecraft:block/conduit";

        if (id.Contains("decorated_pot", StringComparison.Ordinal))
            return "minecraft:entity/decorated_pot/decorated_pot_side";

        // 戈勒姆雕像、moving_piston 等没有专属贴图：用 id 当 sprite 名去缺，
        // 走 missingno 棋盘——至少形状在，缺图名单里也报得出名字。
        if (id.Contains("golem_statue", StringComparison.Ordinal) ||
            id.Contains("moving_piston", StringComparison.Ordinal)) return $"minecraft:block/{id}";

        return "";
    }

    // 六面同贴图的盒子：uv 全 0..16、无 cullface（回退体不参与面剔除，
    // 宁可多画也不跟邻居的剔除语义再纠缠一次）、tint 只给水用。
    private static ResolvedBlockModel Box(string modelId, Vector3 from, Vector3 to, string sprite,
        bool waterTint = false)
    {
        List<ElementFace> faces = [];
        foreach (var face in new[]
                     { FaceName.Down, FaceName.Up, FaceName.North, FaceName.South, FaceName.West, FaceName.East })
            faces.Add(new ElementFace(face, new Vector4(0, 0, 16, 16), sprite, null, waterTint ? 0 : -1, 0));

        return new ResolvedBlockModel(modelId, [new ModelElement(from, to, null, faces)]);
    }

    // 64x64 实体贴图的 uv 换算：1 uv 单位（0..16 的面坐标）= 贴图 4px。
    private const float EntitySheetUnitsPerPixel = 4f;

    // 64x64 实体贴图上的箱子。几何与 uv 区域按原版 ChestModel 抄：盖 texOffs(0,0)
    // 14x5、身 texOffs(0,19) 14x10，展开图里 up/down 同排在 y0..14（盖）与
    // y19..33（身），侧面横条在 y14..19 / y33..43。uv 单位 = 贴图 4px。
    // 正面取 facing 属性（原版锁舌在模型 +Z，即 canonical 正面朝南）；
    // type=left/right 是双箱半体：各 15 宽、外侧缩 1px，对接成 30px 连体。
    // 贴图继续用整张（_left/_right 半张的条带布局不同，左半张缺 0..14 段，
    // 直接搬会采进透明区被 cutout 吃出洞；整张的条带四面齐全）。
    private static ResolvedBlockModel Chest(string modelId, string spriteBase,
        ImmutableDictionary<string, string> properties)
    {
        properties.TryGetValue("facing", out var facingValue);
        var facing = facingValue ?? "north";
        properties.TryGetValue("type", out var typeValue);
        var type = typeValue ?? "single";
        var isLeft = type == "left";
        var isRight = type == "right";
        var wide = isLeft || isRight;

        // canonical：正面朝南（+Z）。南=正面条带，北=背面，东=半箱外侧端面
        // （canonical 里 chest 朝南时其左手边是东），西=接缝侧。
        (FaceName face, float x0, float y0, float x1, float y1)[] lidFaces =
        [
            (FaceName.Up, 14, 0, 28, 14),
            (FaceName.Down, 28, 0, 42, 14),
            (FaceName.South, 42, 14, 56, 19),
            (FaceName.North, 14, 14, 28, 19),
            (FaceName.East, 0, 14, 14, 19),
            (FaceName.West, 28, 14, 42, 19),
        ];
        (FaceName face, float x0, float y0, float x1, float y1)[] bodyFaces =
        [
            (FaceName.Up, 14, 19, 28, 33),
            (FaceName.Down, 28, 19, 42, 33),
            (FaceName.South, 42, 33, 56, 43),
            (FaceName.North, 14, 33, 28, 43),
            (FaceName.East, 0, 33, 14, 43),
            (FaceName.West, 28, 33, 42, 43),
        ];

        // canonical 盒：进深 z 1..15（正面在 z=15）；宽度 single 1..15，
        // 半箱 15 宽——left 在东（+X）侧、外缘缩 1px（x 0..15），right 镜像（x 1..16）。
        var x0 = isLeft ? 0 : 1;
        var x1 = isRight ? 16 : 15;

        List<ModelElement> elements =
        [
            RotatedPart(new Vector3(x0, 9, 1), new Vector3(x1, 14, 15), lidFaces, spriteBase, facing),
            RotatedPart(new Vector3(x0, 0, 1), new Vector3(x1, 9, 15), bodyFaces, spriteBase, facing),
        ];

        if (!wide)
        {
            // 锁舌：贴图左上 2x4，canonical 挂在南面（z 15..16）居中。
            List<ElementFace> latchFaces = [];
            foreach (var face in new[]
                         { FaceName.Down, FaceName.Up, FaceName.North, FaceName.South, FaceName.West, FaceName.East })
            {
                var uv = face == FaceName.South
                    ? new Vector4(0, 0, 2 / EntitySheetUnitsPerPixel, 4 / EntitySheetUnitsPerPixel)
                    : new Vector4(0, 0, 0.5f, 1f);
                latchFaces.Add(new ElementFace(MapFace(face, facing), uv, spriteBase, null, -1, 0));
            }

            elements.Add(new ModelElement(
                RotateXz(new Vector3(7, 7, 15), facing), RotateXz(new Vector3(9, 11, 16), facing), null, latchFaces));
        }

        return new ResolvedBlockModel(modelId, elements);
    }

    // 1.20.1 就有左右半张的箱子贴图族；其余（铜箱等新客）保守用整张。
    private static bool SpriteHasHalves(string spriteBase) =>
        spriteBase.Contains("normal", StringComparison.Ordinal) ||
        spriteBase.Contains("trapped", StringComparison.Ordinal) ||
        spriteBase.Contains("ender", StringComparison.Ordinal);

    // canonical（正面朝南）→ 实际面名：绕 Y 把 +Z 转到 facing 方向。
    // Up/Down 不随水平旋转；六面必须显式全覆盖，兜底会把顶/底面也拧到南面
    // （顶面开天窗、黑底区域贴到侧面）。
    private static FaceName MapFace(FaceName canonical, string facing) => facing switch
    {
        "east" => canonical switch
        {
            FaceName.South => FaceName.East,
            FaceName.East => FaceName.North,
            FaceName.North => FaceName.West,
            FaceName.West => FaceName.South,
            _ => canonical,
        },
        "west" => canonical switch
        {
            FaceName.South => FaceName.West,
            FaceName.West => FaceName.North,
            FaceName.North => FaceName.East,
            FaceName.East => FaceName.South,
            _ => canonical,
        },
        "north" => canonical switch
        {
            FaceName.South => FaceName.North,
            FaceName.North => FaceName.South,
            FaceName.East => FaceName.West,
            FaceName.West => FaceName.East,
            _ => canonical,
        },
        _ => canonical,
    };

    // canonical 点位随 MapFace 同角度转（绕格中心；y 不动）。
    private static Vector3 RotateXz(Vector3 v, string facing) => facing switch
    {
        "east" => new Vector3(v.Z, v.Y, 16 - v.X),
        "west" => new Vector3(16 - v.Z, v.Y, v.X),
        "north" => new Vector3(16 - v.X, v.Y, 16 - v.Z),
        _ => v,
    };

    // 建 canonical 盒后把面名与几何一起转到实际朝向。旋转会把某轴的 from/to
    // 颠倒（16-x 递减），网格层不做归一化，负扩展会建出翻面墙板——这里收 min/max。
    private static ModelElement RotatedPart(Vector3 from, Vector3 to,
        (FaceName face, float x0, float y0, float x1, float y1)[] regions, string sprite, string facing)
    {
        List<ElementFace> faces = [];
        foreach (var (face, x0, y0, x1, y1) in regions)
        {
            var uv = new Vector4(x0 / EntitySheetUnitsPerPixel, y0 / EntitySheetUnitsPerPixel,
                x1 / EntitySheetUnitsPerPixel, y1 / EntitySheetUnitsPerPixel);
            faces.Add(new ElementFace(MapFace(face, facing), uv, sprite, null, -1, 0));
        }

        var a = RotateXz(from, facing);
        var b = RotateXz(to, facing);
        return new ModelElement(Vector3.Min(a, b), Vector3.Max(a, b), null, faces);
    }
}
