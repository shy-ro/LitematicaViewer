using System.Numerics;
using LitematicaViewer.Assets;
using LitematicaViewer.Meshing;

namespace LitematicaViewer.ShellPreview;

// 软件光栅化的场景快照：网格 + 图集 + 取景球。后台线程整块构建，
// 构建完通过消息交给 STA 线程使用，之后只读。
internal sealed record Scene(List<MeshData> Meshes, Vector3 Centre, float Radius, TextureAtlas Atlas);

// 正交轨道相机 + z-buffer 光栅化，从 PreviewBlocks 的等距渲染器改出：
// 视角不钉死（拖动改 yaw/pitch），输出 32bpp BGRA 供 SetDIBitsToDevice 直贴
// （24bpp 的行对齐规则要自己补 padding，32bpp 天然对齐，省心）。
// 纯函数：吃 Scene 吐像素，不碰任何窗口/GDI 状态。
internal static class Raster
{
    public static void Render(Scene scene, byte[] bgra, int width, int height, float yaw, float pitch)
    {
        Array.Clear(bgra);
        var view = new Vector3(
            MathF.Cos(pitch) * MathF.Cos(yaw),
            -MathF.Sin(pitch),
            MathF.Cos(pitch) * MathF.Sin(yaw));

        // right = up0 × view（与 lookAt 同构）；up 由两者闭合。
        var right = Vector3.Normalize(Vector3.Cross(new Vector3(0, 1, 0), view));
        var up = Vector3.Cross(view, right);
        var scale = MathF.Min(width, height) * 0.5f * 0.92f / scene.Radius;

        float[] zbuf = new float[width * height];
        Array.Fill(zbuf, float.NegativeInfinity);

        foreach (var mesh in scene.Meshes)
        {
            RenderMesh(mesh, scene.Atlas, bgra, zbuf, width, height, scene.Centre, view, right, up, scale);
        }
    }

