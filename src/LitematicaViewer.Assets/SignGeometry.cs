using System.Collections.Immutable;
using System.Numerics;

namespace LitematicaViewer.Assets;

// 告示牌（站牌/墙牌/吊牌）的像素几何。SpecialBlockModels 用它出模型盒，Meshing 的文字层
// 用它贴同一块牌面：两边各抄一份数字迟早错位，所以常量与换算都放这里共用。
//
// 数字全部来自原版：模型盒是 SignModel 的 addBox（1 单位 = 1 贴图像素），再按
// SignRenderer 的 RENDER_SCALE 缩到方块局部像素。
public static class SignGeometry
{
    // 实体贴图 minecraft:entity/signs/<wood>（含 hanging/）是 64x32，箱子/床那种是 64x64。
    // uv 纵向归一化必须用 16/32 而不是 16/64：一律按 4 除会把牌面挤进贴图靠上的四分之一，
    // 其余采样落到空像素上被 cutout 丢光，看起来就是「一块木头反复叠了几层」。
    public const float SheetHeight = 32f;

    // 牌面 addBox(-12,-14,-1, 24,12,2)，贴图 texOffs(0,0)。24 是模型像素，
    // (24/16)*(2/3) = 1 个方块宽——现代告示牌的牌子正好与格子等宽。
    private static readonly Vector3 BoardFrom = new(-12, -14, -1);
    public static readonly Vector3 BoardSize = new(24, 12, 2);

    // 支柱 addBox(-1,-2,-1, 2,14,2)，贴图 texOffs(0,14)。
    private static readonly Vector3 StickFrom = new(-1, -2, -1);
    public static readonly Vector3 StickSize = new(2, 14, 2);

    // 吊牌是另一套模型：板 14x10x2 texOffs(0,12)，板上的横梁 18x4x2 texOffs(0,0)。
    // 这里的吊带用横梁区的实心木纹（texOffs(4,0) 那块 2x4x2），不用贴图 6..11 行的
    // 真链环——那里是镂空的，1px 细杆采上去会碎成一串点。
    public static readonly Vector3 HangingBoardSize = new(14, 10, 2);

    // 站牌/吊牌的整体变换：(0.5,0.5,0.5) 平移 + (2/3,-2/3,-2/3) 缩放。支柱底端 my=12
    // 因此正好落在 y=0（立柱踩地），牌面上沿到 17.33px——比格子高 1.33px 是原版就有的
    // 探出，不是算错。原版模型 y 向下为正，这里换算成方块局部 y 向上。
    private const float Scale = 2f / 3f;

    // 墙牌：原版 WallSignRenderer 在整体变换之后再平移 (0,-5px,-7px)（方块局部，-Z 侧是墙）。
    // 牌面因此离墙 1/3px 而不是完全共面——共面会和墙的贴图面抢深度出摩尔纹。
    private static readonly Vector3 WallOffset = new(0, -5, -7);

    private static Vector3 Pixel(Vector3 model) =>
        new(8 + model.X * Scale, 8 - model.Y * Scale, 8 - model.Z * Scale);

    private static (Vector3 From, Vector3 To) Box(Vector3 from, Vector3 size)
    {
        var a = Pixel(from);
        var b = Pixel(from + size);
        return (Vector3.Min(a, b), Vector3.Max(a, b));
    }

    public static (Vector3 From, Vector3 To) Board(bool wall)
    {
        var (from, to) = Box(BoardFrom, BoardSize);
        return wall ? (from + WallOffset, to + WallOffset) : (from, to);
    }

    public static (Vector3 From, Vector3 To) Stick() => Box(StickFrom, StickSize);

    // 吊牌沿用现有摆放（板 y3..13、两条吊带顶到格顶），只把贴图对正。
    public static (Vector3 From, Vector3 To) HangingBoard() => (new Vector3(1, 3, 7), new Vector3(15, 13, 9));

    public static (Vector3 From, Vector3 To) HangingStrap(float x) =>
        (new Vector3(x, 13, 7.6f), new Vector3(x + 1, 16, 8.4f));

    // 朝向角，**element 约定**（与箱子 ChestRotation 同号：东 = +90，交给 RotateElement
    // 的正角）。站牌/吊牌的 rotation 状态值 0..15 是「从上方看顺时针」每格 22.5°，
    // 与 facing 反向计数，所以取负；0/180 自逆，掩盖了这个号很久。
    public static float ElementAngle(ImmutableDictionary<string, string> properties)
    {
        if (properties.TryGetValue("rotation", out var rotation) && int.TryParse(rotation, out var step))
            return -step * 22.5f;
        return properties.TryGetValue("facing", out var facing)
            ? facing switch { "east" => 90f, "south" => 0f, "west" => 270f, _ => 180f }
            : 0f;
    }

    // Meshing 的 Rotate/RotateAround 走 blockstate 约定（与 element 反号）。文字层把这个值
    // 直接喂 RotateAround，得到的朝向就和牌面的 ElementRotation 一致；牌面与文字同源，
    // 修完贴图才不会露出「字在背面」的第二个 bug。
    public static float BlockStateAngle(ImmutableDictionary<string, string> properties) =>
        -ElementAngle(properties);
}
