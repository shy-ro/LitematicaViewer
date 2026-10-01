using System.Collections.Immutable;
using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using LitematicaViewer.Assets;
using LitematicaViewer.Core.Model;

namespace LitematicaViewer.Meshing;

public sealed partial class BlockMeshBuilder
{
    private void CollectSpecialSprites(IEnumerable<LitematicRegion> source, HashSet<string> output, PackStack? packs,
        ICollection<GeneratedSprite>? generatedSprites)
    {
        var regions = source as IReadOnlyCollection<LitematicRegion> ?? source.ToArray();
        BitmapFont? font = null;
        if (packs is not null && generatedSprites is not null && regions.Any(static region =>
                region.BlockEntities.Values.Any(static entity => IsSignEntity(entity.Id))))
            font = BitmapFont.Load(packs);

        foreach (var region in regions)
        {
            var blockEntitySummary = string.Join(',', region.BlockEntities.Values.GroupBy(static item => item.Id)
                .OrderBy(static group => group.Key, StringComparer.Ordinal)
                .Select(static group => $"{group.Key}={group.Count()}"));
            var entitySummary = string.Join(',', region.Entities.GroupBy(static item => item.Id)
                .OrderBy(static group => group.Key, StringComparer.Ordinal)
                .Select(static group => $"{group.Key}={group.Count()}"));
            Debug.WriteLine($"[MESH][entity.audit] region={region.Name} blockEntities=[{blockEntitySummary}] entities=[{entitySummary}]");
            foreach (var blockEntity in region.BlockEntities.Values)
            {
                if (IsSignEntity(blockEntity.Id) && font is not null && generatedSprites is not null)
                {
                    foreach (var side in ReadSignText(blockEntity.Data))
                    {
                        if (side.Lines.All(string.IsNullOrEmpty)) continue;
                        var spriteId = SignSpriteId(region, blockEntity.Position, side);
                        output.Add(spriteId);
                        generatedSprites.Add(font.Render(spriteId, side.Lines, side.Color, side.Glowing));
                    }
                }

                if (blockEntity.Id == "minecraft:banner" && packs is not null && generatedSprites is not null &&
                    blockEntity.Data.GetSequence("Patterns") is { Values.Length: > 0 } patterns)
                {
                    List<(string Sprite, uint Rgb)> layers = [];
                    foreach (var pattern in patterns.Values.OfType<NbtMap>())
                        if (pattern.GetString("Pattern") is { } code && BannerPatternSprite(code) is { } sprite)
                            layers.Add((sprite, DyeColor((int)(pattern.GetInt64("Color") ?? 15))));
                    if (layers.Count > 0)
                    {
                        var spriteId = BannerSpriteId(region, blockEntity.Position, layers);
                        output.Add(spriteId);
                        generatedSprites.Add(LayeredTextureComposer.RenderTintedLayers(packs, spriteId, layers));
                    }
                }

                if (blockEntity.Id == "minecraft:decorated_pot" && ReadSherds(blockEntity.Data) is { Length: > 0 } sherds)
                    foreach (var sherd in sherds) output.Add(PotterySprite(sherd));
            }

            foreach (var entity in region.Entities)
            {
                if (entity.Id is "minecraft:item_frame" or "minecraft:glow_item_frame")
                {
                    output.Add("minecraft:block/oak_planks");
                    if (entity.Data.GetMap("Item")?.GetString("id") is { } itemId)
                        CollectItemSprite(itemId, output);
                }
                else if (entity.Id == "minecraft:armor_stand")
                {
                    output.Add("minecraft:entity/armorstand/wood");
                }
                else if (entity.Id == "minecraft:painting")
                {
                    output.Add(PaintingSprite(entity.Data));
                    output.Add("minecraft:painting/back");
                }
                else if (IsMinecart(entity.Id))
                {
                    output.Add("minecraft:entity/minecart");
                    var cargo = MinecartCargoBlock(entity.Id);
                    if (cargo is not null) CollectBlockSprites(cargo, output);
                }
                else if (entity.Id == "minecraft:item")
                {
                    if (entity.Data.GetMap("Item")?.GetString("id") is { } itemId) CollectItemSprite(itemId, output);
                }
                else if (IsBoat(entity.Id))
                {
                    output.Add(BoatSprite(entity));
                    if (entity.Id.Contains("chest", StringComparison.Ordinal))
                        CollectBlockSprites("minecraft:chest[facing=north,type=single,waterlogged=false]", output);
                }
                else if (EntitySprite(entity) is { } entitySprite)
                {
                    output.Add(entitySprite);
                }
                else
                {
                    Debug.WriteLine($"[MESH][entity.audit] unsupported entity id={entity.Id} pos={entity.Position} " +
                                    $"keys=[{string.Join(',', entity.Data.Values.Keys)}]");
                }
            }
        }
    }

    private void CollectItemSprite(string itemId, HashSet<string> output)
    {
        var resolved = _resolver.Resolve(itemId);
        var any = false;
        foreach (var sprite in resolved.Variants.SelectMany(static variant => variant.Model.Elements)
                     .SelectMany(static element => element.Faces).Select(static face => face.Sprite))
        {
            output.Add(sprite);
            any = true;
        }

        if (!any)
        {
            var colon = itemId.IndexOf(':');
            var ns = colon < 0 ? "minecraft" : itemId[..colon];
            var path = colon < 0 ? itemId : itemId[(colon + 1)..];
            output.Add($"{ns}:item/{path}");
        }
    }

    private void CollectBlockSprites(string blockState, HashSet<string> output)
    {
        foreach (var sprite in _resolver.Resolve(blockState).Variants.SelectMany(static variant => variant.Model.Elements)
                     .SelectMany(static element => element.Faces).Select(static face => face.Sprite))
            if (sprite.Length > 0) output.Add(sprite);
    }

