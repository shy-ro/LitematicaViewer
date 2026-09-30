using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Compression;
using System.Numerics;
using System.Text.Json;
using LitematicaViewer.Assets;
using LitematicaViewer.Assets.Model;
using LitematicaViewer.Core.Model;
using LitematicaViewer.Core.Parsing;
using LitematicaViewer.Meshing.Extension;

namespace LitematicaViewer.Meshing.Tests;

// --preview：把包栈里每个方块状态按「三个面可见」的等距视图单独渲染成 PNG。
// 用途：材质解析 / 网格 uv 的故障（比如「铁块的面是空的」）要对账到单个状态，
// 靠在视口里找角度截图永远对不全；把每个状态的渲染产物铺成文件，
// 人查 + 程序查（完整方块内部透洞自动点名）都能落到位。
//
// 渲染走与 GPU 完全相同的前半段（resolver → BlockMeshBuilder → atlas 像素采样），
// 只有光栅化是软件的：z-buffer 三角形遍历 + NEAREST 采样 + 同一套 alpha cutout
// 与 tint 色板。所以材质链上的任何问题在这里必然复现；而 GPU 侧的问题
// （混合、mipmap）不会出现——那是刻意分开的：两边不一致本身就是一个判据。
internal static class PreviewBlocks
{
    private const int Side = 192;

    // 区域 alpha 加权均色（alpha>=128 才计色）。返回 (-1,-1,-1) 表示区域内无不透明纹素。
    // TexturePad 必须与 TextureAtlas.Pad 一致：那是 private 的，这里抄一份并注释钉死。
    private const int TexturePad = 8;

    // ---------- 软件光栅化 ----------

    // 等距视角：yaw 45°、pitch ≈ 33.7°，立方体的顶面与两个侧面同时可见。
    private static readonly Vector3 EyeDir = Vector3.Normalize(new Vector3(1f, 0.85f, 1f));
    private static readonly Vector3 Light = Vector3.Normalize(new Vector3(0.35f, 0.9f, 0.2f));

    private static readonly uint[] CrcTable = BuildCrcTable();

    public static int Run(string outDir, string[] packPaths)
    {
        return RunCore(outDir, null, packPaths);
    }

    // ---------- --regionrender：把真实 litematic 的整个 region 用软件光栅化画出来 ----------

    // 用途：GPU 截帧看着不对时，把同一条 BuildRegion 网格用软件光栅化再画一遍。
    // 软件这张用 level0 NEAREST 采样、无 mipmap、无混合——两边不一致就是 GPU 侧的问题；
    // 软件这张本身就花，就是网格/sprite 落位的问题。与 --montage 一样复用 RunCore 的
    // 装包与 atlas 构建，只是渲染对象从「单方块」换成「整个文档的每个 region」。
    public static int RunRegionRender(string[] args)
    {
        // args：--regionrender <out.png> <litematic> [边长] <资源包...>
        var outPath = args[1];
        var litematicPath = args[2];
        var scan = 3;
        var side = 768;
        if (scan < args.Length && int.TryParse(args[scan], out var parsedSide))
        {
            side = Math.Max(128, parsedSide);
            scan++;
        }

        List<string> packPaths = [.. args.Skip(scan)];
        using PackStack packs = new();
        foreach (var path in packPaths)
            packs.Add(Directory.Exists(path) ? ResourcePack.OpenFolder(path) : ResourcePack.OpenZip(path));

        BlockStateResolver resolver = new(packs);
        var result = LitematicLoader.TryLoadFile(litematicPath);
        if (!result.Success)
        {
            Console.Error.WriteLine($"[MESH][regionrender] 载入失败: {result.Error}");
            return 1;
        }

        // sprite 收集走文档调色板的真实状态（带属性），与 Sample 的 CollectSprites 同口径。
        HashSet<string> sprites = new(StringComparer.Ordinal);
        BlockMeshBuilder builder;
        foreach (var region in result.Document!.Regions)
        foreach (var state in region.Palette)
            try
            {
                foreach (var variant in resolver.Resolve(state.ToString()).Variants)
                foreach (var element in variant.Model.Elements)
                foreach (var face in element.Faces)
                    if (face.Sprite.Length > 0)
                        sprites.Add(face.Sprite);
            }
            catch
            {
                // 解析失败的状态真渲染时同样失败，网格期会跳过，这里不重复点名。
            }

        var atlas = TextureAtlas.Build(packs, sprites);
        Console.Out.WriteLine(
            $"[MESH][regionrender] 图集 {atlas.Width}x{atlas.Height} sprites={atlas.Rects.Count} 缺失={atlas.MissingCount}");
        builder = new BlockMeshBuilder(resolver, atlas);

        var regionIndex = 0;
        foreach (var region in result.Document.Regions)
        {
            var mesh = builder.BuildRegion(region);
            Vector3 centre = new(
                (region.Bounds.Min.X + region.Bounds.Max.X + 1f) / 2f,
                (region.Bounds.Min.Y + region.Bounds.Max.Y + 1f) / 2f,
                (region.Bounds.Min.Z + region.Bounds.Max.Z + 1f) / 2f);
            var radius = MathF.Max(
                region.Bounds.Size.X, MathF.Max(region.Bounds.Size.Y, region.Bounds.Size.Z)) / 2f;
            var rgb = RenderRegionToBuffer(mesh, atlas, side, centre, radius);
            var path = regionIndex == 0
                ? outPath
                : Path.Combine(
                    Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? ".",
                    $"{Path.GetFileNameWithoutExtension(outPath)}_r{regionIndex}{Path.GetExtension(outPath)}");
            WritePng(path, side, side, rgb);
            Console.Out.WriteLine($"[MESH][regionrender] {path} faces={mesh.Indices.Length / 6}");
            regionIndex++;
        }

        return 0;
    }

