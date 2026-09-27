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

    // 展开图区域之间紧挨着完全不同的内容（盖顶旁边就是暗色盖底/透明区），
    // uv 压在区域边界上线性采样会混进 50% 邻居：双箱接缝整列发暗、邻透明区
    // 的边被 cutout 吃出细洞。所有区域四边内缩半像素，采 texel 中心。
    private const float RegionInsetPx = 0.5f;

    // 64x64 实体贴图上的箱子。展开图区域逐像素比对过（normal/_left/_right 三张
    // 互相印证）：第一方块是暗的内部（盖底/箱内地板），第二方块才是亮的顶面，
    // 条带面序是 [西][南=正面][东][北]——半张图里 left 缺西段、right 缺东段，
    // 正是各自的接缝面留白，由此反推出条带里东西的归属。原版反编译确认
    // ChestType.LEFT 的搭档在 facing 顺时针方向，canonical（正面朝南）下
    // left 是东侧半块、接缝在西缘。
    // 双箱贴 _left/_right 半张展开图（15 宽盒的标准 unwrap），两个半张拼起来
    // 才是连续的大盖面；贴整张会让每个半块都带完整边框，看着像两个单箱。
    // 半张缺失（ender 等只有整张的）退回整张：盒宽仍 15、按 w=14 采样，轻微
    // 拉伸好过采进透明区。
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
        var useHalves = wide && SpriteHasHalves(spriteBase);
        var sprite = useHalves ? spriteBase + (isLeft ? "_left" : "_right") : spriteBase;

        // canonical 盒：进深 z 1..15（正面在 z=15）；single 宽 1..15；半箱 15 宽，
        // left 对接缝（西缘）齐平、外缘缩 1px（x 0..15），right 镜像（x 1..16）。
        var x0 = isLeft ? 0 : 1;
        var x1 = isRight ? 16 : 15;

        (FaceName face, float px0, float py0, float px1, float py1)[] lidRegions;
        (FaceName face, float px0, float py0, float px1, float py1)[] bodyRegions;
        if (useHalves)
        {
            lidRegions =
            [
                (FaceName.Up, 29, 0, 44, 14),
                (FaceName.Down, 14, 0, 29, 14),
                (FaceName.South, 14, 14, 29, 19),
                (FaceName.North, 43, 14, 58, 19),
                (FaceName.East, 29, 14, 43, 19),
                (FaceName.West, 0, 14, 14, 19),
            ];
            bodyRegions =
            [
                (FaceName.Up, 29, 19, 44, 33),
                (FaceName.Down, 14, 19, 29, 33),
                (FaceName.South, 14, 33, 29, 43),
                (FaceName.North, 43, 33, 58, 43),
                (FaceName.East, 29, 33, 43, 43),
                (FaceName.West, 0, 33, 14, 43),
            ];
        }
        else
        {
            lidRegions =
            [
                (FaceName.Up, 28, 0, 42, 14),
                (FaceName.Down, 14, 0, 28, 14),
                (FaceName.South, 14, 14, 28, 19),
                (FaceName.North, 42, 14, 56, 19),
                (FaceName.East, 28, 14, 42, 19),
                (FaceName.West, 0, 14, 14, 19),
            ];
            bodyRegions =
            [
                (FaceName.Up, 28, 19, 42, 33),
                (FaceName.Down, 14, 19, 28, 33),
                (FaceName.South, 14, 33, 28, 43),
                (FaceName.North, 42, 33, 56, 43),
                (FaceName.East, 28, 33, 42, 43),
                (FaceName.West, 0, 33, 14, 43),
            ];
        }

        List<ModelElement> elements =
        [
            RotatedPart(new Vector3(x0, 9, 1), new Vector3(x1, 14, 15), lidRegions, sprite, facing),
            RotatedPart(new Vector3(x0, 0, 1), new Vector3(x1, 9, 15), bodyRegions, sprite, facing),
        ];

        if (!wide)
        {
            // 锁舌盒（2x4x1，texOffs(0,0)）的标准 unwrap 区域，正面（南）是唯一
            // 2px 宽的 w 段，锁舌像素就画在那里。
            List<ElementFace> latchFaces = [];
            foreach (var face in new[]
                         { FaceName.Down, FaceName.Up, FaceName.North, FaceName.South, FaceName.West, FaceName.East })
            {
                var uv = face switch
                {
                    FaceName.South => new Vector4(0.25f, 0.25f, 0.75f, 1.25f),
                    FaceName.North => new Vector4(1f, 0.25f, 1.25f, 1.25f),
                    FaceName.West => new Vector4(0f, 0.25f, 0.25f, 1.25f),
                    FaceName.East => new Vector4(0.75f, 0.25f, 1f, 1.25f),
                    FaceName.Down => new Vector4(0.25f, 0f, 0.75f, 0.25f),
                    _ => new Vector4(0.75f, 0f, 1.25f, 0.25f),
                };
                var inset = RegionInsetPx / EntitySheetUnitsPerPixel;
                uv = new Vector4(uv.X + inset, uv.Y + inset, uv.Z - inset, uv.W - inset);
                latchFaces.Add(new ElementFace(MapFace(face, facing), uv, spriteBase, null, -1, 0));
            }

            elements.Add(new ModelElement(
                RotateXz(new Vector3(7, 7, 15), facing), RotateXz(new Vector3(9, 11, 16), facing), null, latchFaces));
        }

        return new ResolvedBlockModel(modelId, elements);
    }

    // 1.20.1 里只有 normal/trapped 两族有 _left/_right 半张；ender 没有半张
    // （末影箱本就不能组双箱）。
    private static bool SpriteHasHalves(string spriteBase) =>
        spriteBase.Contains("normal", StringComparison.Ordinal) ||
        spriteBase.Contains("trapped", StringComparison.Ordinal);

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
            var uv = new Vector4((x0 + RegionInsetPx) / EntitySheetUnitsPerPixel,
                (y0 + RegionInsetPx) / EntitySheetUnitsPerPixel,
                (x1 - RegionInsetPx) / EntitySheetUnitsPerPixel,
                (y1 - RegionInsetPx) / EntitySheetUnitsPerPixel);
            // 顶/底面要补一个随朝向的 uv 旋转：水平旋转是刚体，贴图该跟着盒子转，
            // 但网格层给顶/底面定 uv 轴用的是世界轴，预旋转几何后区域朝向对不上
            // ——顶面贴图被转置+镜像，双箱接缝正好压上外缘深色边框列，看着像
            // 中间有条黑缝。侧面（r=观察系右推得）恰好自洽，不用动。
            faces.Add(new ElementFace(MapFace(face, facing), uv, sprite, null, -1, TopUvRotation(face, facing)));
        }

        var a = RotateXz(from, facing);
        var b = RotateXz(to, facing);
        return new ModelElement(Vector3.Min(a, b), Vector3.Max(a, b), null, faces);
    }

    // 逐朝向展开 R⁻¹(世界uv轴) 与区域轴（Up: tu=+X/tv=+Z；Down: tu=+X/tv=-Z）
    // 的对应关系得到的角点轮转量，语义与网格层 face rotation 一致
    // （90°=uvA←uvD）。
    private static int TopUvRotation(FaceName canonical, string facing) => (canonical, facing) switch
    {
        (FaceName.Up, "east") => 270,
        (FaceName.Up, "west") => 90,
        (FaceName.Down, "east") => 90,
        (FaceName.Down, "west") => 270,
        (_, "north") => 180,
        _ => 0,
    };
}