    private void EmitBlockEntityOverlays(LitematicRegion region, List<float> vertices, List<int> indices)
    {
        foreach (var blockEntity in region.BlockEntities.Values)
        {
            var local = NormalizeBlockEntityPosition(region, blockEntity.Position);
            var size = region.Bounds.Size;
            if (local.X < 0 || local.Y < 0 || local.Z < 0 || local.X >= size.X || local.Y >= size.Y || local.Z >= size.Z)
                continue;
            var state = region.GetStateAt(region.ToIndex(local.X, local.Y, local.Z));
            if (IsSignEntity(blockEntity.Id))
            {
                foreach (var side in ReadSignText(blockEntity.Data))
                {
                    if (side.Lines.All(string.IsNullOrEmpty)) continue;
                    var spriteId = SignSpriteId(region, blockEntity.Position, side);
                    if (!_atlas.TryGetRect(spriteId, out var rect))
                    {
                        _specialSpritesMissing++;
                        Debug.WriteLine($"[MESH][entity.audit] missingSprite kind=sign side={side.Side} id={spriteId} pos={blockEntity.Position}");
                        continue;
                    }
                    EmitSignText(region, local, state, rect, side.Side == SignSide.Back, vertices, indices);
                    _specialEntitiesEmitted++;
                }
            }
            else if (blockEntity.Id == "minecraft:banner" && blockEntity.Data.GetSequence("Patterns") is { Values.Length: > 0 } patterns)
            {
                EmitBannerPatterns(region, blockEntity.Position, local, state, patterns, vertices, indices);
            }
            else if (blockEntity.Id == "minecraft:decorated_pot" && ReadSherds(blockEntity.Data) is { Length: > 0 } sherds)
            {
                EmitPotteryPatterns(region, local, sherds, vertices, indices);
            }
        }
    }

    private void EmitPotteryPatterns(LitematicRegion region, Vector3I local, string[] sherds,
        List<float> vertices, List<int> indices)
    {
        var origin = new Vector3(region.Bounds.Min.X + local.X, region.Bounds.Min.Y + local.Y,
            region.Bounds.Min.Z + local.Z);
        var centers = new[]
        {
            (new Vector3(0.5f, 0.53f, 0.817f), Vector3.UnitZ),
            (new Vector3(0.183f, 0.53f, 0.5f), -Vector3.UnitX),
            (new Vector3(0.5f, 0.53f, 0.183f), -Vector3.UnitZ),
            (new Vector3(0.817f, 0.53f, 0.5f), Vector3.UnitX)
        };
        for (var i = 0; i < Math.Min(4, sherds.Length); i++)
            if (_atlas.TryGetRect(PotterySprite(sherds[i]), out var rect))
                EmitOrientedPlane(vertices, indices, origin, centers[i].Item1, centers[i].Item2, 0.31f, 0.31f, rect, 0f);
    }

    private void EmitSignText(LitematicRegion region, Vector3I local, BlockStateDefinition state, SpriteRect rect,
        bool back,
        List<float> vertices, List<int> indices)
    {
        // 文字片从牌面盒推出来（SignGeometry 与 Assets 侧同源）：牌面挪了文字跟着挪，
        // 墙牌的贴墙偏移也不用在这里再抄一遍。
        var hanging = state.Name.Contains("hanging_sign", StringComparison.Ordinal);
        var wall = state.Name.Contains("wall_", StringComparison.Ordinal);
        var (from, to) = hanging ? SignGeometry.HangingBoard() : SignGeometry.Board(wall);
        // 文字贴图是 192x96 的四行点阵，片面必须保持 2:1，否则笔画横向压扁。
        var height = MathF.Min(to.Y - from.Y - 1f, 7f);
        var width = MathF.Min(2f * height, to.X - from.X - 2f);
        height = width / 2f;
        var center = (from + to) * 0.5f;
        var x0 = (center.X - width / 2f) / 16f;
        var x1 = (center.X + width / 2f) / 16f;
        var y0 = (center.Y - height / 2f) / 16f;
        var y1 = (center.Y + height / 2f) / 16f;
        // 离面前 0.2px：贴死会和牌面抢深度。
        var z = (back ? from.Z - 0.2f : to.Z + 0.2f) / 16f;
        var points = new[]
        {
            new Vector3(back ? x1 : x0, y1, z), new Vector3(back ? x0 : x1, y1, z),
            new Vector3(back ? x0 : x1, y0, z), new Vector3(back ? x1 : x0, y0, z)
        };
        var degrees = SignGeometry.BlockStateAngle(state.Properties);
        // 朝向对不上时先看这一行：props 为空说明调色板把属性塞进了 Name 而不是 Properties，
        // 所有带属性的方块会静默退回默认朝向（牌子永远朝南、箱子永远朝北）。
        Debug.WriteLine($"[MESH][sign] local={local} name={state.Name} " +
                        $"props=[{string.Join(',', state.Properties.Select(static p => $"{p.Key}={p.Value}"))}] " +
                        $"back={back} degrees={degrees} boxZ={from.Z:F2}..{to.Z:F2} z={z:F4}");
        for (var i = 0; i < points.Length; i++) points[i] = RotateAround(points[i], 1, degrees, new Vector3(0.5f));
        var normal = Rotate(new Vector3(0, 0, back ? -1 : 1), 1, degrees);
        var origin = new Vector3(region.Bounds.Min.X + local.X, region.Bounds.Min.Y + local.Y,
            region.Bounds.Min.Z + local.Z);
        EmitCustomQuad(vertices, indices, origin, points, normal, rect, 0f, 1f);
    }

    private void EmitBannerPatterns(LitematicRegion region, Vector3I storedPosition, Vector3I local,
        BlockStateDefinition state, NbtSequence patterns, List<float> vertices, List<int> indices)
    {
        var degrees = SignRotation(state.Properties);
        List<(string Sprite, uint Rgb)> layers = [];
        foreach (var pattern in patterns.Values.OfType<NbtMap>())
            if (pattern.GetString("Pattern") is { } code && BannerPatternSprite(code) is { } sprite)
                layers.Add((sprite, DyeColor((int)(pattern.GetInt64("Color") ?? 15))));
        var spriteId = BannerSpriteId(region, storedPosition, layers);
        if (!_atlas.TryGetRect(spriteId, out var rect)) return;
        const float z = 0.563f;
        var points = new[]
        {
            new Vector3(0.04f, 0.94f, z), new Vector3(0.96f, 0.94f, z),
            new Vector3(0.96f, 0.12f, z), new Vector3(0.04f, 0.12f, z)
        };
        for (var i = 0; i < points.Length; i++) points[i] = RotateAround(points[i], 1, degrees, new Vector3(0.5f));
        var origin = new Vector3(region.Bounds.Min.X + local.X, region.Bounds.Min.Y + local.Y,
            region.Bounds.Min.Z + local.Z);
        EmitCustomQuad(vertices, indices, origin, points, Rotate(new Vector3(0, 0, 1), 1, degrees), rect, 0f, 1f);
    }