    // ---------- --montage：把 Render 的产物拼成一张网格大图 ----------

    // 与 --preview 走同一条 resolver → mesh → atlas → Render 链路，只是产物不落散图，
    // 而是按行序铺进一张 PNG。用途：快速人检「哪类方块不对劲」——翻几千张散图不现实，
    // 一张 montage 扫一眼就能圈出异常区，再回 --preview 拿单张细看。
    //
    // args：--montage <out.png> [过滤子串] [每格边长] [上限张数] <资源包...>
    // 过滤子串对状态键做 OrdinalContains（"iron" 只看铁系）。格边长默认 96，下限 32。
    // 同行写一个 .txt 索引（序号 → 状态键）：montage 上没法画字，对账靠它。
    public static int RunMontage(string[] args)
    {
        // args[1] 是输出路径，已在 MeshSmoke.Main 分流处保证存在。
        var outPath = args[1];
        var filter = string.Empty;
        var cell = 96;
        var limit = 512;
        // 固定顺序：[过滤子串] [格边长] [上限]，都可选，遇到第一个资源包路径即停。
        // （资源包路径若与数字同名会被吃掉——真实包名没有纯数字的，值得为省解析器不设转义。）
        var scan = 2;
        if (scan < args.Length && !File.Exists(args[scan]) && !Directory.Exists(args[scan])) filter = args[scan++];

        if (scan < args.Length && int.TryParse(args[scan], out var parsedCell))
        {
            cell = Math.Max(32, parsedCell);
            scan++;
        }

        if (scan < args.Length && int.TryParse(args[scan], out var parsedLimit))
        {
            limit = Math.Max(1, parsedLimit);
            scan++;
        }

        List<string> packPaths = [.. args.Skip(scan)];
        var montageDir = Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? ".";
        return RunCore(montageDir, new MontageOptions(outPath, filter, cell, limit), [.. packPaths]);
    }

