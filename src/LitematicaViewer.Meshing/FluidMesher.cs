using System.Diagnostics;
using System.Numerics;
using LitematicaViewer.Assets;
using LitematicaViewer.Core.Model;

namespace LitematicaViewer.Meshing;

// 流体几何（水/熔岩/bubble_column）。这些方块的模型 elements 为空，vanilla 用
// 流体渲染器按 level 与邻居现算：表面高随 level 递减，四角向同流体邻居取平均
// → 倾斜水面，相邻同流体共面连接、中间不画壁。这里复刻同一套规则（近似）；
// 贴图/染色归 FluidMaterial 与 tint 色板，邻居查询由调用方把 BuildRegion 手里
// 的调色板数组包成 World 视图喂进来——本类不碰 region，纯函数可单测。
//
// waterlogged 宿主（台阶/楼梯/栅栏……）算 level 0 的水：本体走正常模型路径，
// 水面叠画在宿主格里，vanilla 同样双层渲染。
public sealed class FluidMesher(TextureAtlas atlas)
{
    // 每个外表面沿法线向水体内部缩进的量。水logged 宿主（台阶/楼梯/墙）的模型面
    // 常在格边界上，水壁原样画在格边界就与之完全共面：半透明 pass 与不透明 pass
    // 深度相等，光栅化逐像素竞争，一半像素被宿主面吃掉——侧面出现锯齿摩尔纹、
    // 看着像水「穿」进方块。缩进只动平面位置、不动四角高：相邻同流体格之间本就
    // 不画壁，顶面在水平方向仍满格相接，缝隙只在亚像素级。
    internal const float Inset = 0.002f;
    // ---- 状态分类 ----

    public static bool IsPlainFluid(BlockStateDefinition state)
    {
        return FluidMaterial.IsFluidBlock(state.Name);
    }

    public static bool IsWaterlogged(BlockStateDefinition state)
    {
        return state.Properties.TryGetValue("waterlogged", out var waterlogged) && waterlogged == "true";
    }

    // 连通标记：普通流体与带水宿主都算「这里有一格水」。相邻水格必须共享这个
    // 标记才不画中间壁——不然每格各画一圈侧面，两格交界上两片重合四边形
    // 互相 z-fight。
    public static bool IsWaterCell(BlockStateDefinition state)
    {
        return IsPlainFluid(state) || IsWaterlogged(state);
    }

    // 1.20.1 的 level：0 源方块，1..7 流动（7 最浅），8..15 下落柱。
    // waterlogged 宿主 / bubble_column 视作 level 0。
    public static int LevelOf(BlockStateDefinition state)
    {
        return IsPlainFluid(state)
               && state.Properties.TryGetValue("level", out var raw)
               && int.TryParse(raw, out var level)
            ? level
            : 0;
    }

    // 表面高，vanilla 的 (8-level)/9：源 8/9≈14.2px（旧占位盒取 14/16 的出处），
    // level 7 剩 1/9 薄膜，8+ 下落柱整格满。
    public static float OwnHeight(int level)
    {
        return level >= 8 ? 1f : (8 - level) / 9f;
    }

    // ---- 出面 ----

    public void EmitCell(
        List<float> vertices, List<int> indices, Vector3 origin, World world,
        int x, int y, int z, int level, string sprite, float tint)
    {
        if (!atlas.TryGetRect(sprite, out var rect))
        {
            // CollectSprites 与 Build 走同一条分类路径，到这里缺图集只可能是包里
            // 没这张图；整格留痕跳过，画品红占位还得把它塞进图集，不值。
            Debug.WriteLine($"[MESH][fluid] sprite 不在图集里 {sprite}");
            return;
        }

        var h00 = Corner(world, x, y, z, level, -1, -1);
        var h10 = Corner(world, x, y, z, level, +1, -1);
        var h11 = Corner(world, x, y, z, level, +1, +1);
        var h01 = Corner(world, x, y, z, level, -1, +1);

        if (!world.IsWater(x, y + 1, z)) Top(vertices, indices, origin, h00, h10, h11, h01, rect, tint);

        if (!world.IsWater(x, y - 1, z) && !world.Occludes(x, y - 1, z)) Bottom(vertices, indices, origin, rect, tint);

        if (!world.IsWater(x + 1, y, z) && !world.Occludes(x + 1, y, z))
            East(vertices, indices, origin, h10, h11, rect, tint);

        if (!world.IsWater(x - 1, y, z) && !world.Occludes(x - 1, y, z))
            West(vertices, indices, origin, h00, h01, rect, tint);

        if (!world.IsWater(x, y, z + 1) && !world.Occludes(x, y, z + 1))
            South(vertices, indices, origin, h01, h11, rect, tint);

        if (!world.IsWater(x, y, z - 1) && !world.Occludes(x, y, z - 1))
            North(vertices, indices, origin, h00, h10, rect, tint);
    }

