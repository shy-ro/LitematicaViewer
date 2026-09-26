using System.Diagnostics;
using System.Numerics;
using LitematicaViewer.Assets;
using LitematicaViewer.Assets.Model;
using LitematicaViewer.Core.Model;

namespace LitematicaViewer.Meshing;

// Document → 网格。邻居是空气才出面（区域外算空气，外表面照常出），
// 每个面的几何与 uv 从资产层解析出的 element 拿，variant 的 x/y 旋转与
// element 的自身旋转在这里变成矩阵。
public sealed class BlockMeshBuilder
{
    private readonly BlockStateResolver _resolver;
    private readonly TextureAtlas _atlas;
    private readonly Dictionary<string, CachedState> _cache = new(StringComparer.Ordinal);

    private int _skippedFaces;
    private int _emptySpriteFaces;

    public BlockMeshBuilder(BlockStateResolver resolver, TextureAtlas atlas)
    {
        _resolver = resolver;
        _atlas = atlas;
    }

    public string Stats => $"skippedFaces={_skippedFaces} emptySpriteFaces={_emptySpriteFaces}";

    // 调色板里实际用到的 sprite 全集。先收集再建图集，图集里就没有用不上的贴图。
    // 收 region 列表而不是 document：调用方（Sample）可能只网格化单个 region。
    //
    // 这里必须直走 resolver 而不是 GetState 的缓存：缓存里的四边形是在图集上算的
    // （uv 要映射进 sprite 落位），而图集要等这份收集做完才建得出来——
    // 先有蛋后有鸡的顺序反过来一次，sprite 全集就永远是空的。
    public void CollectSprites(IEnumerable<LitematicRegion> regions, HashSet<string> output)
    {
        HashSet<string> seenStates = new(StringComparer.Ordinal);
        foreach (LitematicRegion region in regions)
        {
            foreach (BlockStateDefinition state in region.Palette)
            {
                if (state.IsAir || !seenStates.Add(state.ToString()))
                {
                    continue;
                }

                foreach (ResolvedVariant variant in _resolver.Resolve(state.ToString()).Variants)
                {
                    foreach (ModelElement element in variant.Model.Elements)
                    {
                        foreach (ElementFace face in element.Faces)
                        {
                            if (face.Sprite.Length > 0)
                            {
                                output.Add(face.Sprite);
                            }
                        }
                    }
                }
            }
        }
    }

    public MeshData BuildRegion(LitematicRegion region)
    {
        Vector3I size = region.Bounds.Size;
        Debug.Assert(size.X > 0 && size.Y > 0 && size.Z > 0, $"[MESH] region {region.Name} 尺寸非正 {size}");

        // 调色板的空气标记一次备好：内层循环里查数组比查 HashSet 快一个量级，
        // 大模型（几百万体素 × 6 邻居）全靠这里。
        bool[] airFlags = new bool[region.Palette.Length];
        for (int i = 0; i < airFlags.Length; i++)
        {
            airFlags[i] = region.Palette[i].IsAir;
        }

        List<float> vertices = [];
        List<int> indices = [];
        int[] blocks = region.BlockIndices.ToArray();

        for (int y = 0; y < size.Y; y++)
        {
            for (int z = 0; z < size.Z; z++)
            {
                int rowBase = ((y * size.Z) + z) * size.X;
                for (int x = 0; x < size.X; x++)
                {
                    int index = rowBase + x;
                    int paletteIndex = blocks[index];
                    if ((uint)paletteIndex >= (uint)airFlags.Length || airFlags[paletteIndex])
                    {
                        // 越界下标按空气跳过，与 Core 的拾取/统计同一口径；
                        // 回退 palette[0] 的读法会让「统计说没方块」的地方长出方块。
                        continue;
                    }

                    BlockStateDefinition state = region.Palette[paletteIndex];
                    CachedState cached = GetState(state);
                    if (cached.Variants.Count == 0)
                    {
                        continue;
                    }

                    // 多 variant 的花色（stone 的 4 个旋转）按位置散列轮换，
                    // 与 MC 的随机取法一样是逐方块变化，但这里是确定性的：
                    // 同一文件每次载入画面完全一致，像素校验才有稳定的参照物。
                    CachedVariant variant = cached.Variants[
                        (int)(((uint)(x * 73856093) ^ ((uint)y * 19349663) ^ ((uint)z * 83492791)) % (uint)cached.Variants.Count)];

                    Vector3 origin = new(region.Bounds.Min.X + x, region.Bounds.Min.Y + y, region.Bounds.Min.Z + z);
                    foreach (CachedQuad quad in variant.Quads)
                    {
                        if (quad.Cullface is Vector3 cull && IsNeighborSolid(blocks, airFlags, size, x, y, z, cull))
                        {
                            _skippedFaces++;
                            continue;
                        }

                        if (quad.Sprite.Length == 0)
                        {
                            // 引用解不开的面直接丢（日志在资产层打过一次）；
                            // 补一块棋盘占位反而需要把它塞进图集，不值得。
                            _emptySpriteFaces++;
                            continue;
                        }

                        EmitQuad(vertices, indices, origin, quad);
                    }
                }
            }
        }

        Debug.WriteLine(
            $"[MESH][build] region={region.Name} blocks={region.CountNonAirBlocks()} " +
            $"verts={vertices.Count / MeshData.FloatsPerVertex} faces={indices.Count / 6}");
        return new MeshData([.. vertices], [.. indices]);
    }