    private void EmitEntities(LitematicRegion region, List<float> vertices, List<int> indices)
    {
        Dictionary<string, (int Count, int Faces)> emitted = new(StringComparer.Ordinal);
        Dictionary<string, int> skipped = new(StringComparer.Ordinal);
        foreach (var entity in region.Entities)
        {
            var before = indices.Count;
            var origin = new Vector3(region.Bounds.Min.X, region.Bounds.Min.Y, region.Bounds.Min.Z) + entity.Position;
            if (entity.Id is "minecraft:item_frame" or "minecraft:glow_item_frame")
                EmitItemFrame(entity, origin, vertices, indices);
            else if (entity.Id == "minecraft:armor_stand")
                EmitArmorStand(entity, origin, vertices, indices);
            else if (entity.Id == "minecraft:painting")
                EmitPainting(entity, origin, vertices, indices);
            else if (IsMinecart(entity.Id))
                EmitMinecart(entity, origin, vertices, indices);
            else if (entity.Id == "minecraft:item")
                EmitDroppedItem(entity, origin, vertices, indices);
            else if (IsBoat(entity.Id))
                EmitBoat(entity, origin, vertices, indices);
            else if (EntitySprite(entity) is { } entitySprite)
                EmitStaticMob(entity, origin, entitySprite, vertices, indices);
            else
            {
                _specialEntitiesSkipped++;
                skipped[entity.Id] = skipped.GetValueOrDefault(entity.Id) + 1;
                Debug.WriteLine($"[MESH][entity.audit] skipped entity geometry id={entity.Id} pos={entity.Position}");
            }
            if (indices.Count > before)
            {
                var current = emitted.GetValueOrDefault(entity.Id);
                emitted[entity.Id] = (current.Count + 1, current.Faces + (indices.Count - before) / 6);
            }
        }
        if (region.Entities.Length > 0)
            Debug.WriteLine($"[MESH][entity.audit] geometry region={region.Name} emitted=[" +
                            string.Join(',', emitted.OrderBy(static pair => pair.Key)
                                .Select(static pair => $"{pair.Key}={pair.Value.Count}/{pair.Value.Faces}f")) +
                            $"] skipped=[{string.Join(',', skipped.OrderBy(static pair => pair.Key).Select(static pair => $"{pair.Key}={pair.Value}"))}]");
    }

    private void EmitStaticMob(EntityData entity, Vector3 origin, string sprite, List<float> vertices, List<int> indices)
    {
        if (!_atlas.TryGetRect(sprite, out var rect))
        {
            _specialSpritesMissing++;
            Debug.WriteLine($"[MESH][entity.audit] missingSprite kind=mob id={entity.Id} sprite={sprite} pos={entity.Position}");
            return;
        }
        var yaw = (float)(entity.Data.GetSequence("Rotation")?.Values.FirstOrDefault()?.AsDouble() ?? 0);
        var baseOrigin = origin - new Vector3(0.5f, 0, 0.5f);
        var boxes = MobBoxes(entity.Id);
        foreach (var (from, to) in boxes) EmitCustomBox(vertices, indices, baseOrigin, from, to, rect, yaw);
        _specialEntitiesEmitted++;
    }

    private static IReadOnlyList<(Vector3 From, Vector3 To)> MobBoxes(string id) => id switch
    {
        "minecraft:iron_golem" =>
        [
            (new(0.18f, 1.75f, 0.18f), new(0.82f, 2.45f, 0.82f)),
            (new(0.12f, 0.75f, 0.24f), new(0.88f, 1.78f, 0.76f)),
            (new(-0.02f, 0.45f, 0.31f), new(0.14f, 1.72f, 0.69f)),
            (new(0.86f, 0.45f, 0.31f), new(1.02f, 1.72f, 0.69f)),
            (new(0.23f, 0, 0.31f), new(0.43f, 0.82f, 0.69f)),
            (new(0.57f, 0, 0.31f), new(0.77f, 0.82f, 0.69f))
        ],
        "minecraft:snow_golem" =>
        [
            (new(0.25f, 1.38f, 0.25f), new(0.75f, 1.88f, 0.75f)),
            (new(0.18f, 0.62f, 0.18f), new(0.82f, 1.42f, 0.82f)),
            (new(0.08f, 0, 0.08f), new(0.92f, 0.72f, 0.92f)),
            (new(-0.25f, 0.92f, 0.46f), new(1.25f, 1.02f, 0.54f))
        ],
        "minecraft:pig" =>
        [
            (new(0.08f, 0.42f, 0.18f), new(0.92f, 1.08f, 0.82f)),
            (new(0.18f, 0.48f, -0.28f), new(0.82f, 1.08f, 0.22f)),
            (new(0.14f, 0, 0.2f), new(0.3f, 0.5f, 0.38f)),
            (new(0.7f, 0, 0.2f), new(0.86f, 0.5f, 0.38f)),
            (new(0.14f, 0, 0.62f), new(0.3f, 0.5f, 0.8f)),
            (new(0.7f, 0, 0.62f), new(0.86f, 0.5f, 0.8f))
        ],
        "minecraft:shulker" =>
        [
            (new(0.06f, 0, 0.06f), new(0.94f, 0.48f, 0.94f)),
            (new(0.02f, 0.48f, 0.02f), new(0.98f, 0.96f, 0.98f))
        ],
        "minecraft:bat" =>
        [
            (new(0.34f, 0.72f, 0.34f), new(0.66f, 1.18f, 0.66f)),
            (new(-0.35f, 0.76f, 0.45f), new(0.36f, 1.08f, 0.55f)),
            (new(0.64f, 0.76f, 0.45f), new(1.35f, 1.08f, 0.55f))
        ],
        _ =>
        [
            (new(0.28f, 1.48f, 0.28f), new(0.72f, 1.92f, 0.72f)),
            (new(0.3f, 0.72f, 0.34f), new(0.7f, 1.5f, 0.66f)),
            (new(0.14f, 0.7f, 0.39f), new(0.3f, 1.48f, 0.61f)),
            (new(0.7f, 0.7f, 0.39f), new(0.86f, 1.48f, 0.61f)),
            (new(0.31f, 0, 0.38f), new(0.47f, 0.76f, 0.62f)),
            (new(0.53f, 0, 0.38f), new(0.69f, 0.76f, 0.62f))
        ]
    };