    // 四角高。vanilla 思路：头顶同流体 → 满格（列内部）；两侧与对角的同流体列
    // 参与平均 → 水面向低处倾斜。任一参与列向上连续时，共享角必须抬到满格，
    // 否则上层对角流体下方会露出三角缝。三列都不是流体时保持自身高。
    internal static float Corner(World world, int x, int y, int z, int level, int dx, int dz)
    {
        if (world.IsWater(x, y + 1, z)) return 1f;

        var self = OwnHeight(level);
        var side1 = ColumnTop(world, x + dx, y, z);
        var side2 = ColumnTop(world, x, y, z + dz);
        var diag = ColumnTop(world, x + dx, y, z + dz);

        if (side1 >= 1f || side2 >= 1f || diag >= 1f) return 1f;
        if (side1 < 0f && side2 < 0f && diag < 0f) return self;

        var sum = self + MathF.Max(side1, 0f) + MathF.Max(side2, 0f);
        var count = 1 + (side1 >= 0f ? 1 : 0) + (side2 >= 0f ? 1 : 0);
        if (diag >= 0f)
        {
            sum += diag;
            count++;
        }

        return sum / count;
    }

    // 邻格这列流体的表面高；不是流体 -1；列头上有同流体 → 满格。
    internal static float ColumnTop(World world, int x, int y, int z)
    {
        if (!world.IsWater(x, y, z)) return -1f;

        return world.IsWater(x, y + 1, z) ? 1f : OwnHeight(world.Level(x, y, z));
    }

    // 顶点序与 BlockMeshBuilder.BuildQuad 的 FaceBasis 推法一致（Up 面从
    // (x-,z-) 起，侧面从「从外面看的左上」起），三角形 A-B-C / A-C-D。
    // uv 一律 (0,0)(16,0)(16,16)(0,16) 覆满格，不随角高缩——vanilla 同样按格投。
    private void Top(List<float> vertices, List<int> indices, Vector3 origin,
        float h00, float h10, float h11, float h01, SpriteRect rect, float tint)
    {
        Emit(vertices, indices, origin,
            new Vector3(0, h00 - Inset, 0), new Vector3(1, h10 - Inset, 0),
            new Vector3(1, h11 - Inset, 1), new Vector3(0, h01 - Inset, 1),
            new Vector3(0, 1, 0), rect, tint);
    }

    private void Bottom(List<float> vertices, List<int> indices, Vector3 origin, SpriteRect rect, float tint)
    {
        Emit(vertices, indices, origin,
            new Vector3(0, Inset, 1), new Vector3(1, Inset, 1), new Vector3(1, Inset, 0), new Vector3(0, Inset, 0),
            new Vector3(0, -1, 0), rect, tint);
    }

    private void South(List<float> vertices, List<int> indices, Vector3 origin,
        float hWest, float hEast, SpriteRect rect, float tint)
    {
        Emit(vertices, indices, origin,
            new Vector3(0, hWest, 1 - Inset), new Vector3(1, hEast, 1 - Inset), new Vector3(1, 0, 1 - Inset),
            new Vector3(0, 0, 1 - Inset),
            new Vector3(0, 0, 1), rect, tint);
    }

    private void North(List<float> vertices, List<int> indices, Vector3 origin,
        float hWest, float hEast, SpriteRect rect, float tint)
    {
        Emit(vertices, indices, origin,
            new Vector3(1, hEast, Inset), new Vector3(0, hWest, Inset), new Vector3(0, 0, Inset),
            new Vector3(1, 0, Inset),
            new Vector3(0, 0, -1), rect, tint);
    }

