using System.Diagnostics;
using System.Numerics;
using LitematicaViewer.Core.Model;

namespace LitematicaViewer.Core.Picking;

// 射线与体素网格求交。纯 CPU、不碰 GL，所以放 Core 而不是 Previewer：
// 这一步不需要相机，相机只负责把屏幕上的一个点变成这条射线，那段换算留在 Previewer。
//
// 用 Amanatides & Woo 的逐格步进，不用"沿射线按固定步长采样"：步长给小了在
// 350 万体积的区域上每次点击都要走几百万步，给大了会在掠射时整格跳过去，
// 而跳过的那一格表现为"明明点到方块却说没命中"。
public static class VoxelPicker
{
    private static void MaybeLog(bool log, string message)
    {
        if (log)
        {
            Debug.WriteLine(message);
        }
    }

    // 射线起点与方向用 System.Numerics.Vector3（float）：方块坐标是整数语义，
    // 但射线本身是连续的。取不到整数的量硬塞进 Vector3I 只会把误差藏起来。
    public const float DefaultMaxDistance = 512f;

    private const float AxisEpsilon = 1e-8f;

    // 进入点正好落在格子边界上时往盒内挪一丁点。Litematica 里轴对齐射线是常态，
    // 轴对齐射线的进入点必然落在边界上，不挪的话 floor 会挑到盒外那一格。
    // 挪的方向只能是射线的去向了——盒内一定在进入平面的去侧。
    private const float EntryNudge = 1e-4f;

    // log=false 给悬停拾取用：指针一秒几百条事件，逐条打会把终端冲掉。
    // 点击式拾取（日志有排查价值）保持默认 true。
    public static bool TryPick(
        LitematicDocument document,
        Vector3 origin,
        Vector3 direction,
        out VoxelHit hit,
        float maxDistance = DefaultMaxDistance,
        bool log = true)
    {
        hit = default;
        bool found = false;
        float best = maxDistance;

        // 后面的区域拿已知命中距离当射程：比已命中的还远就不必走完它的边界。
        foreach (LitematicRegion region in document.Regions)
        {
            if (TryPick(region, origin, direction, out VoxelHit candidate, best, log) && candidate.Distance < best)
            {
                hit = candidate;
                best = candidate.Distance;
                found = true;
            }
        }

        if (log)
        {
            Debug.WriteLine(
                $"[CORE][pick.document] regions={document.Regions.Length} found={found} " +
                $"distance={(found ? best : 0f)} expected=最近的那个区域");
        }

        return found;
    }