    private void EmitDroppedItem(EntityData entity, Vector3 origin, List<float> vertices, List<int> indices)
    {
        var itemId = entity.Data.GetMap("Item")?.GetString("id");
        var sprite = itemId is null ? null : FirstItemSprite(itemId);
        if (sprite is null || !_atlas.TryGetRect(sprite, out var rect))
        {
            _specialSpritesMissing++;
            Debug.WriteLine($"[MESH][entity.audit] missingSprite kind=dropped_item item={itemId ?? "-"} pos={entity.Position}");
            return;
        }
        var yaw = (float)(entity.Data.GetSequence("Rotation")?.Values.FirstOrDefault()?.AsDouble() ?? 0);
        var radians = -yaw * MathF.PI / 180f;
        var normalA = Vector3.Normalize(new Vector3(MathF.Sin(radians), 0, MathF.Cos(radians)));
        var normalB = Vector3.Normalize(new Vector3(normalA.Z, 0, -normalA.X));
        EmitOrientedPlane(vertices, indices, origin, new Vector3(0, 0.22f, 0), normalA, 0.24f, 0.24f, rect, 0f);
        EmitOrientedPlane(vertices, indices, origin, new Vector3(0, 0.22f, 0), normalB, 0.24f, 0.24f, rect, 0f);
        _specialEntitiesEmitted++;
    }

    private void EmitBoat(EntityData entity, Vector3 origin, List<float> vertices, List<int> indices)
    {
        var sprite = BoatSprite(entity);
        if (!_atlas.TryGetRect(sprite, out var rect))
        {
            _specialSpritesMissing++;
            Debug.WriteLine($"[MESH][entity.audit] missingSprite kind=boat sprite={sprite} pos={entity.Position}");
            return;
        }
        var yaw = (float)(entity.Data.GetSequence("Rotation")?.Values.FirstOrDefault()?.AsDouble() ?? 0);
        var baseOrigin = origin - new Vector3(0.5f, 0, 0.5f);
        EmitCustomBox(vertices, indices, baseOrigin, new Vector3(-0.18f, 0.05f, 0.02f),
            new Vector3(1.18f, 0.22f, 0.98f), rect, yaw);
        EmitCustomBox(vertices, indices, baseOrigin, new Vector3(-0.18f, 0.18f, 0.02f),
            new Vector3(-0.02f, 0.62f, 0.98f), rect, yaw);
        EmitCustomBox(vertices, indices, baseOrigin, new Vector3(1.02f, 0.18f, 0.02f),
            new Vector3(1.18f, 0.62f, 0.98f), rect, yaw);
        EmitCustomBox(vertices, indices, baseOrigin, new Vector3(-0.02f, 0.18f, 0.02f),
            new Vector3(1.02f, 0.52f, 0.16f), rect, yaw);
        EmitCustomBox(vertices, indices, baseOrigin, new Vector3(-0.02f, 0.18f, 0.84f),
            new Vector3(1.02f, 0.52f, 0.98f), rect, yaw);
        if (entity.Id.Contains("chest", StringComparison.Ordinal) &&
            FirstBlockSprite("minecraft:chest[facing=north,type=single,waterlogged=false]") is { } chestSprite &&
            _atlas.TryGetRect(chestSprite, out var chestRect))
            EmitCustomBox(vertices, indices, baseOrigin, new Vector3(0.26f, 0.22f, 0.24f),
                new Vector3(0.74f, 0.72f, 0.76f), chestRect, yaw);
        _specialEntitiesEmitted++;
    }

    private void EmitMinecart(EntityData entity, Vector3 origin, List<float> vertices, List<int> indices)
    {
        if (!_atlas.TryGetRect("minecraft:entity/minecart", out var cartRect))
        {
            _specialSpritesMissing++;
            Debug.WriteLine($"[MESH][entity.audit] missingSprite kind=minecart id={entity.Id} pos={entity.Position}");
            return;
        }

        var yaw = (float)(entity.Data.GetSequence("Rotation")?.Values.FirstOrDefault()?.AsDouble() ?? 0);
        var baseOrigin = origin - new Vector3(0.5f, 0, 0.5f);
        EmitCustomBox(vertices, indices, baseOrigin, new Vector3(0.03f, 0.08f, 0.03f),
            new Vector3(0.97f, 0.48f, 0.97f), cartRect, yaw);
        // 内腔用较小的反差盒表达，静态远景中比只画一张薄片更容易辨认朝向。
        EmitCustomBox(vertices, indices, baseOrigin, new Vector3(0.12f, 0.32f, 0.12f),
            new Vector3(0.88f, 0.56f, 0.88f), cartRect, yaw);

        if (MinecartCargoBlock(entity.Id) is { } cargo)
        {
            var sprite = FirstBlockSprite(cargo);
            if (sprite is not null && _atlas.TryGetRect(sprite, out var cargoRect))
                EmitCustomBox(vertices, indices, baseOrigin, new Vector3(0.18f, 0.42f, 0.18f),
                    new Vector3(0.82f, 1.05f, 0.82f), cargoRect, yaw);
        }
        _specialEntitiesEmitted++;
    }