    // ---------- 缓存：每个方块状态解析一次，旋转在缓存时就算完 ----------

    private sealed record CachedQuad(
        Vector3 A, Vector3 B, Vector3 C, Vector3 D,
        Vector3 Normal,
        Vector2 UvA, Vector2 UvB, Vector2 UvC, Vector2 UvD,
        string Sprite,
        Vector3? Cullface);

    private sealed record CachedVariant(List<CachedQuad> Quads);

    private sealed record CachedState(List<CachedVariant> Variants);

    private CachedState GetState(BlockStateDefinition state)
    {
        // BlockStateDefinition.ToString() 已按 key 排序：同一状态在缓存里只有一份，
        // 不同进程里拼出的键也一致（这个保证写在 Core 那边的注释里）。
        string key = state.ToString();
        if (_cache.TryGetValue(key, out CachedState? cached))
        {
            return cached;
        }

        ResolvedBlockState resolved = _resolver.Resolve(key);
        List<CachedVariant> variants = [];
        foreach (ResolvedVariant variant in resolved.Variants)
        {
            List<CachedQuad> quads = [];
            foreach (ModelElement element in variant.Model.Elements)
            {
                foreach (ElementFace face in element.Faces)
                {
                    if (BuildQuad(element, face, variant) is { } quad)
                    {
                        quads.Add(quad);
                    }
                }
            }

            variants.Add(new CachedVariant(quads));
        }

        CachedState result = new(variants);
        _cache[key] = result;
        return result;
    }

    private CachedQuad? BuildQuad(ModelElement element, ElementFace face, ResolvedVariant variant)
    {
        if (!TryGetRect(face.Sprite, out SpriteRect rect))
        {
            // sprite 没进图集（例如带 tint 的覆盖图后来才收集）不该发生：
            // CollectSprites 与 Build 用同一个解析路径。真发生时按缺面处理并留痕。
            Debug.WriteLine($"[MESH][build] sprite 不在图集里 face={face.Sprite}");
            return null;
        }

        Vector3 from = element.From / 16f;
        Vector3 to = element.To / 16f;
        (Vector3 normal, Vector3 r, Vector3 d) = FaceBasis(face.Face);

        // 面四角：从「屏幕左上」出发，往右加 r、往下加 d。
        // r/d 是轴对齐单位向量（带符号），左上角按「沿 r 的负方向、沿 d 的负方向取到边」推。
        int nAxis = AxisOf(normal);
        float nSign = ComponentOf(normal, nAxis);
        Vector3 tl = new(
            Pick(from.X, to.X, 0, nSign),
            Pick(from.Y, to.Y, 1, nSign),
            Pick(from.Z, to.Z, 2, nSign));

        int rAxis = AxisOf(r);
        float rSign = ComponentOf(r, rAxis);
        tl = WithAxis(tl, rAxis, Pick(from, to, rAxis, -rSign));

        int dAxis = AxisOf(d);
        float dSign = ComponentOf(d, dAxis);
        tl = WithAxis(tl, dAxis, Pick(from, to, dAxis, -dSign));

        Vector3 rVec = r * Extent(from, to, rAxis);
        Vector3 dVec = d * Extent(from, to, dAxis);
        Vector3 a = tl;
        Vector3 b = tl + rVec;
        Vector3 c = tl + rVec + dVec;
        Vector3 e = tl + dVec;

        // element 自身旋转（big_dripleaf 的 45°），再叠 variant 的整体旋转（横放的原木）。
        // 顺序固定：先 element 后 variant；橡木原木 axis=x 的冒烟断言钉着这套约定。
        if (element.Rotation is { } rot)
        {
            Vector3 origin = rot.Origin / 16f;
            a = RotateAround(a, rot.Axis, rot.AngleDegrees, origin);
            b = RotateAround(b, rot.Axis, rot.AngleDegrees, origin);
            c = RotateAround(c, rot.Axis, rot.AngleDegrees, origin);
            e = RotateAround(e, rot.Axis, rot.AngleDegrees, origin);
            normal = Rotate(normal, rot.Axis, rot.AngleDegrees);
        }

        a = ApplyVariantRotation(a, variant);
        b = ApplyVariantRotation(b, variant);
        c = ApplyVariantRotation(c, variant);
        e = ApplyVariantRotation(e, variant);
        normal = RotateDirection(normal, variant);

        // uv：元素面 0..16 → 图集像素 → [0,1]，v 在这里翻转（图集左上 vs GL 左下）。
        Vector2 uvA = MapUv(face.Uv.X, face.Uv.Y, rect);
        Vector2 uvB = MapUv(face.Uv.Z, face.Uv.Y, rect);
        Vector2 uvC = MapUv(face.Uv.Z, face.Uv.W, rect);
        Vector2 uvD = MapUv(face.Uv.X, face.Uv.W, rect);

        Vector3? cullface = face.Cullface is string dir && TryDirection(dir, out Vector3 cull) ? cull : null;
        return new CachedQuad(a, b, c, e, normal, uvA, uvB, uvC, uvD, face.Sprite, cullface);
    }