    private void East(List<float> vertices, List<int> indices, Vector3 origin,
        float hNorth, float hSouth, SpriteRect rect, float tint)
    {
        Emit(vertices, indices, origin,
            new Vector3(1 - Inset, hSouth, 1), new Vector3(1 - Inset, hNorth, 0), new Vector3(1 - Inset, 0, 0),
            new Vector3(1 - Inset, 0, 1),
            new Vector3(1, 0, 0), rect, tint);
    }

    private void West(List<float> vertices, List<int> indices, Vector3 origin,
        float hNorth, float hSouth, SpriteRect rect, float tint)
    {
        Emit(vertices, indices, origin,
            new Vector3(Inset, hNorth, 0), new Vector3(Inset, hSouth, 1), new Vector3(Inset, 0, 1),
            new Vector3(Inset, 0, 0),
            new Vector3(-1, 0, 0), rect, tint);
    }

    // 顶点布局与 MeshData 的 9 floats 契约一致（pos3 normal3 uv2 tint1）。
    private void Emit(List<float> vertices, List<int> indices, Vector3 origin,
        Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal, SpriteRect rect, float tint)
    {
        var baseIndex = vertices.Count / MeshData.FloatsPerVertex;
        foreach (var (p, u16, v16) in new[]
                 {
                     (a, 0f, 0f), (b, 16f, 0f), (c, 16f, 16f), (d, 0f, 16f)
                 })
        {
            var uv = MapUv(u16, v16, rect);
            vertices.Add(origin.X + p.X);
            vertices.Add(origin.Y + p.Y);
            vertices.Add(origin.Z + p.Z);
            vertices.Add(normal.X);
            vertices.Add(normal.Y);
            vertices.Add(normal.Z);
            vertices.Add(uv.X);
            vertices.Add(uv.Y);
            vertices.Add(tint);
            vertices.Add(1f);
        }

        indices.Add(baseIndex);
        indices.Add(baseIndex + 1);
        indices.Add(baseIndex + 2);
        indices.Add(baseIndex);
        indices.Add(baseIndex + 2);
        indices.Add(baseIndex + 3);
    }

    // 与 BlockMeshBuilder.MapUv 同一约定：v = 图集 y/H 不翻转（GL 的 t=0 就是
    // 上传数据第一行，数组行 0 是图集顶），理由详见那边。
    private Vector2 MapUv(float u16, float v16, SpriteRect rect)
    {
        var u = (rect.X + u16 / 16f * rect.Width) / atlas.Width;
        var v = (rect.Y + v16 / 16f * rect.Height) / atlas.Height;
        return new Vector2(u, v);
    }

    // ---- 邻居视图：BuildRegion 的调色板数组直接喂进来，流体侧不碰 region ----

    public readonly struct World
    {
        private readonly bool[] _water;
        private readonly int[] _levels;
        private readonly bool[] _occluding;
        private readonly int[] _blocks;
        private readonly Vector3I _size;

        public World(bool[] water, int[] levels, bool[] occluding, int[] blocks, Vector3I size)
        {
            _water = water;
            _levels = levels;
            _occluding = occluding;
            _blocks = blocks;
            _size = size;
        }

        // 越界与坏下标一律「不是水、不遮挡」：与 BuildRegion 的空气口径一致，
        // 区域边界上的水照常对外出面。
        private bool Lookup(int x, int y, int z, out int paletteIndex)
        {
            paletteIndex = -1;
            if (x < 0 || y < 0 || z < 0 || x >= _size.X || y >= _size.Y || z >= _size.Z) return false;

            var index = (y * _size.Z + z) * _size.X + x;
            if ((uint)index >= (uint)_blocks.Length) return false;

            paletteIndex = _blocks[index];
            return (uint)paletteIndex < (uint)_water.Length;
        }

        public bool IsWater(int x, int y, int z)
        {
            return Lookup(x, y, z, out var i) && _water[i];
        }

        public int Level(int x, int y, int z)
        {
            return Lookup(x, y, z, out var i) ? _levels[i] : 0;
        }

        public bool Occludes(int x, int y, int z)
        {
            return Lookup(x, y, z, out var i) && _occluding[i];
        }
    }
}
