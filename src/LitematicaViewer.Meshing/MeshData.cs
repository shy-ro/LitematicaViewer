namespace LitematicaViewer.Meshing;

// 中间层的产物：纯 CPU 数组，渲染层拿去建缓冲。两层之间唯一的契约。
// 顶点布局 pos3 + normal3 + uv2 = 8 floats；uv 已经归一到 [0,1] 且 v 已翻转
// （图集像素原点在左上，GL 采样原点在左下，翻转发生在生成期而不是着色器里）。
public sealed record MeshData(float[] Vertices, int[] Indices)
{
    public const int FloatsPerVertex = 8;
    public const int PositionOffset = 0;
    public const int NormalOffset = 3;
    public const int UvOffset = 6;

    public int VertexCount => Vertices.Length / FloatsPerVertex;
}
