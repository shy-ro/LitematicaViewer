namespace LitematicaViewer.Meshing;

// 中间层的产物：纯 CPU 数组，渲染层拿去建缓冲。两层之间唯一的契约。
// 顶点布局 pos3 + normal3 + uv2 + tint1 + ao1 = 10 floats；uv 已经归一到 [0,1]。
// v 不翻转：GL 采样的 t=0 对应上传数据的第一行，图集字节数组行 0 是图集顶
// （PNG 约定），v = 图集 y/H。翻转会在多行图集时镜像进隔壁 sprite 的 cell。
// tint 是色板槽号（0 不染 / 1 草绿 / 2 叶绿 / 3 水蓝），着色器按槽乘固定色。
public sealed record MeshData(float[] Vertices, int[] Indices)
{
    public const int FloatsPerVertex = 10;
    public const int PositionOffset = 0;
    public const int NormalOffset = 3;
    public const int UvOffset = 6;
    public const int TintOffset = 8;
    public const int AoOffset = 9;

    public int VertexCount => Vertices.Length / FloatsPerVertex;
}
