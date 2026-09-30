using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;
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

    // montage 的底色。格子背景必须与它一致：格子之间不留缝、不画边框，
    // 只要两者不同色就会看到一圈深色方框（那正是「不同颜色的边框」的来源）。
    private const byte BackgroundR = 40;
    private const byte BackgroundG = 40;
    private const byte BackgroundB = 40;

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
    // args：--montage <out.png> [--names] [--gap N] [过滤子串] [每格边长] [上限张数] <资源包...>
    // 过滤子串对状态键做 OrdinalContains（"iron" 只看铁系）。格边长默认 128，下限 32；
    // 格间距 --gap 默认 0（格子同底色，本来也看不出缝）。--names 在每格左侧的标签列里
    // 画状态键（系统字体、纯黄字、无底条、按列宽断行不截断，全名另在 .txt 索引里）。
    // 开关先行剥离再走位置参数，否则会被当成过滤子串吃掉。同行写一个 .txt 索引。
    public static int RunMontage(string[] args)
    {
        // args[1] 是输出路径，已在 MeshSmoke.Main 分流处保证存在。
        var outPath = args[1];
        var names = false;
        var gap = 0;
        var rest = new List<string>();
        for (var i = 2; i < args.Length; i++)
        {
            if (args[i] == "--names") names = true;
            else if (args[i] == "--nonames") { /* 默认即关，容错成对 */ }
            else if (args[i] == "--gap" && i + 1 < args.Length && int.TryParse(args[++i], out var parsedGap))
                gap = Math.Clamp(parsedGap, 0, 64);
            else rest.Add(args[i]);
        }

        var filter = string.Empty;
        var cell = 128;
        var limit = 512;
        // 固定顺序：[过滤子串] [格边长] [上限]，都可选，遇到第一个资源包路径即停。
        // （资源包路径若与数字同名会被吃掉——真实包名没有纯数字的，值得为省解析器不设转义。）
        var scan = 0;
        if (scan < rest.Count && !File.Exists(rest[scan]) && !Directory.Exists(rest[scan])) filter = rest[scan++];

        if (scan < rest.Count && int.TryParse(rest[scan], out var parsedCell))
        {
            cell = Math.Max(32, parsedCell);
            scan++;
        }

        // 上限 0（或负）＝不限，全量铺满。上限只是给「几千张铺一张」省内存的闸门。
        if (scan < rest.Count && int.TryParse(rest[scan], out var parsedLimit))
        {
            limit = parsedLimit <= 0 ? int.MaxValue : parsedLimit;
            scan++;
        }

        List<string> packPaths = [.. rest.Skip(scan)];
        var montageDir = Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? ".";
        return RunCore(montageDir, new MontageOptions(outPath, filter, cell, limit, names, gap), [.. packPaths]);
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
        List<string> montageKeys = [];
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
                (rgb, isFullCube, holePixels) = RenderToBuffer(mesh, atlas, cell, BackgroundR);
                if (tiles.Count < montage!.Limit)
                {
                    tiles.Add(rgb);
                    montageKeys.Add(key);
                    montageIndex.Add($"{tiles.Count - 1}: {key}");
                }
            }
            else
            {
                var path = Path.Combine(outDir, SafeFileName(key) + ".png");
                (rgb, isFullCube, holePixels) = RenderToBuffer(mesh, atlas, Side, 0);
                WritePng(path, Side, Side, rgb);
            }

            written++;

            if (mesh.Indices.Length == 0) emptyMeshes.Add(key);

            if (isFullCube && holePixels > 4) hollowCubes.Add($"{key} holes={holePixels}");
        }

        if (tiles is not null && montage is not null)
        {
            WriteMontage(montage.OutPath, tiles, cell, montageKeys, montage.Names, montage.Gap);
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

    // 8 个包围盒角在屏幕两轴上的最大投影半展宽。两轴共用一个缩放（保形），所以取两者的大值，
    // 横向与纵向都不会溢出。半径必须按屏幕展宽算，不能拿世界空间半径（对角半径 0.87、
    // 或干脆写 0.5）——等距投影下单位立方体的纵向展宽是 0.79，写 0.5 必然切掉顶/底面。
    private static float FitRadius(Vector3 min, Vector3 max, Vector3 centre, Vector3 right, Vector3 up)
    {
        var radius = 0f;
        for (var corner = 0; corner < 8; corner++)
        {
            Vector3 c = new(
                (corner & 1) == 0 ? min.X : max.X,
                (corner & 2) == 0 ? min.Y : max.Y,
                (corner & 4) == 0 ? min.Z : max.Z);
            var rel = c - centre;
            radius = MathF.Max(radius,
                MathF.Max(MathF.Abs(Vector3.Dot(rel, right)), MathF.Abs(Vector3.Dot(rel, up))));
        }

        return radius;
    }

    // region 模式入口：按传入的 region 包围盒取景，不做自适应。
    private static byte[] RenderRegionToBuffer(MeshData mesh, TextureAtlas atlas, int side, Vector3 centre,
        float radius)
    {
        return RenderGeneral(mesh, atlas, side, centre, radius, false, false).Rgb;
    }

    // 单方块入口：取景由网格自身的包围盒推得（autoFit）。
    // 曾经写死 centre=(0.5,0.5,0.5)、radius=0.5，等于假定「球半径 0.5」——但等距投影下
    // 单位立方体的屏纵向半展宽是 0.79（横向 0.71），顶面/底面本就溢出格子被切；高度
    // 1.5 的栅栏、2 的门、1/16 的地毯更惨。自适应后每个方块都完整装进格子。
    private static (byte[] Rgb, bool IsFullCube, int HolePixels) RenderToBuffer(MeshData mesh, TextureAtlas atlas,
        int side, byte background)
    {
        return RenderGeneral(mesh, atlas, side, default, 0f, true, true, background);
    }

    // 通用软件光栅化：等距视角（yaw 45°、pitch≈33.7°），正交投影。
    // autoFit=true 时 centre/radius 由网格包围盒推得（单方块用）；false 时按传入值取景（region 用）。
    // keepFullCubeInfo 只在单方块模式下有意义，region 模式恒 false。
    private static (byte[] Rgb, bool IsFullCube, int HolePixels) RenderGeneral(
        MeshData mesh, TextureAtlas atlas, int side, Vector3 centre, float radius, bool autoFit,
        bool keepFullCubeInfo, byte background = 0)
    {
        var rgb = new byte[side * side * 3];
        if (background != 0)
            for (var i = 0; i < side * side; i++)
            {
                rgb[i * 3 + 0] = background;
                rgb[i * 3 + 1] = background;
                rgb[i * 3 + 2] = background;
            }

        var zbuf = new float[side * side];
        Array.Fill(zbuf, float.NegativeInfinity);

        var vertexCount = mesh.Vertices.Length / MeshData.FloatsPerVertex;
        if (vertexCount == 0) return (rgb, false, 0);

        // right = up × eyeDir（lookAt 的 x 轴）；up 由两者叉积闭合。
        var right = Vector3.Normalize(Vector3.Cross(new Vector3(0, 1, 0), EyeDir));
        var up = Vector3.Cross(EyeDir, right);

        if (autoFit)
        {
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            for (var i = 0; i < vertexCount; i++)
            {
                Vector3 p = new(
                    mesh.Vertices[i * MeshData.FloatsPerVertex + MeshData.PositionOffset],
                    mesh.Vertices[i * MeshData.FloatsPerVertex + MeshData.PositionOffset + 1],
                    mesh.Vertices[i * MeshData.FloatsPerVertex + MeshData.PositionOffset + 2]);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }

            centre = (min + max) * 0.5f;
            // 尺度下限锚在单位立方体的实际投影展宽上：≤1 格的方块彼此保持同尺（矮的看起来
            // 就是矮的、小的就是小的），只有更高的方块（栅栏 1.5、门 2）才为装下而整体缩小。
            // 没有这个下限，火把/按钮会被放大到和方块一样大，失去尺寸参照。
            radius = MathF.Max(
                FitRadius(Vector3.Zero, Vector3.One, new Vector3(0.5f), right, up),
                FitRadius(min, max, centre, right, up));
        }

        // 边距按比例留（5%）：montage 的 96px 格和单图的 192px 共用同一个取景逻辑。
        var scale = side / 2f * 0.9f / MathF.Max(radius, 1e-3f);

        Span<Vector3> screen = stackalloc Vector3[vertexCount];
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

    // 把若干已渲染的方格拼成一张网格大图。列数按「总数开方向上取整」，接近正方最好扫视。
    // 背景与格子同色、格间距默认 0：格子之间没有缝也没有边框，方块之间靠自身的留白分开。
    // --names 时每格左侧让出一条标签列（状态键有自己的地盘，不压方块、也不截断）；
    // 标签列同时把相邻两格的方块隔开，所以没有间距也不会挤在一起。
    private static void WriteMontage(string path, List<byte[]> tiles, int cell, List<string>? keys = null,
        bool names = false, int gap = 0)
    {
        var labelled = names && keys is not null;
        var labelW = labelled ? Math.Max(48, cell * 5 / 8) : 0;
        var tileW = cell + labelW;

        var columns = Math.Max(1, (int)MathF.Ceiling(MathF.Sqrt(tiles.Count)));
        var rows = Math.Max(1, (int)MathF.Ceiling(tiles.Count / (float)columns));
        var width = columns * tileW + (columns + 1) * gap;
        var height = rows * cell + (rows + 1) * gap;
        var rgb = new byte[width * height * 3];
        for (var i = 0; i < width * height; i++)
        {
            rgb[i * 3 + 0] = BackgroundR;
            rgb[i * 3 + 1] = BackgroundG;
            rgb[i * 3 + 2] = BackgroundB;
        }

        for (var tile = 0; tile < tiles.Count; tile++)
        {
            var tx = tile % columns;
            var ty = tile / columns;
            var tileX = gap + tx * (tileW + gap);
            var tileY = gap + ty * (cell + gap);
            var originX = tileX + labelW;
            var pixels = tiles[tile];
            for (var y = 0; y < cell; y++)
            {
                var source = y * cell * 3;
                var target = ((tileY + y) * width + originX) * 3;
                Buffer.BlockCopy(pixels, source, rgb, target, cell * 3);
            }

            if (labelled)
                DrawLabel(rgb, width, tileX + 3, tileY + 3, labelW - 8, cell - 6, keys![tile]);
        }

        WritePng(path, width, height, rgb);
    }

    // 状态键标注：系统字体（GDI 默认 UI 字体 Segoe UI）、纯黄字、无底条。放在每格左侧的
    // 标签列里——不压方块、不截断（按列宽断行，字号在列宽×列高内自适应）。
    private static void DrawLabel(byte[] rgb, int imageWidth, int x0, int y0, int maxWidth, int maxHeight,
        string key)
    {
        // minecraft: 在 montage 语境是噪音，截掉省宽度。
        var text = key.StartsWith("minecraft:", StringComparison.Ordinal) ? key["minecraft:".Length..] : key;
        GdiText.DrawInto(rgb, imageWidth, x0, y0, maxWidth, maxHeight, text);
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

    private static void SetPx(byte[] rgb, int imageWidth, int x, int y, byte r, byte g, byte b)
    {
        if (x < 0 || y < 0) return;
        var i = (y * imageWidth + x) * 3;
        if (i + 2 >= rgb.Length) return;
        rgb[i] = r;
        rgb[i + 1] = g;
        rgb[i + 2] = b;
    }

    // GDI 文本绘制。不用 System.Drawing：Meshing 被 ShellPreview 以 NativeAOT 引用，
    // 托管 GDI+ 对 AOT/裁剪不友好，而目标只有 Windows，P/Invoke 更轻更可控。
    // 渲染到 32bpp DIB、以品红为透明键——字体必须关抗锯齿（NONANTIALIASED），
    // 否则字边混出的品红-黄过渡色会在方块上留一道粉边。
    private static class GdiText
    {
        private const string Gdi = "gdi32.dll";
        private const string User = "user32.dll";

        private const int BkTransparent = 1;
        private const uint TextYellow = 0x0000FFFF; // COLORREF 0x00BBGGRR
        private const byte KeyB = 255;
        private const byte KeyG = 0;
        private const byte KeyR = 255;
        private const uint NonAntialiased = 3;

        private static readonly IntPtr Dc = CreateCompatibleDC(IntPtr.Zero);
        private static IntPtr _font;
        private static int _fontPx;

        // 每种磅值只建一次字体；旧对象不回收——整轮最多十来个尺寸，进程随即退出。
        private static void SelectFont(int px)
        {
            if (_fontPx == px && _font != IntPtr.Zero) return;
            _font = CreateFontW(-px, 0, 0, 0, 400, 0, 0, 0, 0, 0, 0, NonAntialiased, 0, "Segoe UI");
            SelectObject(Dc, _font);
            _fontPx = px;
        }

        private static int Measure(string text) =>
            text.Length == 0 ? 0 : (GetTextExtentPoint32W(Dc, text, text.Length, out var size) ? size.Width : 0);

        // 断行：优先在 [ ] , : _ - = 后断（属性段、命名段天然成组），段内超宽按实测宽度硬切。
        // 下划线与等号也算边界：mod 的方块名多是 snake_case（linear_chassis），属性是
        // k=v（facing=south），只按标点断会把名字/属性从中间劈开，读起来很难受。
        private static List<string> Wrap(string text, int maxWidth)
        {
            List<string> tokens = [];
            var current = "";
            foreach (var ch in text)
            {
                current += ch;
                if (ch is ',' or '[' or ']' or ':' or '_' or '-' or '=')
                {
                    tokens.Add(current);
                    current = "";
                }
            }

            if (current.Length > 0) tokens.Add(current);

            List<string> lines = [];
            var line = "";
            foreach (var token in tokens)
            {
                var rest = token;
                if (Measure(rest) > maxWidth && line.Length > 0)
                {
                    lines.Add(line);
                    line = "";
                }

                while (rest.Length > 1 && Measure(rest) > maxWidth)
                {
                    var cut = rest.Length - 1;
                    while (cut > 1 && Measure(rest[..cut]) > maxWidth) cut--;
                    lines.Add(rest[..cut]);
                    rest = rest[cut..];
                }

                if (line.Length > 0 && Measure(line + rest) > maxWidth)
                {
                    lines.Add(line);
                    line = rest;
                }
                else
                {
                    line += rest;
                }
            }

            if (line.Length > 0) lines.Add(line);
            return lines;
        }

        // 文本按列宽自适应字号（列宽才是真约束：字号定得比列能容纳的大，只会立刻缩回来）、
        // 断行后按透明键叠进目标 RGB 缓冲。
        public static void DrawInto(byte[] rgb, int imageWidth, int x0, int y0, int maxWidth, int maxHeight,
            string text)
        {
            if (maxWidth < 8 || maxHeight < 8) return;

            var fontPx = Math.Clamp(maxWidth / 5, 11, 26);
            List<string> lines;
            int boxW;
            int boxH;
            while (true)
            {
                SelectFont(fontPx);
                lines = Wrap(text, maxWidth);
                boxH = lines.Count * (fontPx + 2);
                boxW = Math.Min(maxWidth, lines.Max(Measure));
                if (boxH <= maxHeight || fontPx <= 11) break;
                fontPx -= 2;
            }

            var info = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = boxW,
                    Height = -boxH, // 负高 = 自上而下，省一次翻转
                    Planes = 1,
                    BitCount = 32,
                },
            };

            var dib = CreateDIBSection(Dc, ref info, 0, out var bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero) return;

            var previous = SelectObject(Dc, dib);
            var stride = boxW * 4;
            var buffer = new byte[stride * boxH];
            for (var i = 0; i < buffer.Length; i += 4)
            {
                buffer[i + 0] = KeyB;
                buffer[i + 1] = KeyG;
                buffer[i + 2] = KeyR;
            }

            Marshal.Copy(buffer, 0, bits, buffer.Length);
            SetBkMode(Dc, BkTransparent);
            SetTextColor(Dc, TextYellow);
            for (var i = 0; i < lines.Count; i++)
                TextOutW(Dc, 0, i * (fontPx + 2), lines[i], lines[i].Length);

            Marshal.Copy(bits, buffer, 0, buffer.Length);
            SelectObject(Dc, previous);
            DeleteObject(dib);

            for (var y = 0; y < boxH; y++)
            for (var x = 0; x < boxW; x++)
            {
                var i = y * stride + x * 4;
                if (buffer[i] == KeyB && buffer[i + 1] == KeyG && buffer[i + 2] == KeyR) continue;
                SetPx(rgb, imageWidth, x0 + x, y0 + y, buffer[i + 2], buffer[i + 1], buffer[i]);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfoHeader
        {
            public uint Size;
            public int Width;
            public int Height;
            public ushort Planes;
            public ushort BitCount;
            public uint Compression;
            public uint SizeImage;
            public int XPelsPerMeter;
            public int YPelsPerMeter;
            public uint ClrUsed;
            public uint ClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfo
        {
            public BitmapInfoHeader Header;
            public uint Colors;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TextSize
        {
            public int Width;
            public int Height;
        }

        [DllImport(Gdi)] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport(Gdi)] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfo info,
            uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport(Gdi)] private static extern IntPtr CreateFontW(int height, int width, int escapement,
            int orientation, int weight, uint italic, uint underline, uint strikeOut, uint charSet,
            uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);
        [DllImport(Gdi)] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport(Gdi)] private static extern bool DeleteObject(IntPtr obj);
        [DllImport(Gdi)] private static extern uint SetTextColor(IntPtr hdc, uint color);
        [DllImport(Gdi)] private static extern int SetBkMode(IntPtr hdc, int mode);
        [DllImport(Gdi, CharSet = CharSet.Unicode)] private static extern bool TextOutW(IntPtr hdc, int x, int y,
            string text, int length);
        [DllImport(Gdi, CharSet = CharSet.Unicode)] private static extern bool GetTextExtentPoint32W(IntPtr hdc,
            string text, int length, out TextSize size);
    }

    private sealed record MontageOptions(string OutPath, string Filter, int Cell, int Limit, bool Names, int Gap);
}