    private void EmitPainting(EntityData entity, Vector3 origin, List<float> vertices, List<int> indices)
    {
        var sprite = PaintingSprite(entity.Data);
        if (!_atlas.TryGetRect(sprite, out var front) || !_atlas.TryGetRect("minecraft:painting/back", out var back))
        {
            _specialSpritesMissing++;
            Debug.WriteLine($"[MESH][entity.audit] missingSprite kind=painting sprite={sprite} pos={entity.Position}");
            return;
        }

        var motive = PaintingMotive(entity.Data);
        var (width, height) = PaintingSize(motive);
        var facing = (int)(GetInt64(entity.Data, "Facing", "facing", "Direction") ?? 2);
        var normal = FacingVector(facing);
        if (MathF.Abs(normal.Y) > 0.1f) normal = -Vector3.UnitZ;
        var center = -normal * 0.016f;
        EmitOrientedPlane(vertices, indices, origin, center, normal, width * 0.5f, height * 0.5f, front, 0f);
        EmitOrientedPlane(vertices, indices, origin, center + normal * 0.032f, -normal, width * 0.5f, height * 0.5f, back, 0f);
        _specialEntitiesEmitted++;
    }

    private void EmitItemFrame(EntityData entity, Vector3 origin, List<float> vertices, List<int> indices)
    {
        if (!_atlas.TryGetRect("minecraft:block/oak_planks", out var frameRect)) return;
        var facing = (int)(entity.Data.GetInt64("Facing") ?? 2);
        var normal = FacingVector(facing);
        var center = new Vector3(0.5f) - normal * 0.49f;
        EmitOrientedPlane(vertices, indices, origin - new Vector3(0.5f), center, normal, 0.48f, 0.48f, frameRect, 0f);

        if (entity.Data.GetMap("Item")?.GetString("id") is not { } itemId) return;
        var sprite = FirstItemSprite(itemId);
        if (sprite is null || !_atlas.TryGetRect(sprite, out var itemRect)) return;
        EmitOrientedPlane(vertices, indices, origin - new Vector3(0.5f), center - normal * 0.012f, normal,
            0.31f, 0.31f, itemRect, 0f);
    }

    private void EmitArmorStand(EntityData entity, Vector3 origin, List<float> vertices, List<int> indices)
    {
        if (!_atlas.TryGetRect("minecraft:entity/armorstand/wood", out var rect)) return;
        var yaw = entity.Data.GetSequence("Rotation")?.Values.FirstOrDefault()?.AsDouble() ?? 0;
        var small = entity.Data.GetInt64("Small") == 1;
        var scale = small ? 0.5f : 1f;
        var pose = entity.Data.GetMap("Pose") ?? NbtMap.Empty;
        var parts = new List<(string Name, Vector3 From, Vector3 To, Vector3 Pivot)>
        {
            ("Body", new(0.46f, 0.66f, 0.46f), new(0.54f, 1.42f, 0.54f), new(0.5f, 1.12f, 0.5f)),
            ("Body", new(0.25f, 1.32f, 0.46f), new(0.75f, 1.42f, 0.54f), new(0.5f, 1.37f, 0.5f)),
            ("Body", new(0.31f, 0.66f, 0.46f), new(0.69f, 0.75f, 0.54f), new(0.5f, 0.705f, 0.5f)),
            ("Head", new(0.34f, 1.45f, 0.34f), new(0.66f, 1.78f, 0.66f), new(0.5f, 1.45f, 0.5f)),
            ("LeftLeg", new(0.34f, 0.06f, 0.45f), new(0.43f, 0.72f, 0.55f), new(0.385f, 0.72f, 0.5f)),
            ("RightLeg", new(0.57f, 0.06f, 0.45f), new(0.66f, 0.72f, 0.55f), new(0.615f, 0.72f, 0.5f))
        };
        if (entity.Data.GetInt64("NoBasePlate") != 1)
            parts.Insert(0, ("Base", new(0.18f, 0, 0.18f), new(0.82f, 0.06f, 0.82f), new(0.5f, 0, 0.5f)));
        if (entity.Data.GetInt64("ShowArms") == 1)
        {
            parts.Add(("LeftArm", new Vector3(0.18f, 0.72f, 0.45f), new Vector3(0.28f, 1.45f, 0.55f),
                new Vector3(0.23f, 1.4f, 0.5f)));
            parts.Add(("RightArm", new Vector3(0.72f, 0.72f, 0.45f), new Vector3(0.82f, 1.45f, 0.55f),
                new Vector3(0.77f, 1.4f, 0.5f)));
        }
        var baseOrigin = origin - new Vector3(0.5f, 0, 0.5f);
        foreach (var (name, from, to, pivot) in parts)
            EmitLimbBox(vertices, indices, baseOrigin, from * scale, to * scale, pivot * scale,
                RotationOf(pose, name), rect, (float)yaw);
        _specialEntitiesEmitted++;
    }

    private void EmitLimbBox(List<float> vertices, List<int> indices, Vector3 origin, Vector3 from, Vector3 to,
        Vector3 pivot, Vector3 rotation, SpriteRect rect, float yaw)
    {
        foreach (var (normal, points) in BoxFaces(from, to))
        {
            var rotated = points.Select(point => RotateEuler(point, pivot, rotation)).Select(point => RotateYaw(point, yaw))
                .ToArray();
            var rotatedNormal = RotateEulerDirection(normal, rotation);
            EmitCustomQuad(vertices, indices, origin, rotated, RotateYawDirection(rotatedNormal, yaw), rect, 0f, 1f);
        }
    }

    private static Vector3 RotationOf(NbtMap pose, string part)
    {
        if (pose.GetSequence(part) is not { Values.Length: >= 3 } values) return Vector3.Zero;
        return new Vector3((float)(values.Values[0].AsDouble() ?? 0), (float)(values.Values[1].AsDouble() ?? 0),
            (float)(values.Values[2].AsDouble() ?? 0));
    }