    private Vector3 ApplyVariantRotation(Vector3 v, ResolvedVariant variant)
    {
        Vector3 result = v - new Vector3(0.5f);
        if (variant.XDegrees != 0f)
        {
            result = Rotate(result, 0, variant.XDegrees);
        }

        if (variant.YDegrees != 0f)
        {
            result = Rotate(result, 1, variant.YDegrees);
        }

        return result + new Vector3(0.5f);
    }

    // 方向量（法线）只旋转不平移：绕中心转的那个「减中心加中心」套在方向上，
    // 会把 (0,1,0) 转成 <±1,1,1> 这种长度 1.7 的鬼东西——实锤过一次。
    private static Vector3 RotateDirection(Vector3 v, ResolvedVariant variant)
    {
        Vector3 result = v;
        if (variant.XDegrees != 0f)
        {
            result = Rotate(result, 0, variant.XDegrees);
        }

        if (variant.YDegrees != 0f)
        {
            result = Rotate(result, 1, variant.YDegrees);
        }

        return result;
    }

    private static Vector3 RotateAround(Vector3 v, int axis, float degrees, Vector3 origin) =>
        Rotate(v - origin, axis, degrees) + origin;

    // 右手系、绕正轴逆时针。MC variant/element 的旋转符号是否同向，由
    // oak_log[axis=x] 的冒烟断言（端面帽必须朝 ±X）钉着，反了就改这里。
    private static Vector3 Rotate(Vector3 v, int axis, float degrees)
    {
        float radians = degrees * MathF.PI / 180f;
        float cos = MathF.Cos(radians);
        float sin = MathF.Sin(radians);
        return axis switch
        {
            0 => new Vector3(v.X, (v.Y * cos) - (v.Z * sin), (v.Y * sin) + (v.Z * cos)),
            1 => new Vector3((v.X * cos) + (v.Z * sin), v.Y, (-v.X * sin) + (v.Z * cos)),
            2 => new Vector3((v.X * cos) - (v.Y * sin), (v.X * sin) + (v.Y * cos), v.Z),
            _ => v,
        };
    }

    // 面基：normal 朝外；r/d = 「从外面看这个面」的右与下。
    // 推导：观察方向 f = -normal，屏幕右 r = f × up，屏幕下 d = f × r。
    // 顶/底两面 up 取 ∓Z（俯视时屏幕上方是北），侧面一律取 +Y。
    private static (Vector3 Normal, Vector3 R, Vector3 D) FaceBasis(FaceName face)
    {
        (Vector3 normal, Vector3 up) = face switch
        {
            FaceName.Down => (new Vector3(0, -1, 0), new Vector3(0, 0, 1)),
            FaceName.Up => (new Vector3(0, 1, 0), new Vector3(0, 0, -1)),
            FaceName.North => (new Vector3(0, 0, -1), new Vector3(0, 1, 0)),
            FaceName.South => (new Vector3(0, 0, 1), new Vector3(0, 1, 0)),
            FaceName.West => (new Vector3(-1, 0, 0), new Vector3(0, 1, 0)),
            FaceName.East => (new Vector3(1, 0, 0), new Vector3(0, 1, 0)),
            _ => (Vector3.UnitY, Vector3.UnitY),
        };

        Vector3 view = -normal;
        Vector3 r = Vector3.Cross(view, up);
        Vector3 d = Vector3.Cross(view, r);
        return (normal, r, d);
    }

