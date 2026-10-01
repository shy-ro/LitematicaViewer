using System.Collections.Immutable;
using System.Numerics;
using LitematicaViewer.Assets.Model;

namespace LitematicaViewer.Assets;

// builtin/entity 与流体的静态投影模型。MC 把箱子、潜影盒、旗帜、头颅、潮涌核心、
// 饰纹陶罐这些交给方块实体渲染，blockstate 里只有 "builtin/entity"（一个 element
// 都没有）；水与熔岩的模型 elements 也是空的，本体交给流体渲染器。对网格预览器
// 来说这些方块原本整块消失。这里为静态预览重建实体模型可见的盒件与展开图；
// 依赖实例 NBT 的文字、图案和实体则由 Meshing 的特殊网格层追加。
internal static class SpecialBlockModels
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
            fallback = Banner(modelId, sprite, id.Contains("wall_", StringComparison.Ordinal));
            return true;
        }

        if (id.Contains("head", StringComparison.Ordinal) || id.Contains("skull", StringComparison.Ordinal))
        {
            fallback = Head(modelId, sprite, id);
            return true;
        }

        if (id.Contains("sign", StringComparison.Ordinal))
        {
            fallback = Sign(modelId, sprite, id, properties);
            return true;
        }

        if (id.EndsWith("_bed", StringComparison.Ordinal))
        {
            fallback = Bed(modelId, sprite, properties);
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

        if (id.Contains("shulker_box", StringComparison.Ordinal))
        {
            fallback = ShulkerBox(modelId, sprite, properties);
            return true;
        }

        if (id.Contains("decorated_pot", StringComparison.Ordinal))
        {
            fallback = DecoratedPot(modelId, sprite);
            return true;
        }

        if (id.Contains("moving_piston", StringComparison.Ordinal))
        {
            fallback = Box(modelId, Vector3.Zero, new Vector3(16), "minecraft:block/piston_side");
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

        if (id.Contains("sign", StringComparison.Ordinal))
        {
            var wood = id.Replace("wall_", "", StringComparison.Ordinal)
                .Replace("_hanging_sign", "", StringComparison.Ordinal)
                .Replace("_sign", "", StringComparison.Ordinal);
            var hanging = id.Contains("hanging_sign", StringComparison.Ordinal) ? "hanging/" : string.Empty;
            return $"minecraft:entity/signs/{hanging}{wood}";
        }

        if (id.EndsWith("_bed", StringComparison.Ordinal))
            return $"minecraft:entity/bed/{id[..^"_bed".Length]}";

        if (id.Contains("shulker", StringComparison.Ordinal))
        {
            var color = id == "shulker_box" ? string.Empty : "_" + id[..^"_shulker_box".Length];
            return $"minecraft:entity/shulker/shulker{color}";
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

        // 老投影里的雕像方块在新版资产中没有同名贴图；用铁块材质保持其材质语义，
        // 不再让整个雕像变成 missingno 棋盘。
        if (id.Contains("golem_statue", StringComparison.Ordinal)) return "minecraft:block/iron_block";
        if (id.Contains("moving_piston", StringComparison.Ordinal)) return "minecraft:block/piston_side";

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

    private static ModelElement BoxElement(Vector3 from, Vector3 to, string sprite)
    {
        List<ElementFace> faces = [];
        foreach (var face in new[]
                     { FaceName.Down, FaceName.Up, FaceName.North, FaceName.South, FaceName.West, FaceName.East })
            faces.Add(new ElementFace(face, new Vector4(0, 0, 16, 16), sprite, null, -1, 0));
        return new ModelElement(from, to, null, faces);
    }

    private static ResolvedBlockModel Banner(string modelId, string sprite, bool wall)
    {
        List<ModelElement> elements = [BoxElement(new Vector3(1, 2, 7.5f), new Vector3(15, 15, 8.5f), sprite)];
        if (!wall)
        {
            elements.Add(BoxElement(new Vector3(7.5f, 0, 7.5f), new Vector3(8.5f, 16, 8.5f), sprite));
            elements.Add(BoxElement(new Vector3(0.5f, 14.5f, 7.25f), new Vector3(15.5f, 15.5f, 8.75f), sprite));
        }

        return new ResolvedBlockModel(modelId, elements);
    }

    // 牌面/支柱必须走 EntityCuboid 逐面展开 uv：整张贴图（uv 0..16）糊到每个面上的话，
    // 64x32 的实体展开图里木板、侧柱、柱子会被拉平重影。几何与朝向取自 SignGeometry，
    // 与 Meshing 的文字层同源。
    private static ResolvedBlockModel Sign(string modelId, string sprite, string id,
        ImmutableDictionary<string, string> properties)
    {
        var hanging = id.Contains("hanging_sign", StringComparison.Ordinal);
        var wall = id.Contains("wall_", StringComparison.Ordinal);
        var rotation = new ElementRotation(1, SignGeometry.ElementAngle(properties), new Vector3(8));
        if (hanging)
        {
            var (boardFrom, boardTo) = SignGeometry.HangingBoard();
            return new ResolvedBlockModel(modelId,
            [
                EntityCuboid(boardFrom, boardTo, sprite, 0, 12, rotation,
                    sheetHeight: SignGeometry.SheetHeight, uvSize: SignGeometry.HangingBoardSize),
                Strap(2),
                Strap(13)
            ]);

            ModelElement Strap(float x)
            {
                var (from, to) = SignGeometry.HangingStrap(x);
                return EntityCuboid(from, to, sprite, 4, 0, rotation,
                    sheetHeight: SignGeometry.SheetHeight, uvSize: new Vector3(2, 4, 2));
            }
        }

        List<ModelElement> elements = [];
        var (standingFrom, standingTo) = SignGeometry.Board(wall);
        elements.Add(EntityCuboid(standingFrom, standingTo, sprite, 0, 0, rotation,
            sheetHeight: SignGeometry.SheetHeight, uvSize: SignGeometry.BoardSize));
        if (!wall)
        {
            var (stickFrom, stickTo) = SignGeometry.Stick();
            elements.Add(EntityCuboid(stickFrom, stickTo, sprite, 0, 14, rotation,
                sheetHeight: SignGeometry.SheetHeight, uvSize: SignGeometry.StickSize));
        }

        return new ResolvedBlockModel(modelId, elements);
    }

    private static ResolvedBlockModel Bed(string modelId, string sprite,
        ImmutableDictionary<string, string> properties)
    {
        properties.TryGetValue("part", out var part);
        var head = part == "head";
        List<ModelElement> elements =
        [
            UnwrappedBoxElement(new Vector3(0, 3, 0), new Vector3(16, 9, 16), sprite, 0, head ? 0 : 22)
        ];
        var legZ = part == "head" ? 13f : 1f;
        elements.Add(UnwrappedBoxElement(new Vector3(1, 0, legZ), new Vector3(3, 3, legZ + 2), sprite,
            head ? 50 : 50, head ? 0 : 12));
        elements.Add(UnwrappedBoxElement(new Vector3(13, 0, legZ), new Vector3(15, 3, legZ + 2), sprite,
            head ? 50 : 50, head ? 6 : 18));
        return new ResolvedBlockModel(modelId, elements);
    }

    private static ResolvedBlockModel ShulkerBox(string modelId, string sprite,
        ImmutableDictionary<string, string> properties)
    {
        properties.TryGetValue("facing", out var facing);
        var rotation = ShulkerRotation(facing ?? "up");
        return new ResolvedBlockModel(modelId,
        [
            // ShulkerModel 的底座是 16×8×16；盖子是 16×12×16。关闭状态下
            // 盖子从 y=4 到 16，与底座重叠 4px，而不是上下各占 8px。
            EntityCuboid(new Vector3(0, 0, 0), new Vector3(16, 8, 16), sprite, 0, 28, rotation),
            EntityCuboid(new Vector3(0, 4, 0), new Vector3(16, 16, 16), sprite, 0, 0, rotation)
        ]);
    }

    private static ModelElement UnwrappedBoxElement(Vector3 from, Vector3 to, string sprite, float u, float v)
    {
        var width = to.X - from.X;
        var height = to.Y - from.Y;
        var depth = to.Z - from.Z;
        var x0 = u;
        var x1 = u + depth;
        var x2 = x1 + width;
        var x3 = x2 + depth;
        var x4 = x3 + width;
        var y0 = v;
        var y1 = v + depth;
        var y2 = y1 + height;
        var scale = 16f / EntitySheetWidth;
        List<ElementFace> faces =
        [
            Face(FaceName.West, x0, y1, x1, y2),
            Face(FaceName.North, x1, y1, x2, y2),
            Face(FaceName.East, x2, y1, x3, y2),
            Face(FaceName.South, x3, y1, x4, y2),
            // 与 EntityCuboid 同一套原版段序：Down 第一格、Up 第二格，且顶格的 u 是
            // **width** 宽（右边界 u2+width，不是侧面的 u3）、v 倒序。床的盒件恰好
            // width==depth（16x16 与 2x2），所以这里一直看不出差别；改错写法不会立刻咬人，
            // 但换成任何 width!=depth 的盒件就会切错格。
            Face(FaceName.Down, x1, y0, x2, y1),
            Face(FaceName.Up, x2, y1, x2 + width, y0)
        ];
        return new ModelElement(from, to, null, faces);

        ElementFace Face(FaceName face, float a, float b, float c, float d) =>
            new(face, new Vector4(a * scale, b * scale, c * scale, d * scale), sprite, null, -1, 0);
    }

    private static ResolvedBlockModel DecoratedPot(string modelId, string sprite) => new(modelId,
    [
        BoxElement(new Vector3(5, 0, 5), new Vector3(11, 3, 11), "minecraft:entity/decorated_pot/decorated_pot_base"),
        BoxElement(new Vector3(3, 3, 3), new Vector3(13, 14, 13), sprite),
        BoxElement(new Vector3(4, 14, 4), new Vector3(12, 16, 12), "minecraft:entity/decorated_pot/decorated_pot_base")
    ]);

    private static ResolvedBlockModel Head(string modelId, string sprite, string id)
    {
        // Entity skins place the six head faces in the 8..32 x 0..16 part of a 64px sheet.
        // UV units here address 1/16 of the whole sheet, so divide pixel coordinates by four.
        var regions = new Dictionary<FaceName, Vector4>
        {
            [FaceName.Down] = new(4, 0, 6, 2),
            [FaceName.Up] = new(2, 0, 4, 2),
            [FaceName.West] = new(0, 2, 2, 4),
            [FaceName.South] = new(2, 2, 4, 4),
            [FaceName.East] = new(4, 2, 6, 4),
            [FaceName.North] = new(6, 2, 8, 4)
        };
        var large = id.Contains("dragon", StringComparison.Ordinal);
        var from = large ? new Vector3(2, 0, 1) : new Vector3(4, 0, 4);
        var to = large ? new Vector3(14, 10, 15) : new Vector3(12, 8, 12);
        List<ElementFace> faces = [.. regions.Select(pair => new ElementFace(pair.Key, pair.Value, sprite, null, -1, 0))];
        return new ResolvedBlockModel(modelId, [new ModelElement(from, to, null, faces)]);
    }

    // 实体贴图的像素坐标换算到模型 UV（0..16）。横向按 64px 宽的常见布局，
    // 纵向由 sheetHeight 定：告示牌那张是 64x32，v 的分母只有一半。
    private const float EntitySheetWidth = 64f;

    // 全新箱子模型：部件尺寸直接按实体模型像素建立，朝向由 element rotation
    // 统一处理。这里不预旋转顶点、不改面名，也不手工补顶面 UV rotation。
    private static ResolvedBlockModel Chest(string modelId, string spriteBase,
        ImmutableDictionary<string, string> properties)
    {
        properties.TryGetValue("facing", out var facingValue);
        properties.TryGetValue("type", out var typeValue);
        var rotation = ChestRotation(facingValue ?? "north");
        var type = typeValue ?? "single";
        if (type == "single" || !HasDoubleChestSprites(spriteBase))
            return new ResolvedBlockModel(modelId,
            [
                // 原版 ChestModel：bottom texOffs(0,19) addBox(1,0,1,14,10,14)、
                // lid texOffs(0,0) addBox(1,0,0,14,5,14) @ PartPose.offset(0,9,1)、
                // lock texOffs(0,0) addBox(7,-2,14,2,4,1) @ offset(0,9,1)。
                // 盖必须传 v=0：贴图的盖条带在 v14..18，身条带在 v33..42，盖传 v=19
                // 会把盖采成身的顶部五行（盖/身那道暗分界线就没了）。盖的 y 是 9..14，
                // 与箱身 0..10 重叠 1px——写 10..15 会让盖底和箱身顶共面抢深度。
                EntityCuboid(new Vector3(1, 0, 1), new Vector3(15, 10, 15), spriteBase, 0, 19, rotation, inset: false),
                EntityCuboid(new Vector3(1, 9, 1), new Vector3(15, 14, 15), spriteBase, 0, 0, rotation, inset: false),
                // The latch is only 2x4 pixels on the entity sheet.  Do not apply
                // the large-face seam inset here: a one-pixel inset on each side
                // would erase the visible front completely.
                EntityCuboid(new Vector3(7, 7, 15), new Vector3(9, 11, 16), spriteBase, 0, 0, rotation, inset: false)
            ]);

        var left = type == "left";
        var sprite = spriteBase + (left ? "_left" : "_right");
        // 半张贴图是 15 宽展开，透明接缝段在 _left 的第一段（West）、_right 的第三段
        // （East）。原版 left 是东侧半块、右侧是西侧半块，接缝必须朝里：left 盒贴西缘
        // x 0..15（接缝在 x=0）、right 盒贴东缘 x 1..16（接缝在 x=16）。
        // 盒子写反 + 面序镜像会互相抵消成「看着连得上」，真文件立刻出洞——用
        // --chest-dump 的 left@low / left@high 两段交叉核对，别只看一张。
        var x0 = left ? 0f : 1f;
        var x1 = left ? 15f : 16f;
        var latchX0 = left ? 0f : 15f;
        var latchX1 = left ? 1f : 16f;
        // 接缝那面原版根本不生成（left 少 West、right 少 East）。我们另有一份透明贴图，
        // 但只在 mip0 等效；粗 mip 上 mip-alpha 取最大会把透明段染成不透明，接缝面
        // 会在远处浮出一片暗板。两个半块接缝面共面，还会在格边界逐像素抢深度。
        var hidden = left ? FaceName.West : FaceName.East;
        return new ResolvedBlockModel(modelId,
        [
            EntityCuboid(new Vector3(x0, 0, 1), new Vector3(x1, 10, 15), sprite, 0, 19, rotation,
                inset: false, hidden: hidden),
            EntityCuboid(new Vector3(x0, 9, 1), new Vector3(x1, 14, 15), sprite, 0, 0, rotation,
                inset: false, hidden: hidden),
            EntityCuboid(new Vector3(latchX0, 7, 15), new Vector3(latchX1, 11, 16), sprite, 0, 0, rotation,
                inset: false, hidden: hidden)
        ]);
    }

    // uv 展开用**贴图像素里的盒子尺寸**（uvSize），不是世界尺寸：告示牌的盒子被缩到 2/3，
    // 拿世界尺寸算展开会切错贴图区域。箱子那种两边相等，所以这个参数一直不是必需的。
    //
    // hidden：原版用 `Util.allOfEnumExcept(WEST/EAST)` 让双箱半块**根本不生成**接缝那一面。
    // 我们靠「半张贴图的接缝段是透明的 + alpha cutout」等效，只有在 mip 链上才不等效——
    // mip alpha 取 2x2 最大，粗层把透明段染成不透明，接缝面会在远处浮出一片暗板。
    // 能像原版那样直接不生成就别依赖贴图 alpha。
    private static ModelElement EntityCuboid(Vector3 from, Vector3 to, string sprite, float u, float v,
        ElementRotation? rotation = null, bool inset = true,
        float sheetHeight = 64f, Vector3? uvSize = null, FaceName? hidden = null)
    {
        var size = uvSize ?? to - from;
        var width = size.X;
        var height = size.Y;
        var depth = size.Z;
        var u0 = u;
        var u1 = u + depth;
        var u2 = u1 + width;
        var u3 = u2 + depth;
        var u4 = u3 + width;
        // 顶格右边界是 u+dz+2*dx，侧面东段的右边界才是 u+2*dz+dx（= u3）。两者只有
        // width==depth 时才重合——单箱/潜影盒/床恰好相等，双箱半块 15x14 差 1px、
        // 告示牌板 24x2 差 12 倍（板顶那条 24 宽的窄面会被压成一竖条）。
        var uUp = u2 + width;
        var v0 = v;
        var v1 = v + depth;
        var v2 = v1 + height;
        // 顶/底段序和侧面一样来自原版：**Down 占第一格、Up 占第二格**，但顶格的 v 是
        // 倒着传的（原版 `UP=(u+dz+dx, v+dz, u+dz+2dx, v)`，minV > maxV）。这不是笔误：
        // Polygon 按顶点序分配 uv（[1]=(u0,v0)、[2]=(u0,v1)…），倒 v 让顶面的贴图沿 Z
        // 镜像回来，正好对上「俯视时南北颠倒」的观察方向。照抄正序会把箱盖顶面的木纹
        // 沿南北镜像（单箱看不出来，两格内容几乎对称，只有角上一个暗像素会换边）。
        // 26.3 未混淆 ModelPart$Cube.<init> 的原始字节码实算：
        //   var28=u+dz, var29=u+dz+dx, var30=u+dz+2dx, var31=u+2dz+dx, var32=u+2dz+2dx
        //   侧面顺序 = [西][北][东][南]；DOWN 用 (28,33,29,34)、UP 用 (30,33,29,34)。
        // 两个独立锚点钉住段序：① normal_left/_right 的透明段分别落在第 1/第 3 段；
        // ② 陷阱箱的红标记画在第 4 段，而官方说明是「闩四周泛红」。顶格的归属锚点：
        // 箱身第二格是「黑口 + 2px 木框」（从上看进去的箱内），第一格是素板。
        List<ElementFace> faces =
        [
            Face(FaceName.West, u0, v1, u1, v2),
            Face(FaceName.North, u1, v1, u2, v2),
            Face(FaceName.East, u2, v1, u3, v2),
            Face(FaceName.South, u3, v1, u4, v2),
            Face(FaceName.Down, u1, v0, u2, v1),
            Face(FaceName.Up, u2, v1, uUp, v0)
        ];
        if (hidden is { } skip)
            faces.RemoveAll(f => f.Face == skip);

        return new ModelElement(from, to, rotation, faces);

        ElementFace Face(FaceName face, float x0, float y0, float x1, float y1)
        {
            const float uvInset = 0.25f;
            float pad = inset ? uvInset : 0;
            var scaleU = 16f / EntitySheetWidth;
            var scaleV = 16f / sheetHeight;
            // 顶格是倒着传的（y0 > y1），内缩必须顺着各自的方向走：一律写 +y0/-y1
            // 会把它变成向外扩张，正好采到格外的邻段。
            var sx = x0 <= x1 ? 1f : -1f;
            var sy = y0 <= y1 ? 1f : -1f;
            return new ElementFace(face,
                new Vector4((x0 + pad * sx) * scaleU, (y0 + pad * sy) * scaleV,
                    (x1 - pad * sx) * scaleU, (y1 - pad * sy) * scaleV),
                sprite, null, -1, 0);
        }
    }

    private static bool HasDoubleChestSprites(string spriteBase) =>
        spriteBase.EndsWith("/normal", StringComparison.Ordinal) ||
        spriteBase.EndsWith("/trapped", StringComparison.Ordinal);

    private static ElementRotation? ChestRotation(string facing) => facing switch
    {
        "south" => null,
        "east" => new ElementRotation(1, 90, new Vector3(8)),
        "west" => new ElementRotation(1, -90, new Vector3(8)),
        _ => new ElementRotation(1, 180, new Vector3(8))
    };

    private static ElementRotation? ShulkerRotation(string facing) => facing switch
    {
        "down" => new ElementRotation(0, 180, new Vector3(8)),
        "north" => new ElementRotation(0, -90, new Vector3(8)),
        "south" => new ElementRotation(0, 90, new Vector3(8)),
        "east" => new ElementRotation(2, -90, new Vector3(8)),
        "west" => new ElementRotation(2, 90, new Vector3(8)),
        _ => null
    };
}