    private static int RunCore(string outDir, MontageOptions? montage, string[] packPaths)
    {
        Directory.CreateDirectory(outDir);
        using TextWriterTraceListener listener = new(Console.Out);
        Trace.Listeners.Add(listener);
        Trace.AutoFlush = true;

        using PackStack packs = new();
        foreach (var path in packPaths)
        {
            var pack = Directory.Exists(path) ? ResourcePack.OpenFolder(path) : ResourcePack.OpenZip(path);
            packs.Add(pack);
            Debug.WriteLine($"[MESH][preview] 装包 {pack.Name}");
        }

        BlockStateResolver resolver = new(packs);

        // 1) 枚举状态：variants 的每个键就是一个状态（键串即属性）；
        //    multipart 只留空属性一档——材质检查用不到连接态的几何差异。
        //    montage 的过滤在枚举后立刻做：过滤掉的状态连 resolve 都省了。
        var states = EnumerateStates(packs);
        if (montage is { Filter.Length: > 0 } m)
            states = states.Where(s =>
                s.Id.Contains(m.Filter, StringComparison.Ordinal) ||
                s.Props.Contains(m.Filter, StringComparison.Ordinal)).ToList();

        Debug.WriteLine($"[MESH][preview] 状态总数 {states.Count}");

        // 2) 第一遍 resolve：收集 sprite；按「模型+旋转」去重——
        //    台阶 128 个状态的画面只有 8 种，铺 128 张只会淹掉真正异常的那张。
        Dictionary<string, string> unique = new(StringComparer.Ordinal);
        HashSet<string> sprites = new(StringComparer.Ordinal);
        List<string> resolveFailures = [];
        foreach (var (id, props) in states)
        {
            var key = props.Length == 0 ? id : $"{id}[{props}]";
            ResolvedBlockState resolved;
            try
            {
                resolved = resolver.Resolve(key);
            }
            catch (Exception ex)
            {
                resolveFailures.Add($"{key} ({ex.GetType().Name}: {ex.Message})");
                continue;
            }

            if (resolved.Variants.Count == 0)
            {
                resolveFailures.Add($"{key} (无 variant)");
                continue;
            }

            var signature = string.Join(
                ';',
                resolved.Variants.Select(v => $"{v.ModelId}@{v.XDegrees},{v.YDegrees}"));
            if (!unique.TryAdd(signature, key)) continue;


            var spriteEnumerable = from variant in resolved.Variants
                from element in variant.Model.Elements
                from face in element.Faces
                where face.Sprite.Length > 0
                select face.Sprite;
            sprites.AddMany(spriteEnumerable);
        }

        Debug.WriteLine(
            $"[MESH][preview] 唯一画面 {unique.Count} sprites={sprites.Count} 解析失败={resolveFailures.Count}");

        var atlas = TextureAtlas.Build(packs, sprites);
        Debug.WriteLine(
            $"[MESH][preview] 图集 {atlas.Width}x{atlas.Height} 缺图={atlas.MissingCount}");

        // 每层 mip 直接落盘（RGBA 丢 alpha 通道）：深层 mip 的跨 sprite 串色
        // 在整图视角下一眼就能认出来，是排查「远处方块换色」的第一现场。
        // montage 模式不落这些中间产物。
        if (montage is null)
            for (var level = 0; level < atlas.Levels.Length; level++)
            {
                var lw = Math.Max(1, atlas.Width >> level);
                var lh = Math.Max(1, atlas.Height >> level);
                var rgbaLevel = atlas.Levels[level];
                var rgb = new byte[lw * lh * 3];
                for (var i = 0; i < lw * lh; i++)
                {
                    rgb[i * 3 + 0] = rgbaLevel[i * 4 + 0];
                    rgb[i * 3 + 1] = rgbaLevel[i * 4 + 1];
                    rgb[i * 3 + 2] = rgbaLevel[i * 4 + 2];
                }

                WritePng(Path.Combine(outDir, $"_atlas_L{level}.png"), lw, lh, rgb);
            }

        // 程序查 mip 串色：每层 cell 的均色要留在 L0 自身均色的邻域里。
        // padding 复制自己的边缘、下采样按 alpha 加权、膨胀借的也是自己的颜色——
        // 正常链路均色漂移很小；一旦混进邻居的颜色，均色立刻跳走。
        // 这是独立于生成器的复核（阈值宽松，只抓大面积污染，不做报警器做体检）。
        var mipDrift = AuditMipColorDrift(atlas);

        BlockMeshBuilder builder = new(resolver, atlas);

        // 3) 逐状态渲染。
        List<string> hollowCubes = [];
        List<string> emptyMeshes = [];
        var written = 0;
        List<string> montageIndex = [];
        List<byte[]>? tiles = montage is null ? null : [];
        var cell = montage?.Cell ?? Side;
        foreach (var key in unique.Values.OrderBy(k => k, StringComparer.Ordinal))
        {
            var state = ParseStateKey(key);
            var mesh = builder.BuildRegion(MakeSingleBlockRegion(state));

            byte[] rgb;
            bool isFullCube;
            int holePixels;
            if (tiles is not null)
            {
                (rgb, isFullCube, holePixels) = RenderToBuffer(mesh, atlas, cell);
                if (tiles.Count < montage!.Limit)
                {
                    tiles.Add(rgb);
                    montageIndex.Add($"{tiles.Count - 1}: {key}");
                }
            }
            else
            {
                var path = Path.Combine(outDir, SafeFileName(key) + ".png");
                (rgb, isFullCube, holePixels) = RenderToBuffer(mesh, atlas, Side);
                WritePng(path, Side, Side, rgb);
            }

            written++;

            if (mesh.Indices.Length == 0) emptyMeshes.Add(key);

            if (isFullCube && holePixels > 4) hollowCubes.Add($"{key} holes={holePixels}");
        }

        if (tiles is not null && montage is not null)
        {
            WriteMontage(montage.OutPath, tiles, cell);
            var indexPath = Path.ChangeExtension(montage.OutPath, ".txt");
            File.WriteAllLines(indexPath, montageIndex);
        }

        // 4) 汇总报告：人查 PNG，程序查两类铁律故障。
        var report = Path.Combine(outDir, "_report.txt");
        using (StreamWriter writer = new(report))
        {
            writer.WriteLine($"状态总数 {states.Count} / 唯一画面 {unique.Count} / 输出 {written}");
            writer.WriteLine(
                $"图集 {atlas.Width}x{atlas.Height} 缺图 {atlas.MissingCount}: {string.Join(", ", atlas.MissingSprites)}");
            writer.WriteLine();
            writer.WriteLine($"== 解析失败 {resolveFailures.Count} ==");
            foreach (var line in resolveFailures) writer.WriteLine(line);

            writer.WriteLine();
            writer.WriteLine($"== 零面状态 {emptyMeshes.Count} ==");
            foreach (var line in emptyMeshes) writer.WriteLine(line);

            writer.WriteLine();
            writer.WriteLine($"== mip 层均色漂移超阈（跨 sprite 串色的量化信号，供参考）{mipDrift.Count} ==");
            foreach (var line in mipDrift) writer.WriteLine(line);

            writer.WriteLine();
            writer.WriteLine($"== 完整方块内部透洞（面材质空了的最直接证据）{hollowCubes.Count} ==");
            foreach (var line in hollowCubes) writer.WriteLine(line);
        }

        Debug.WriteLine(
            $"[MESH][preview] 完成 输出={written} 零面={emptyMeshes.Count} " +
            $"透洞完整方块={hollowCubes.Count} 报告={report}");
        return 0;
    }