    private bool IsNeighborSolid(int[] blocks, bool[] airFlags, Vector3I size, int x, int y, int z, Vector3 direction)
    {
        int nx = x + (int)direction.X;
        int ny = y + (int)direction.Y;
        int nz = z + (int)direction.Z;
        if (nx < 0 || ny < 0 || nz < 0 || nx >= size.X || ny >= size.Y || nz >= size.Z)
        {
            return false; // 区域外算空气：外表面照常画
        }

        int index = ((ny * size.Z) + nz) * size.X + nx;
        return (uint)index < (uint)blocks.Length
            && (uint)blocks[index] < (uint)airFlags.Length
            && !airFlags[blocks[index]];
    }

    private static void EmitQuad(List<float> vertices, List<int> indices, Vector3 origin, CachedQuad quad)
    {
        int baseIndex = vertices.Count / MeshData.FloatsPerVertex;
        foreach ((Vector3 position, Vector2 uv) in new[]
                 {
                     (quad.A, quad.UvA), (quad.B, quad.UvB), (quad.C, quad.UvC), (quad.D, quad.UvD),
                 })
        {
            vertices.Add(origin.X + position.X);
            vertices.Add(origin.Y + position.Y);
            vertices.Add(origin.Z + position.Z);
            vertices.Add(quad.Normal.X);
            vertices.Add(quad.Normal.Y);
            vertices.Add(quad.Normal.Z);
            vertices.Add(uv.X);
            vertices.Add(uv.Y);
        }

        indices.Add(baseIndex);
        indices.Add(baseIndex + 1);
        indices.Add(baseIndex + 2);
        indices.Add(baseIndex);
        indices.Add(baseIndex + 2);
        indices.Add(baseIndex + 3);
    }

    private Vector2 MapUv(float u16, float v16, SpriteRect rect)
    {
        // uv 0..16（MC 纹素）→ 图集像素 → [0,1]。v 取 1 -：图集行 0 是贴图顶，
        // GL 采样 v=0 在底下。
        float u = (rect.X + ((u16 / 16f) * rect.Width)) / _atlas.Width;
        float v = (rect.Y + ((v16 / 16f) * rect.Height)) / _atlas.Height;
        return new Vector2(u, 1f - v);
    }

    private bool TryGetRect(string sprite, out SpriteRect rect) => _atlas.TryGetRect(sprite, out rect);

    private static float Pick(float from, float to, int axis, float sign) =>
        axis switch
        {
            0 => sign > 0 ? to : from,
            1 => sign > 0 ? to : from,
            _ => sign > 0 ? to : from,
        };

    private static float Pick(Vector3 from, Vector3 to, int axis, float sign) => axis switch
    {
        0 => sign > 0 ? to.X : from.X,
        1 => sign > 0 ? to.Y : from.Y,
        _ => sign > 0 ? to.Z : from.Z,
    };

    private static Vector3 WithAxis(Vector3 v, int axis, float value) => axis switch
    {
        0 => v with { X = value },
        1 => v with { Y = value },
        _ => v with { Z = value },
    };

    private static int AxisOf(Vector3 v) =>
        MathF.Abs(v.X) > 0.5f ? 0 : MathF.Abs(v.Y) > 0.5f ? 1 : 2;

    private static float ComponentOf(Vector3 v, int axis) => axis switch
    {
        0 => v.X,
        1 => v.Y,
        _ => v.Z,
    };

    private static float Extent(Vector3 from, Vector3 to, int axis) => axis switch
    {
        0 => MathF.Abs(to.X - from.X),
        1 => MathF.Abs(to.Y - from.Y),
        _ => MathF.Abs(to.Z - from.Z),
    };

    private static bool TryDirection(string name, out Vector3 direction)
    {
        switch (name)
        {
            case "down": direction = new Vector3(0, -1, 0); return true;
            case "up": direction = new Vector3(0, 1, 0); return true;
            case "north": direction = new Vector3(0, 0, -1); return true;
            case "south": direction = new Vector3(0, 0, 1); return true;
            case "west": direction = new Vector3(-1, 0, 0); return true;
            case "east": direction = new Vector3(1, 0, 0); return true;
            default: direction = default; return false;
        }
    }
}
