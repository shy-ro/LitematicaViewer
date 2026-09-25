using System.Numerics;
using LitematicaViewer.Core.Model;
using LitematicaViewer.Core.Parsing;

namespace LitematicaViewer.Core.Tests;

// 拾取的对拍基准，故意写成最笨的版本：沿射线等步长采样，每个采样点用
// Bounds.Contains + GetState 判断，第一个落在非空气格里的采样点就算命中。
//
// 它不碰逐格步进、不碰进面判定、不碰区域排序，与 VoxelPicker 除了"射线是哪条"
// 之外没有共享逻辑。共享了就等于自己确认自己，两边同一个误解会互相确认成"通过"。
//
// 只对下标全部落在调色板内的夹具成立：GetState 对越界下标会回退 palette[0]，
// 而 VoxelPicker 把越界下标当空气跳过。
public static class PickingReference
{
    public static bool TryMarch(
        LitematicDocument document,
        Vector3 origin,
        Vector3 direction,
        float maxDistance,
        float step,
        out Vector3I block,
        out float distance)
    {
        block = default;
        distance = 0f;

        if (step <= 0f || direction.LengthSquared() < 1e-12f)
        {
            return false;
        }

        Vector3 dir = Vector3.Normalize(direction);

        for (float t = 0f; t <= maxDistance; t += step)
        {
            Vector3 point = origin + (dir * t);
            Vector3I cell = new(
                (int)MathF.Floor(point.X),
                (int)MathF.Floor(point.Y),
                (int)MathF.Floor(point.Z));

            foreach (LitematicRegion region in document.Regions)
            {
                if (!region.Bounds.Contains(cell) || region.GetState(cell).IsAir)
                {
                    continue;
                }

                block = cell;
                distance = t;
                return true;
            }
        }

        return false;
    }
}