    // ---------- 状态枚举 ----------

    private static List<(string Id, string Props)> EnumerateStates(PackStack packs)
    {
        List<(string Id, string Props)> states = [];
        foreach (var path in packs.Enumerate("assets/"))
        {
            if (!path.Contains("/blockstates/", StringComparison.Ordinal) ||
                !path.EndsWith(".json", StringComparison.Ordinal))
                continue;

            var nsStart = "assets/".Length;
            var marker = path.IndexOf("/blockstates/", nsStart, StringComparison.Ordinal);
            var ns = path[nsStart..marker];
            var name = path[(marker + "/blockstates/".Length)..^".json".Length];
            var id = $"{ns}:{name}";
            if (BlockStateDefinition.IsAirName(id)) continue;

            if (!packs.TryRead(path, out var content)) continue;

            try
            {
                using var document = JsonDocument.Parse(content);
                if (document.RootElement.TryGetProperty("variants", out var variants) &&
                    variants.ValueKind == JsonValueKind.Object)
                    foreach (var variant in variants.EnumerateObject())
                        states.Add((id, variant.Name));
                else if (document.RootElement.TryGetProperty("multipart", out _)) states.Add((id, string.Empty));
            }
            catch (JsonException)
            {
                // 坏 JSON 走 resolve 时会以异常形式进失败名单，这里不重复记。
            }
        }

        return states;
    }

    private static BlockStateDefinition ParseStateKey(string key)
    {
        var bracket = key.IndexOf('[');
        if (bracket < 0) return new BlockStateDefinition(key, BlockStateDefinition.NoProperties);

        var id = key[..bracket];
        Dictionary<string, string> props = new(StringComparer.Ordinal);
        foreach (var part in key[(bracket + 1)..^1].Split(','))
        {
            var equals = part.IndexOf('=');
            if (equals > 0) props[part[..equals]] = part[(equals + 1)..];
        }

        return new BlockStateDefinition(id, props.ToImmutableDictionary(StringComparer.Ordinal));
    }

    private static LitematicRegion MakeSingleBlockRegion(BlockStateDefinition state)
    {
        Vector3I position = new(0, 0, 0);
        Vector3I size = new(1, 1, 1);
        return new LitematicRegion(
            "preview",
            position,
            size,
            IntBounds.FromPositionSize(position, size),
            ImmutableArray.Create(state),
            ImmutableArray.Create(0));
    }

    private static string SafeFileName(string key)
    {
        return key.Replace(':', '_');
    }

    // region 模式入口：不关心完整方块判据，只要像素。
    private static byte[] RenderRegionToBuffer(MeshData mesh, TextureAtlas atlas, int side, Vector3 centre,
        float radius)
    {
        return RenderGeneral(mesh, atlas, side, centre, radius, false).Rgb;
    }

