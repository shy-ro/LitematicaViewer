using System.Diagnostics;
using System.Numerics;

namespace LitematicaViewer.Previewer.Diagnostics;

// 展台光环的顶点数据检查。和轴线那套同一个理由：它是死数据，没有隐藏面、没有插值，
// 「数据写对了就等于画对了」，所以不读一个像素回来。
//
// 顶点数据写错的表现全是「看着有点不对」：半径不是那一个、y 不在底面上、颜色偏了、
// alpha 三档写成一档——四种都不报错，只让人觉得光环的位置或者形状怪。所以这里把每一样都断言一遍。
internal static class DebugPedestal
{
    public static void CheckGeometry(float[] vertices, int[] indices)
    {
        int rings = GlPedestalRenderer.RingRadii.Length;
        int segments = GlPedestalRenderer.Segments;

        Debug.Assert(
            vertices.Length == rings * segments * GlPedestalRenderer.FloatsPerVertex,
            $"[PREVIEWER][gl.pedestal] 顶点数不对 floats={vertices.Length} " +
            $"expected={rings * segments * GlPedestalRenderer.FloatsPerVertex}");

        // 相邻两圈之间一个带、一个带里每段两个三角形。
        Debug.Assert(
            indices.Length == (rings - 1) * segments * 6,
            $"[PREVIEWER][gl.pedestal] 索引数不对 indices={indices.Length} expected={(rings - 1) * segments * 6}");

        for (int ring = 0; ring < rings; ring++)
        {
            float declared = GlPedestalRenderer.RingRadii[ring];
            float alpha = GlPedestalRenderer.RingAlphas[ring];

            Debug.Assert(
                float.IsFinite(declared) && declared > 0f,
                $"[PREVIEWER][gl.pedestal] 半径不是正数 ring={ring} radius={declared}");
            Debug.Assert(
                alpha is > 0f and < 1f,
                $"[PREVIEWER][gl.pedestal] alpha 不在 (0,1) ring={ring} alpha={alpha}");

            // 每圈只查首尾两个顶点：它们是按同一个半径算出来的，中间那些分段只改角度。
            // 全查一遍是两万多次比较，而错法只有「这一圈的半径整体写错」一种。
            foreach (int segment in new[] { 0, segments - 1 })
            {
                int vertex = (ring * segments) + segment;
                Vector3 position = ReadPosition(vertices, vertex);
                float radius = MathF.Sqrt((position.X * position.X) + (position.Z * position.Z));

                // 落在底面上。底面高度是矩阵给的，顶点里的 y 必须是 0——
                // 写成别的值时，光环会跟着底面高度被抬两次。
                Debug.Assert(
                    position.Y == 0f,
                    $"[PREVIEWER][gl.pedestal] 顶点不在 y=0 平面上 ring={ring} segment={segment} y={position.Y}");
                Debug.Assert(
                    MathF.Abs(radius - declared) < 1e-6f,
                    $"[PREVIEWER][gl.pedestal] 顶点半径不对 ring={ring} segment={segment} " +
                    $"got={radius:F6} expected={declared}");

                Vector3 color = ReadColor(vertices, vertex);
                Debug.Assert(
                    color == GlPedestalRenderer.Color,
                    $"[PREVIEWER][gl.pedestal] 顶点颜色不对 ring={ring} segment={segment} " +
                    $"got=({color}) expected=({GlPedestalRenderer.Color})");
                Debug.Assert(
                    ReadAlpha(vertices, vertex) == alpha,
                    $"[PREVIEWER][gl.pedestal] 顶点 alpha 不对 ring={ring} segment={segment} " +
                    $"got={ReadAlpha(vertices, vertex)} expected={alpha}");
            }
        }

        for (int ring = 1; ring < rings; ring++)
        {
            Debug.Assert(
                GlPedestalRenderer.RingRadii[ring] > GlPedestalRenderer.RingRadii[ring - 1],
                $"[PREVIEWER][gl.pedestal] 半径不是递增的 ring={ring} " +
                $"got={GlPedestalRenderer.RingRadii[ring]} previous={GlPedestalRenderer.RingRadii[ring - 1]}");
        }

        // 内缘大于 1（= 目标的水平半对角线）的那条界线：光环是在「托底」，不是压在目标底下。
        // 小于 1 时目标的外接圆盖住光环一圈，被挡掉的那一段看起来像光环少画了一块。
        Debug.Assert(
            GlPedestalRenderer.RingRadii[0] > 1f,
            $"[PREVIEWER][gl.pedestal] 内缘半径不大于目标的水平半对角线 " +
            $"inner={GlPedestalRenderer.RingRadii[0]} note=光环会被目标的轮廓挡掉一段");

        // 中带必须比外圈亮，否则那圈软边看不出来——光环就成了一个厚边圆环。
        Debug.Assert(
            GlPedestalRenderer.RingAlphas[rings / 2] > GlPedestalRenderer.RingAlphas[0],
            $"[PREVIEWER][gl.pedestal] 中带不比外圈亮，软边看不出来 " +
            $"middle={GlPedestalRenderer.RingAlphas[rings / 2]} outer={GlPedestalRenderer.RingAlphas[0]}");

        // 蓝色是要求本身（「半透明蓝色光环」）。写成偏暖的颜色不会报错也不会崩，
        // 只是那圈光环看着像锈——而这条断言的存在就是为了让「改成别的颜色」这件事必须是有意的。
        Vector3 color3 = GlPedestalRenderer.Color;
        Debug.Assert(
            color3.Z > color3.X && color3.Z > color3.Y,
            $"[PREVIEWER][gl.pedestal] 光环不是蓝色 color=({color3})");
    }

    private static Vector3 ReadPosition(float[] vertices, int vertex)
    {
        int start = vertex * GlPedestalRenderer.FloatsPerVertex;
        return new Vector3(vertices[start], vertices[start + 1], vertices[start + 2]);
    }

    private static Vector3 ReadColor(float[] vertices, int vertex)
    {
        int start = (vertex * GlPedestalRenderer.FloatsPerVertex) + 3;
        return new Vector3(vertices[start], vertices[start + 1], vertices[start + 2]);
    }

    private static float ReadAlpha(float[] vertices, int vertex) =>
        vertices[(vertex * GlPedestalRenderer.FloatsPerVertex) + 6];
}