    private static void RenderMesh(
        MeshData mesh, TextureAtlas atlas, byte[] bgra, float[] zbuf,
        int width, int height, Vector3 centre, Vector3 view, Vector3 right, Vector3 up, float scale)
    {
        var vertexCount = mesh.VertexCount;
        if (vertexCount == 0)
        {
            return;
        }

        var verts = mesh.Vertices;
        // 先把全部顶点投到屏幕（x, y, 深度），三角形遍历里反复用。
        Span<Vector3> screen = vertexCount <= 512
            ? stackalloc Vector3[vertexCount]
            : new Vector3[vertexCount];
        for (var i = 0; i < vertexCount; i++)
        {
            var o = i * MeshData.FloatsPerVertex;
            var rel = new Vector3(verts[o], verts[o + 1], verts[o + 2]) - centre;
            screen[i] = new Vector3(
                width / 2f + Vector3.Dot(rel, right) * scale,
                height / 2f - Vector3.Dot(rel, up) * scale,
                Vector3.Dot(rel, view));
        }

        var indices = mesh.Indices;
        var light = Vector3.Normalize(new Vector3(0.35f, 0.9f, 0.2f));
        for (var tri = 0; tri < indices.Length; tri += 3)
        {
            var i0 = indices[tri];
            var i1 = indices[tri + 1];
            var i2 = indices[tri + 2];
            var s0 = screen[i0];
            var s1 = screen[i1];
            var s2 = screen[i2];

            var area = Edge(s0, s1, s2.X, s2.Y);
            if (MathF.Abs(area) < 1e-9f)
            {
                continue;
            }

            var minX = Math.Max(0, (int)MathF.Floor(Math.Min(s0.X, Math.Min(s1.X, s2.X))));
            var maxX = Math.Min(width - 1, (int)MathF.Ceiling(Math.Max(s0.X, Math.Max(s1.X, s2.X))));
            var minY = Math.Max(0, (int)MathF.Floor(Math.Min(s0.Y, Math.Min(s1.Y, s2.Y))));
            var maxY = Math.Min(height - 1, (int)MathF.Ceiling(Math.Max(s0.Y, Math.Max(s1.Y, s2.Y))));
            if (maxX < minX || maxY < minY)
            {
                continue;
            }

            // 平面属性取首顶点：quads 是平的，法线与 tint 本来就逐面一份。
            var o0 = i0 * MeshData.FloatsPerVertex;
            var normal = new Vector3(verts[o0 + 3], verts[o0 + 4], verts[o0 + 5]);

            // 背面剔除：法线背向视线（点积为正 = 面朝场景深处）的面跳过。
            // 少画约一半像素，拖动的帧率就靠它。朝向约定与 PreviewBlocks 一致，
            // 挖孔贴图 discard 之后再剔不影响结果。
            if (Vector3.Dot(normal, view) >= 0)
            {
                continue;
            }

            var shade = 0.62f + 0.38f * MathF.Max(Vector3.Dot(normal, light), 0f);
            var tintSlot = verts[o0 + MeshData.TintOffset];
            var tint = tintSlot switch
            {
                < 0.5f => new Vector3(1f),
                < 1.5f => new Vector3(0.569f, 0.741f, 0.349f),
                < 2.5f => new Vector3(0.467f, 0.671f, 0.184f),
                _ => new Vector3(0.247f, 0.463f, 0.894f)
            };

            var u0 = verts[o0 + MeshData.UvOffset];
            var v0 = verts[o0 + MeshData.UvOffset + 1];
            var o1 = i1 * MeshData.FloatsPerVertex;
            var u1 = verts[o1 + MeshData.UvOffset];
            var v1 = verts[o1 + MeshData.UvOffset + 1];
            var o2 = i2 * MeshData.FloatsPerVertex;
            var u2 = verts[o2 + MeshData.UvOffset];
            var v2 = verts[o2 + MeshData.UvOffset + 1];

            var inv = 1f / area;
            for (var y = minY; y <= maxY; y++)
            {
                var rowBase = y * width;
                for (var x = minX; x <= maxX; x++)
                {
                    var px = x + 0.5f;
                    var py = y + 0.5f;
                    // 循环边函数重心坐标：l_k 在第 k 个顶点取 1。
                    // 配错权重会整张贴图沿对角线镜像错位——PreviewBlocks 踩过。
                    var l0 = Edge(s1, s2, px, py) * inv;
                    var l1 = Edge(s2, s0, px, py) * inv;
                    var l2 = Edge(s0, s1, px, py) * inv;
                    if (l0 < 0 || l1 < 0 || l2 < 0)
                    {
                        continue;
                    }

                    var depth = l0 * s0.Z + l1 * s1.Z + l2 * s2.Z;
                    var index = rowBase + x;
                    if (depth <= zbuf[index])
                    {
                        continue;
                    }

                    var u = l0 * u0 + l1 * u1 + l2 * u2;
                    var v = l0 * v0 + l1 * v1 + l2 * v2;
                    // 与 GL 一致：v 不翻转，t=0 对应数据第一行（图集顶行）。
                    var tx = Math.Clamp((int)(u * atlas.Width), 0, atlas.Width - 1);
                    var ty = Math.Clamp((int)(v * atlas.Height), 0, atlas.Height - 1);
                    var texel = (ty * atlas.Width + tx) * 4;
                    if (atlas.Pixels[texel + 3] < 128)
                    {
                        continue; // alpha cutout，与片元着色器同阈值
                    }

                    zbuf[index] = depth;
                    var pixel = index * 4;
                    bgra[pixel + 0] = (byte)Math.Clamp(atlas.Pixels[texel + 2] * tint.X * shade, 0, 255);
                    bgra[pixel + 1] = (byte)Math.Clamp(atlas.Pixels[texel + 1] * tint.Y * shade, 0, 255);
                    bgra[pixel + 2] = (byte)Math.Clamp(atlas.Pixels[texel + 0] * tint.Z * shade, 0, 255);
                    bgra[pixel + 3] = 255;
                }
            }
        }
    }

    // 有向边函数 = 叉积 (b-a)×(p-a)。
    private static float Edge(Vector3 a, Vector3 b, float px, float py)
    {
        return ((b.X - a.X) * (py - a.Y)) - ((b.Y - a.Y) * (px - a.X));
    }
}