    private static (byte[] Rgb, bool IsFullCube, int HolePixels) RenderToBuffer(MeshData mesh, TextureAtlas atlas,
        int side)
    {
        return RenderGeneral(mesh, atlas, side, new Vector3(0.5f, 0.5f, 0.5f), 0.5f, true);
    }

    // 通用软件光栅化：等距视角（yaw 45°、pitch≈33.7°），正交投影，centre/radius 决定取景。
    // keepFullCubeInfo 只在单方块模式下有意义，region 模式恒 false。
    private static (byte[] Rgb, bool IsFullCube, int HolePixels) RenderGeneral(
        MeshData mesh, TextureAtlas atlas, int side, Vector3 centre, float radius, bool keepFullCubeInfo)
    {
        var rgb = new byte[side * side * 3];
        var zbuf = new float[side * side];
        Array.Fill(zbuf, float.NegativeInfinity);

        // right = up × eyeDir（lookAt 的 x 轴）；up 由两者叉积闭合。
        var right = Vector3.Normalize(Vector3.Cross(new Vector3(0, 1, 0), EyeDir));
        var up = Vector3.Cross(EyeDir, right);
        // 边距按比例留（5%）：montage 的 96px 格和单图的 192px 共用同一个取景逻辑。
        var scale = side / 2f * 0.9f / radius;

        var vertexCount = mesh.Vertices.Length / MeshData.FloatsPerVertex;
        var screen = vertexCount > 0 ? stackalloc Vector3[vertexCount] : default;
        if (vertexCount == 0) return (rgb, false, 0);

        for (var i = 0; i < vertexCount; i++)
        {
            Vector3 p = new(
                mesh.Vertices[i * MeshData.FloatsPerVertex + MeshData.PositionOffset],
                mesh.Vertices[i * MeshData.FloatsPerVertex + MeshData.PositionOffset + 1],
                mesh.Vertices[i * MeshData.FloatsPerVertex + MeshData.PositionOffset + 2]);
            var rel = p - centre;
            screen[i] = new Vector3(
                side / 2f + Vector3.Dot(rel, right) * scale,
                side / 2f - Vector3.Dot(rel, up) * scale,
                Vector3.Dot(rel, EyeDir));
        }

        var isFullCube = false;
        if (keepFullCubeInfo)
        {
            // 完整方块判据：恰好 6 个面、法线全部轴向对齐、包围盒占满单位立方体。
            isFullCube = mesh.Indices.Length / 6 == 6 && vertexCount == 24;
            if (isFullCube)
            {
                Vector3 min = new(float.MaxValue), max = new(float.MinValue);
                for (var i = 0; i < vertexCount; i++)
                {
                    Vector3 normal = new(
                        mesh.Vertices[i * MeshData.FloatsPerVertex + 3],
                        mesh.Vertices[i * MeshData.FloatsPerVertex + 4],
                        mesh.Vertices[i * MeshData.FloatsPerVertex + 5]);
                    var length = normal.Length();
                    if (length < 0.9f || length > 1.1f ||
                        !(MathF.Abs(normal.X) is > 0.9f or < 0.1f) ||
                        !(MathF.Abs(normal.Y) is > 0.9f or < 0.1f) ||
                        !(MathF.Abs(normal.Z) is > 0.9f or < 0.1f))
                    {
                        isFullCube = false;
                        break;
                    }

                    min = Vector3.Min(min, new Vector3(
                        mesh.Vertices[i * MeshData.FloatsPerVertex + 0],
                        mesh.Vertices[i * MeshData.FloatsPerVertex + 1],
                        mesh.Vertices[i * MeshData.FloatsPerVertex + 2]));
                    max = Vector3.Max(max, new Vector3(
                        mesh.Vertices[i * MeshData.FloatsPerVertex + 0],
                        mesh.Vertices[i * MeshData.FloatsPerVertex + 1],
                        mesh.Vertices[i * MeshData.FloatsPerVertex + 2]));
                }

                if (isFullCube &&
                    (min.X < -0.01f || min.Y < -0.01f || min.Z < -0.01f ||
                     max.X > 1.01f || max.Y > 1.01f || max.Z > 1.01f ||
                     max.X - min.X < 0.98f || max.Y - min.Y < 0.98f || max.Z - min.Z < 0.98f))
                    isFullCube = false;
            }
        }

        for (var tri = 0; tri < mesh.Indices.Length; tri += 3)
        {
            var i0 = mesh.Indices[tri];
            var i1 = mesh.Indices[tri + 1];
            var i2 = mesh.Indices[tri + 2];
            var s0 = screen[i0];
            var s1 = screen[i1];
            var s2 = screen[i2];

            var minX = MathF.Max(0, MathF.Floor(MathF.Min(s0.X, MathF.Min(s1.X, s2.X))));
            var maxX = MathF.Min(side - 1, MathF.Ceiling(MathF.Max(s0.X, MathF.Max(s1.X, s2.X))));
            var minY = MathF.Max(0, MathF.Floor(MathF.Min(s0.Y, MathF.Min(s1.Y, s2.Y))));
            var maxY = MathF.Min(side - 1, MathF.Ceiling(MathF.Max(s0.Y, MathF.Max(s1.Y, s2.Y))));
            if (maxX < minX || maxY < minY) continue;

            // 平面属性取首顶点：quads 是平的，法线与 tint 本来就逐面一份。
            Vector3 normal = new(
                mesh.Vertices[i0 * MeshData.FloatsPerVertex + 3],
                mesh.Vertices[i0 * MeshData.FloatsPerVertex + 4],
                mesh.Vertices[i0 * MeshData.FloatsPerVertex + 5]);
            var shade = 0.62f + 0.38f * MathF.Max(Vector3.Dot(normal, Light), 0f);
            var tintSlot = mesh.Vertices[i0 * MeshData.FloatsPerVertex + 8];
            var ao = mesh.Vertices[i0 * MeshData.FloatsPerVertex + MeshData.AoOffset];
            var tint = TintOf(tintSlot);

            var area = Edge(s0, s1, s2.X, s2.Y);
            if (MathF.Abs(area) < 1e-9f) continue;

            for (var y = (int)minY; y <= (int)maxY; y++)
            for (var x = (int)minX; x <= (int)maxX; x++)
            {
                var px = x + 0.5f;
                var py = y + 0.5f;
                // 重心坐标用循环边函数：l_k = E(下一顶点对, p) / area，
                // 循环不变式保证 l_k 在第 k 个顶点处取 1。第一版把 E01 的值
                // 配给了 i0 的属性——那实际是 i2 的权重，整张贴图沿对角线
                // 镜像错位，四边形两个三角形各错各的，看起来就是「面混一起」。
                var l0 = Edge(s1, s2, px, py) / area;
                var l1 = Edge(s2, s0, px, py) / area;
                var l2 = Edge(s0, s1, px, py) / area;
                if (l0 < 0 || l1 < 0 || l2 < 0) continue;

                var depth = l0 * s0.Z + l1 * s1.Z + l2 * s2.Z;
                var index = y * side + x;
                if (depth <= zbuf[index]) continue;

                var u = l0 * UvX(mesh, i0) + l1 * UvX(mesh, i1) + l2 * UvX(mesh, i2);
                var v = l0 * UvY(mesh, i0) + l1 * UvY(mesh, i1) + l2 * UvY(mesh, i2);

                // 与 GL 一致：v 不翻转，t=0 对应上传数据第一行（图集顶行），行 = v*H。
                var tx = Math.Clamp((int)(u * atlas.Width), 0, atlas.Width - 1);
                var ty = Math.Clamp((int)(v * atlas.Height), 0, atlas.Height - 1);
                var texel = (ty * atlas.Width + tx) * 4;
                if (atlas.Pixels[texel + 3] < 128) continue; // alpha cutout，与片元着色器同阈值

                zbuf[index] = depth;
                rgb[index * 3 + 0] = (byte)Math.Clamp(atlas.Pixels[texel + 0] * tint.X * shade * ao, 0, 255);
                rgb[index * 3 + 1] = (byte)Math.Clamp(atlas.Pixels[texel + 1] * tint.Y * shade * ao, 0, 255);
                rgb[index * 3 + 2] = (byte)Math.Clamp(atlas.Pixels[texel + 2] * tint.Z * shade * ao, 0, 255);
            }
        }

        // 完整方块的轮廓里不该有任何背景洞：内部一个背景像素四周全是画面，
        // 就是「面材质被 cutout 丢光」的直接形状证据。
        var holes = 0;
        if (isFullCube)
            for (var y = 1; y < side - 1; y++)
            for (var x = 1; x < side - 1; x++)
            {
                var index = y * side + x;
                if (zbuf[index] != float.NegativeInfinity) continue;

                if (zbuf[index - 1] != float.NegativeInfinity &&
                    zbuf[index + 1] != float.NegativeInfinity &&
                    zbuf[index - side] != float.NegativeInfinity &&
                    zbuf[index + side] != float.NegativeInfinity)
                    holes++;
            }

        return (rgb, isFullCube, holes);
    }