    private static Vector3 RotateEuler(Vector3 value, Vector3 pivot, Vector3 degrees)
    {
        var result = value - pivot;
        result = RotateRadians(result, Vector3.UnitX, degrees.X);
        result = RotateRadians(result, Vector3.UnitY, degrees.Y);
        result = RotateRadians(result, Vector3.UnitZ, degrees.Z);
        return result + pivot;
    }

    private static Vector3 RotateEulerDirection(Vector3 value, Vector3 degrees)
    {
        var result = RotateRadians(value, Vector3.UnitX, degrees.X);
        result = RotateRadians(result, Vector3.UnitY, degrees.Y);
        return RotateRadians(result, Vector3.UnitZ, degrees.Z);
    }

    private static Vector3 RotateRadians(Vector3 value, Vector3 axis, float degrees)
    {
        if (degrees == 0f) return value;
        return Vector3.Transform(value, Matrix4x4.CreateFromAxisAngle(axis, -degrees * MathF.PI / 180f));
    }

    private string? FirstItemSprite(string itemId)
    {
        var resolved = _resolver.Resolve(itemId);
        var sprite = resolved.Variants.SelectMany(static variant => variant.Model.Elements)
            .SelectMany(static element => element.Faces).Select(static face => face.Sprite).FirstOrDefault();
        if (sprite is not null) return sprite;
        var colon = itemId.IndexOf(':');
        var ns = colon < 0 ? "minecraft" : itemId[..colon];
        var path = colon < 0 ? itemId : itemId[(colon + 1)..];
        return $"{ns}:item/{path}";
    }

    private string? FirstBlockSprite(string blockState) => _resolver.Resolve(blockState).Variants
        .SelectMany(static variant => variant.Model.Elements).SelectMany(static element => element.Faces)
        .Select(static face => face.Sprite).FirstOrDefault(static sprite => sprite.Length > 0);

    private void EmitCustomBox(List<float> vertices, List<int> indices, Vector3 origin, Vector3 from,
        Vector3 to, SpriteRect rect, float yaw)
    {
        foreach (var (normal, points) in BoxFaces(from, to))
        {
            var rotated = points.Select(point => RotateYaw(point, yaw)).ToArray();
            EmitCustomQuad(vertices, indices, origin, rotated, RotateYawDirection(normal, yaw), rect, 0f, 1f);
        }
    }

    private static IEnumerable<(Vector3 Normal, Vector3[] Points)> BoxFaces(Vector3 a, Vector3 b)
    {
        yield return (Vector3.UnitZ, [new(a.X,b.Y,b.Z),new(b.X,b.Y,b.Z),new(b.X,a.Y,b.Z),new(a.X,a.Y,b.Z)]);
        yield return (-Vector3.UnitZ, [new(b.X,b.Y,a.Z),new(a.X,b.Y,a.Z),new(a.X,a.Y,a.Z),new(b.X,a.Y,a.Z)]);
        yield return (Vector3.UnitX, [new(b.X,b.Y,b.Z),new(b.X,b.Y,a.Z),new(b.X,a.Y,a.Z),new(b.X,a.Y,b.Z)]);
        yield return (-Vector3.UnitX, [new(a.X,b.Y,a.Z),new(a.X,b.Y,b.Z),new(a.X,a.Y,b.Z),new(a.X,a.Y,a.Z)]);
        yield return (Vector3.UnitY, [new(a.X,b.Y,a.Z),new(b.X,b.Y,a.Z),new(b.X,b.Y,b.Z),new(a.X,b.Y,b.Z)]);
        yield return (-Vector3.UnitY, [new(a.X,a.Y,b.Z),new(b.X,a.Y,b.Z),new(b.X,a.Y,a.Z),new(a.X,a.Y,a.Z)]);
    }

    private void EmitOrientedPlane(List<float> vertices, List<int> indices, Vector3 origin, Vector3 center,
        Vector3 normal, float halfWidth, float halfHeight, SpriteRect rect, float tint)
    {
        var up = Vector3.UnitY;
        if (MathF.Abs(Vector3.Dot(normal, up)) > 0.9f) up = Vector3.UnitZ;
        var right = Vector3.Normalize(Vector3.Cross(up, normal));
        up = Vector3.Normalize(Vector3.Cross(normal, right));
        var points = new[]
        {
            center - right * halfWidth + up * halfHeight, center + right * halfWidth + up * halfHeight,
            center + right * halfWidth - up * halfHeight, center - right * halfWidth - up * halfHeight
        };
        EmitCustomQuad(vertices, indices, origin, points, normal, rect, tint, 1f);
    }

    private void EmitCustomQuad(List<float> vertices, List<int> indices, Vector3 origin,
        IReadOnlyList<Vector3> points, Vector3 normal, SpriteRect rect, float tint, float ao)
    {
        var baseIndex = vertices.Count / MeshData.FloatsPerVertex;
        var uv = new[]
        {
            new Vector2(rect.X / (float)_atlas.Width, rect.Y / (float)_atlas.Height),
            new Vector2((rect.X + rect.Width) / (float)_atlas.Width, rect.Y / (float)_atlas.Height),
            new Vector2((rect.X + rect.Width) / (float)_atlas.Width, (rect.Y + rect.Height) / (float)_atlas.Height),
            new Vector2(rect.X / (float)_atlas.Width, (rect.Y + rect.Height) / (float)_atlas.Height)
        };
        for (var i = 0; i < 4; i++)
        {
            var position = origin + points[i];
            vertices.Add(position.X); vertices.Add(position.Y); vertices.Add(position.Z);
            vertices.Add(normal.X); vertices.Add(normal.Y); vertices.Add(normal.Z);
            vertices.Add(uv[i].X); vertices.Add(uv[i].Y); vertices.Add(tint); vertices.Add(ao);
        }
        indices.Add(baseIndex); indices.Add(baseIndex + 1); indices.Add(baseIndex + 2);
        indices.Add(baseIndex); indices.Add(baseIndex + 2); indices.Add(baseIndex + 3);
    }

