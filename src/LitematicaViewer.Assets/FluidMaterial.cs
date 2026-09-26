namespace LitematicaViewer.Assets;

// 流体的材质入口。水/熔岩/bubble_column 的 blockstate 模型 elements 是空的
// （vanilla 本体交给流体渲染器），形状在网格层按 level 与邻居现算；这里只
// 回答「哪些 id 是流体、用哪张贴图」。资产层看不见邻居，不掺和几何。
// 字符串出入参：Assets 不引用 Core，网格层拿 BlockStateDefinition.Name 喂进来。
public static class FluidMaterial
{
    // bubble_column 也是流体渲染的格子（模型同样为空），贴水图。
    public static bool IsFluidBlock(string blockName) =>
        blockName is "minecraft:water" or "minecraft:lava" or "minecraft:bubble_column";

    public static string SpriteFor(string blockName) => blockName switch
    {
        "minecraft:lava" => "minecraft:block/lava_still",
        "minecraft:water" or "minecraft:bubble_column" => "minecraft:block/water_still",
        _ => "",
    };
}