    private static float UvX(MeshData mesh, int vertex)
    {
        return mesh.Vertices[(vertex * MeshData.FloatsPerVertex) + MeshData.UvOffset];
    }

    private static float UvY(MeshData mesh, int vertex)
    {
        return mesh.Vertices[(vertex * MeshData.FloatsPerVertex) + MeshData.UvOffset + 1];
    }

    // 有向边函数 = 叉积 (b-a)×(p-a)，三角形内三点同号，绝对值之和 = 2×面积。
    private static float Edge(Vector3 a, Vector3 b, float px, float py)
    {
        return ((b.X - a.X) * (py - a.Y)) - ((b.Y - a.Y) * (px - a.X));
    }

    // 与片元着色器同一套固定色板（plains 群系）：0 不染 / 1 草 / 2 叶 / 3 水。
    // 两处必须同步改：这里是逐方块预览，那边是真渲染。
    private static Vector3 TintOf(float slot)
    {
        return slot switch
        {
            < 0.5f => new Vector3(1f),
            < 1.5f => new Vector3(0.569f, 0.741f, 0.349f),
            < 2.5f => new Vector3(0.467f, 0.671f, 0.184f),
            _ => new Vector3(0.247f, 0.463f, 0.894f)
        };
    }

    // ---------- 最小 PNG 编码：RGB8、无滤波、ZLibStream ----------