    private static Vector3 RotateYaw(Vector3 value, float degrees)
    {
        var radians = -degrees * MathF.PI / 180f;
        var centered = value - new Vector3(0.5f, 0, 0.5f);
        var rotated = new Vector3(centered.X * MathF.Cos(radians) + centered.Z * MathF.Sin(radians), centered.Y,
            -centered.X * MathF.Sin(radians) + centered.Z * MathF.Cos(radians));
        return rotated + new Vector3(0.5f, 0, 0.5f);
    }

    private static Vector3 RotateYawDirection(Vector3 value, float degrees)
    {
        var radians = -degrees * MathF.PI / 180f;
        return new Vector3(value.X * MathF.Cos(radians) + value.Z * MathF.Sin(radians), value.Y,
            -value.X * MathF.Sin(radians) + value.Z * MathF.Cos(radians));
    }

    // 朝向只留一个定义（SignGeometry）：牌面几何走 Assets 的 ElementRotation，旗帜图案和
    // 文字走这里的 RotateAround，两边必须同角。以前这里把 rotation 状态值直接当 blockstate
    // 角用，facing 型（墙牌/墙旗）就整体反了侧——0/180 自逆所以南北向一直没暴露。
    private static float SignRotation(ImmutableDictionary<string, string> properties) =>
        SignGeometry.BlockStateAngle(properties);

    private static Vector3I NormalizeBlockEntityPosition(LitematicRegion region, Vector3I position)
    {
        var size = region.Bounds.Size;
        if (position.X >= 0 && position.Y >= 0 && position.Z >= 0 &&
            position.X < size.X && position.Y < size.Y && position.Z < size.Z) return position;
        return position - region.Bounds.Min;
    }

    private static IReadOnlyList<SignText> ReadSignText(NbtMap data)
    {
        List<SignText> result = [];
        AddModern("front_text", SignSide.Front);
        AddModern("back_text", SignSide.Back);
        if (result.Count == 0)
        {
            var legacy = Enumerable.Range(1, 4).Select(i => PlainText(data.GetString($"Text{i}") ?? string.Empty)).ToArray();
            result.Add(new SignText(SignSide.Front, legacy, DyeColor(data.GetString("Color") ?? "black"),
                data.GetInt64("GlowingText") == 1));
        }
        return result;

        void AddModern(string key, SignSide side)
        {
            var text = data.GetMap(key);
            if (text is null) return;
            var messages = text.GetSequence("messages")?.Values
                .Select(value => PlainText(value.AsString() ?? string.Empty)).Take(4).ToArray() ?? [];
            Array.Resize(ref messages, 4);
            for (var i = 0; i < messages.Length; i++) messages[i] ??= string.Empty;
            result.Add(new SignText(side, messages, DyeColor(text.GetString("color") ?? "black"),
                text.GetInt64("has_glowing_text") == 1));
        }
    }