    public static bool TryPick(
        LitematicRegion region,
        Vector3 origin,
        Vector3 direction,
        out VoxelHit hit,
        float maxDistance = DefaultMaxDistance,
        bool log = true)
    {
        hit = default;

        // 方向不要求调用方先归一化：Distance 的语义是沿射线的世界单位长度，
        // 一旦谁忘了归一化，拿到的距离会自洽地差一个系数，而且不会以异常的形式暴露。
        if (!TryNormalize(direction, out Vector3 dir) || maxDistance <= 0f)
        {
            MaybeLog(log, $"[CORE][pick.miss] region='{region.Name}' reason=badRay maxDistance={maxDistance}");
            return false;
        }

        Vector3I size = region.Bounds.Size;
        if (size.X <= 0 || size.Y <= 0 || size.Z <= 0)
        {
            MaybeLog(log, $"[CORE][pick.miss] region='{region.Name}' reason=degenerateSize size={size}");
            return false;
        }

        Vector3I min = region.Bounds.Min;
        Vector3 local = origin - new Vector3(min.X, min.Y, min.Z);
        Vector3 limit = new(size.X, size.Y, size.Z);

        if (!TryEnterBox(local, dir, limit, maxDistance, out float entry, out int entryAxis))
        {
            MaybeLog(log, $"[CORE][pick.miss] region='{region.Name}' reason=boxMiss entry={entry} axis={entryAxis}");
            return false;
        }

        Vector3 point = local + (dir * entry);
        if (entry > 0f)
        {
            point += dir * EntryNudge;
        }

        int cellX = (int)MathF.Floor(point.X);
        int cellY = (int)MathF.Floor(point.Y);
        int cellZ = (int)MathF.Floor(point.Z);

        int stepX = dir.X > 0f ? 1 : dir.X < 0f ? -1 : 0;
        int stepY = dir.Y > 0f ? 1 : dir.Y < 0f ? -1 : 0;
        int stepZ = dir.Z > 0f ? 1 : dir.Z < 0f ? -1 : 0;

        // 每跨一格需要的参数增量，以及到下一格的参数值（相对于 entry）。
        float tDeltaX = stepX == 0 ? float.PositiveInfinity : MathF.Abs(1f / dir.X);
        float tDeltaY = stepY == 0 ? float.PositiveInfinity : MathF.Abs(1f / dir.Y);
        float tDeltaZ = stepZ == 0 ? float.PositiveInfinity : MathF.Abs(1f / dir.Z);
        float tMaxX = stepX == 0 ? float.PositiveInfinity
            : stepX > 0 ? (cellX + 1 - point.X) * tDeltaX : (point.X - cellX) * tDeltaX;
        float tMaxY = stepY == 0 ? float.PositiveInfinity
            : stepY > 0 ? (cellY + 1 - point.Y) * tDeltaY : (point.Y - cellY) * tDeltaY;
        float tMaxZ = stepZ == 0 ? float.PositiveInfinity
            : stepZ > 0 ? (cellZ + 1 - point.Z) * tDeltaZ : (point.Z - cellZ) * tDeltaZ;

        // 从盒外进来时，脸必然是进入轴上的那个反向面；起点本来就在盒内则没有面可穿。
        VoxelFace face = entryAxis < 0 ? VoxelFace.None : VoxelFaces.FromAxis(entryAxis, At(dir, entryAxis) > 0f ? -1 : 1);
        float t = entry;
        int steps = 0;

        while (true)
        {
            steps++;

            if ((uint)cellX < (uint)size.X && (uint)cellY < (uint)size.Y && (uint)cellZ < (uint)size.Z)
            {
                int index = region.ToIndex(cellX, cellY, cellZ);
                if ((uint)index < (uint)region.BlockIndices.Length)
                {
                    int paletteIndex = region.BlockIndices[index];

                    // 越出调色板的下标当空气跳过，口径与 CountNonAirBlocks 一致。
                    // 换成 GetStateAt 会回退到 palette[0]，于是"统计说这里没有方块"的地方
                    // 反而能拾取出一个方块，两边对不上账。
                    if ((uint)paletteIndex < (uint)region.Palette.Length && !region.Palette[paletteIndex].IsAir)
                    {
                        Vector3I block = new(min.X + cellX, min.Y + cellY, min.Z + cellZ);
                        hit = new VoxelHit(region, block, face, t, paletteIndex, region.Palette[paletteIndex]);
                        if (log)
                        {
                            Debug.WriteLine(
                                $"[CORE][pick.hit] region='{region.Name}' block={block} face={face} " +
                                $"distance={t} steps={steps} palette={paletteIndex} state={region.Palette[paletteIndex]}");
                        }
                        return true;
                    }
                }
            }

            float next;
            if (tMaxX <= tMaxY && tMaxX <= tMaxZ)
            {
                next = tMaxX;
                tMaxX += tDeltaX;
                cellX += stepX;
                face = VoxelFaces.FromAxis(0, -stepX);
            }
            else if (tMaxY <= tMaxZ)
            {
                next = tMaxY;
                tMaxY += tDeltaY;
                cellY += stepY;
                face = VoxelFaces.FromAxis(1, -stepY);
            }
            else
            {
                next = tMaxZ;
                tMaxZ += tDeltaZ;
                cellZ += stepZ;
                face = VoxelFaces.FromAxis(2, -stepZ);
            }

            t = entry + next;

            // 走出盒外就是没命中，不必再走。三个轴里至少有一个的 tDelta 是有限值
            // （方向全零的射线在前面就被拒了），所以 t 必然单调增长并最终越过射程，
            // 循环一定会退出，不需要再加步数上限。
            if (t > maxDistance || (uint)cellX >= (uint)size.X || (uint)cellY >= (uint)size.Y || (uint)cellZ >= (uint)size.Z)
            {
                MaybeLog(
                    log,
                    $"[CORE][pick.miss] region='{region.Name}' reason=marchOut t={t} steps={steps} maxDistance={maxDistance}");
                return false;
            }
        }
    }

    // 先求射线与包围盒的进出参数，从盒外一步一格走进去是最容易写错也最慢的一段。
    // 返回的 entry 是进入盒子的参数（起点已在盒内时为 0），entryAxis 是进入时穿过的轴，
    // 起点已在盒内时 entryAxis 为 -1。
    private static bool TryEnterBox(
        Vector3 origin,
        Vector3 direction,
        Vector3 limit,
        float maxDistance,
        out float entry,
        out int entryAxis)
    {
        entry = 0f;
        entryAxis = -1;
        float exit = float.PositiveInfinity;

        for (int axis = 0; axis < 3; axis++)
        {
            float o = At(origin, axis);
            float d = At(direction, axis);
            float hi = At(limit, axis);

            if (MathF.Abs(d) < AxisEpsilon)
            {
                // 与这对面平行：要么整条射线都夹在这层板里，要么永远进不来。
                if (o < 0f || o >= hi)
                {
                    return false;
                }

                continue;
            }

            float near = -o / d;
            float far = (hi - o) / d;
            if (near > far)
            {
                (near, far) = (far, near);
            }

            if (near > entry)
            {
                entry = near;
                entryAxis = axis;
            }

            if (far < exit)
            {
                exit = far;
            }

            if (entry > exit)
            {
                return false;
            }
        }

        // entry 停在 0 且 exit 为负，说明盒子整个在射线背后。
        if (exit < 0f || entry > maxDistance)
        {
            return false;
        }

        entry = MathF.Max(entry, 0f);
        return true;
    }

    private static bool TryNormalize(Vector3 direction, out Vector3 normalized)
    {
        float length = direction.Length();
        if (!float.IsFinite(length) || length < AxisEpsilon)
        {
            normalized = default;
            return false;
        }

        normalized = direction / length;
        return true;
    }

    // Vector3 的索引器在部分目标框架上表现不一致，这里统一走显式分派。
    private static float At(Vector3 v, int axis) => axis switch
    {
        0 => v.X,
        1 => v.Y,
        _ => v.Z,
    };
}