    // 每层 mip cell 的均色与 L0 整个 cell（含 padding）的均色比对，仅限「近全不透明」
    // 的 sprite（不透明覆盖 ≥95%）。基线必须取 cell 不能取内区：外扩边缘复制的是边缘色，
    // 边缘色≠内部色的贴图（原木年轮）会让比对产生与层无关的恒定假漂移（实测 44）。
    // 挖孔 sprite（花/叶/轨道）不参审：它们的深层形态由 alpha 膨胀主导（黑底被花色
    // 占据是设计行为，实测 allium 漂移 200+），均色比对对它们没有意义，靠 montage 人查。
    // 阈值宽松只抓大面积污染：不透明 sprite 实测正常漂移 ≤1，混进邻居颜色直接爆表。
    private static List<string> AuditMipColorDrift(TextureAtlas atlas)
    {
        List<string> flagged = [];
        foreach (var rect in atlas.Rects)
        {
            var cellX = rect.X - TexturePad;
            var cellY = rect.Y - TexturePad;
            var cellW = rect.Width + TexturePad * 2;
            var cellH = rect.Height + TexturePad * 2;
            if (OpaqueCoverage(atlas.Levels[0], atlas.Width, cellX, cellY, cellW, cellH) < 0.95f) continue;

            var (baseMeanR, baseMeanG, baseMeanB) = MeanColor(
                atlas.Levels[0], atlas.Width, cellX, cellY, cellW, cellH);
            if (baseMeanR < 0) continue; // 全透明 sprite 没有颜色可比

            for (var level = 1; level < atlas.Levels.Length; level++)
            {
                var lw = atlas.Width >> level;
                var lh = atlas.Height >> level;
                var lx = Math.Max(0, Math.Min(lw - 1, cellX >> level));
                var ly = Math.Max(0, Math.Min(lh - 1, cellY >> level));
                var cw = Math.Max(1, cellW >> level);
                var ch = Math.Max(1, cellH >> level);
                if (OpaqueCoverage(atlas.Levels[level], lw, lx, ly, cw, ch) < 0.95f) continue;

                var (r, g, b) = MeanColor(atlas.Levels[level], lw, lx, ly, cw, ch);
                if (r < 0) continue;

                var drift = Math.Max(Math.Max(Math.Abs(r - baseMeanR), Math.Abs(g - baseMeanG)),
                    Math.Abs(b - baseMeanB));
                if (drift > 30)
                    flagged.Add(
                        $"{rect.Sprite} L{level} drift={drift} base=({baseMeanR},{baseMeanG},{baseMeanB}) actual=({r},{g},{b})");
            }
        }

        return flagged.OrderBy(s => s, StringComparer.Ordinal).ToList();
    }