    private static string PlainText(string json)
    {
        if (json.Length == 0) return string.Empty;
        try
        {
            using var document = JsonDocument.Parse(json);
            return ReadTextComponent(document.RootElement);
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static string ReadTextComponent(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String) return element.GetString() ?? string.Empty;
        if (element.ValueKind == JsonValueKind.Array)
            return string.Concat(element.EnumerateArray().Select(ReadTextComponent));
        if (element.ValueKind != JsonValueKind.Object) return string.Empty;
        var value = element.TryGetProperty("text", out var text) ? text.GetString() ?? string.Empty : string.Empty;
        if (element.TryGetProperty("extra", out var extra)) value += ReadTextComponent(extra);
        return value;
    }

    private static string SignSpriteId(LitematicRegion region, Vector3I position, SignText text)
    {
        uint hash = 2166136261;
        foreach (var ch in region.Name + text.Side + text.Color.ToString("x6") + text.Glowing + string.Join('\n', text.Lines))
            hash = (hash ^ ch) * 16777619;
        return $"generated:sign/{position.X}_{position.Y}_{position.Z}_{text.Side.ToString().ToLowerInvariant()}_{hash:x8}";
    }

    private static bool IsSignEntity(string id) => id is "minecraft:sign" or "minecraft:hanging_sign";

    private static bool IsMinecart(string id) => id == "minecraft:minecart" ||
                                                  id.EndsWith("_minecart", StringComparison.Ordinal);

    private static bool IsBoat(string id) => id is "minecraft:boat" or "minecraft:chest_boat" ||
                                              id.EndsWith("_boat", StringComparison.Ordinal) ||
                                              id.EndsWith("_raft", StringComparison.Ordinal);

    private static string BoatSprite(EntityData entity)
    {
        var id = entity.Id[(entity.Id.IndexOf(':') + 1)..];
        var chest = id.Contains("chest", StringComparison.Ordinal);
        var wood = id is "boat" or "chest_boat"
            ? entity.Data.GetString("Type") ?? entity.Data.GetString("type") ?? "oak"
            : id.Replace("_chest_boat", "", StringComparison.Ordinal)
                .Replace("_boat", "", StringComparison.Ordinal)
                .Replace("_raft", "", StringComparison.Ordinal);
        if (wood.Length == 0) wood = "oak";
        return $"minecraft:entity/{(chest ? "chest_boat" : "boat")}/{wood}";
    }

    private static string? EntitySprite(EntityData entity) => entity.Id switch
    {
        "minecraft:drowned" => "minecraft:entity/zombie/drowned",
        "minecraft:villager" => "minecraft:entity/villager/villager",
        "minecraft:iron_golem" => "minecraft:entity/iron_golem/iron_golem",
        "minecraft:snow_golem" => "minecraft:entity/snow_golem",
        "minecraft:bat" => "minecraft:entity/bat",
        "minecraft:piglin" => "minecraft:entity/piglin/piglin",
        "minecraft:pig" => "minecraft:entity/pig/pig",
        "minecraft:shulker" => ShulkerEntitySprite(entity.Data),
        _ => null
    };

    private static string ShulkerEntitySprite(NbtMap data)
    {
        var color = (int)(data.GetInt64("Color") ?? 16);
        string[] names =
        [
            "white", "orange", "magenta", "light_blue", "yellow", "lime", "pink", "gray",
            "light_gray", "cyan", "purple", "blue", "brown", "green", "red", "black"
        ];
        return color is >= 0 and < 16
            ? $"minecraft:entity/shulker/shulker_{names[color]}"
            : "minecraft:entity/shulker/shulker";
    }

    private static string? MinecartCargoBlock(string id) => id switch
    {
        "minecraft:hopper_minecart" => "minecraft:hopper[facing=down,enabled=true]",
        "minecraft:chest_minecart" => "minecraft:chest[facing=north,type=single,waterlogged=false]",
        "minecraft:tnt_minecart" => "minecraft:tnt",
        "minecraft:furnace_minecart" => "minecraft:furnace[facing=north,lit=false]",
        _ => null
    };

    private static string PaintingMotive(NbtMap data)
    {
        var raw = GetString(data, "variant", "Motive", "motive") ?? "minecraft:kebab";
        var colon = raw.IndexOf(':');
        return colon < 0 ? raw : raw[(colon + 1)..];
    }

    private static string PaintingSprite(NbtMap data) => $"minecraft:painting/{PaintingMotive(data)}";

    private static (float Width, float Height) PaintingSize(string motive) => motive switch
    {
        "pool" or "courbet" or "sea" or "sunset" or "creebet" => (2, 1),
        "wanderer" or "graham" => (1, 2),
        "match" or "bust" or "stage" or "void" or "skull_and_roses" or "wither" => (2, 2),
        "fighters" => (4, 2),
        "skeleton" or "donkey_kong" => (4, 3),
        "pointer" or "pigscene" or "burning_skull" => (4, 4),
        _ => (1, 1)
    };

    private static string? GetString(NbtMap data, params string[] keys)
    {
        foreach (var key in keys) if (data.GetString(key) is { } value) return value;
        return null;
    }

    private static long? GetInt64(NbtMap data, params string[] keys)
    {
        foreach (var key in keys) if (data.GetInt64(key) is { } value) return value;
        return null;
    }

    private enum SignSide { Front, Back }
    private sealed record SignText(SignSide Side, string[] Lines, uint Color, bool Glowing);

    private static uint DyeColor(string color) => color switch
    {
        "white" => 0xF9FFFE, "orange" => 0xF9801D, "magenta" => 0xC74EBD, "light_blue" => 0x3AB3DA,
        "yellow" => 0xFED83D, "lime" => 0x80C71F, "pink" => 0xF38BAA, "gray" => 0x474F52,
        "light_gray" => 0x9D9D97, "cyan" => 0x169C9C, "purple" => 0x8932B8, "blue" => 0x3C44AA,
        "brown" => 0x835432, "green" => 0x5E7C16, "red" => 0xB02E26, _ => 0x1D1D21
    };

    private static uint DyeColor(int color) => color switch
    {
        0 => 0xF9FFFE, 1 => 0xF9801D, 2 => 0xC74EBD, 3 => 0x3AB3DA,
        4 => 0xFED83D, 5 => 0x80C71F, 6 => 0xF38BAA, 7 => 0x474F52,
        8 => 0x9D9D97, 9 => 0x169C9C, 10 => 0x8932B8, 11 => 0x3C44AA,
        12 => 0x835432, 13 => 0x5E7C16, 14 => 0xB02E26, _ => 0x1D1D21
    };

    private static string BannerSpriteId(LitematicRegion region, Vector3I position,
        IEnumerable<(string Sprite, uint Rgb)> layers)
    {
        uint hash = 2166136261;
        foreach (var ch in region.Name + string.Join(';', layers.Select(static layer => $"{layer.Sprite}:{layer.Rgb:x6}")))
            hash = (hash ^ ch) * 16777619;
        return $"generated:banner/{position.X}_{position.Y}_{position.Z}_{hash:x8}";
    }

    private static string? BannerPatternSprite(string code) => code switch
    {
        "b" => "minecraft:entity/banner/base", "bs" => "minecraft:entity/banner/stripe_bottom",
        "ts" => "minecraft:entity/banner/stripe_top", "ls" => "minecraft:entity/banner/stripe_left",
        "rs" => "minecraft:entity/banner/stripe_right", "cs" => "minecraft:entity/banner/stripe_center",
        "ms" => "minecraft:entity/banner/stripe_middle", "drs" => "minecraft:entity/banner/stripe_downright",
        "dls" => "minecraft:entity/banner/stripe_downleft", "ss" => "minecraft:entity/banner/small_stripes",
        "cr" => "minecraft:entity/banner/straight_cross", "sc" => "minecraft:entity/banner/diagonal_cross",
        "bo" => "minecraft:entity/banner/border", "cbo" => "minecraft:entity/banner/curly_border",
        "mc" => "minecraft:entity/banner/circle", "mr" => "minecraft:entity/banner/rhombus",
        "hh" => "minecraft:entity/banner/half_horizontal", "vh" => "minecraft:entity/banner/half_vertical",
        "cre" => "minecraft:entity/banner/creeper", "sku" => "minecraft:entity/banner/skull",
        "flo" => "minecraft:entity/banner/flower", "glb" => "minecraft:entity/banner/globe", _ => null
    };

    private static string[] ReadSherds(NbtMap data)
    {
        var sequence = data.GetSequence("sherds") ?? data.GetSequence("Sherds");
        return sequence?.Values.Select(static value => value.AsString()).Where(static value => value is not null)
            .Cast<string>().ToArray() ?? [];
    }

    private static string PotterySprite(string sherd)
    {
        var colon = sherd.IndexOf(':');
        var path = colon < 0 ? sherd : sherd[(colon + 1)..];
        const string suffix = "_pottery_sherd";
        if (path.EndsWith(suffix, StringComparison.Ordinal)) path = path[..^suffix.Length];
        return path == "brick"
            ? "minecraft:entity/decorated_pot/decorated_pot_side"
            : $"minecraft:entity/decorated_pot/{path}_pottery_pattern";
    }

    private static Vector3 FacingVector(int facing) => facing switch
    {
        0 => -Vector3.UnitY, 1 => Vector3.UnitY, 3 => Vector3.UnitZ, 4 => -Vector3.UnitX,
        5 => Vector3.UnitX, _ => -Vector3.UnitZ
    };
}