    // 区域内不透明纹素（alpha>=128）占比。
    private static float OpaqueCoverage(byte[] rgba, int width, int x, int y, int w, int h)
    {
        var opaque = 0;
        var total = 0;
        for (var row = y; row < y + h; row++)
        for (var col = x; col < x + w; col++)
        {
            var index = (row * width + col) * 4;
            if (index + 3 >= rgba.Length) continue;

            total++;
            if (rgba[index + 3] >= 128) opaque++;
        }

        return total == 0 ? 0f : opaque / (float)total;
    }

    private static (int R, int G, int B) MeanColor(byte[] rgba, int width, int x, int y, int w, int h)
    {
        long r = 0, g = 0, b = 0, count = 0;
        for (var row = y; row < y + h; row++)
        for (var col = x; col < x + w; col++)
        {
            var index = (row * width + col) * 4;
            if (index + 3 >= rgba.Length) continue;

            if (rgba[index + 3] >= 128)
            {
                r += rgba[index];
                g += rgba[index + 1];
                b += rgba[index + 2];
                count++;
            }
        }

        return count == 0 ? (-1, -1, -1) : ((int)(r / count), (int)(g / count), (int)(b / count));
    }

    // 把若干已渲染的方格拼成一张网格大图。列数按「总数开方向上取整」，
    // 接近正方最好扫视；1px 深灰缝分开相邻格子。
    private static void WriteMontage(string path, List<byte[]> tiles, int cell)
    {
        var columns = Math.Max(1, (int)MathF.Ceiling(MathF.Sqrt(tiles.Count)));
        var rows = Math.Max(1, (int)MathF.Ceiling(tiles.Count / (float)columns));
        var gap = 1;
        var width = columns * cell + (columns + 1) * gap;
        var height = rows * cell + (rows + 1) * gap;
        var rgb = new byte[width * height * 3];
        for (var i = 0; i < width * height; i++)
        {
            rgb[i * 3 + 0] = 40;
            rgb[i * 3 + 1] = 40;
            rgb[i * 3 + 2] = 40;
        }

        for (var tile = 0; tile < tiles.Count; tile++)
        {
            var tx = tile % columns;
            var ty = tile / columns;
            var originX = gap + tx * (cell + gap);
            var originY = gap + ty * (cell + gap);
            var pixels = tiles[tile];
            for (var y = 0; y < cell; y++)
            {
                var source = y * cell * 3;
                var target = ((originY + y) * width + originX) * 3;
                Buffer.BlockCopy(pixels, source, rgb, target, cell * 3);
            }
        }

        WritePng(path, width, height, rgb);
    }

    private static void WritePng(string path, int width, int height, byte[] rgb)
    {
        var raw = new byte[height * (1 + width * 3)];
        for (var y = 0; y < height; y++)
        {
            var row = y * (1 + width * 3);
            Buffer.BlockCopy(rgb, y * width * 3, raw, row + 1, width * 3);
        }

        using var file = File.Create(path);
        file.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

        var ihdr = new byte[13];
        Be32(ihdr, 0, (uint)width);
        Be32(ihdr, 4, (uint)height);
        ihdr[8] = 8; // 位深
        ihdr[9] = 2; // 真彩色
        WriteChunk(file, "IHDR", ihdr);

        byte[] idat;
        using (MemoryStream compressed = new())
        {
            using (ZLibStream zlib = new(compressed, CompressionLevel.Fastest))
            {
                zlib.Write(raw);
            }

            idat = compressed.ToArray();
        }

        WriteChunk(file, "IDAT", idat);
        WriteChunk(file, "IEND", []);
    }

    private static void Be32(byte[] target, int offset, uint value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var header = new byte[8];
        Be32(header, 0, (uint)data.Length);
        for (var i = 0; i < 4; i++) header[4 + i] = (byte)type[i];

        stream.Write(header);
        stream.Write(data);

        // PNG 的 CRC 只覆盖 type+data，header 里的 4 字节长度前缀不参与；
        // 算进去所有图 CRC 全错，严格解析器（PIL/浏览器）直接拒读。
        var crc = Crc32(header.AsSpan(4).ToArray(), data);
        var tail = new byte[4];
        Be32(tail, 0, crc);
        stream.Write(tail);
    }

    private static uint Crc32(byte[] a, byte[] b)
    {
        var table = CrcTable;
        var crc = 0xFFFFFFFF;
        foreach (var value in a) crc = table[(crc ^ value) & 0xFF] ^ (crc >> 8);

        foreach (var value in b) crc = table[(crc ^ value) & 0xFF] ^ (crc >> 8);

        return crc ^ 0xFFFFFFFF;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;

            table[i] = c;
        }

        return table;
    }

    private sealed record MontageOptions(string OutPath, string Filter, int Cell, int Limit);
}
