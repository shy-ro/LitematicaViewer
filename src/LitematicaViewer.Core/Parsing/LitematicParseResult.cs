using System.Collections.Immutable;
using LitematicaViewer.Core.Model;

namespace LitematicaViewer.Core.Parsing;

// 解析过程中一切"能继续但不该无视"的情况都进 Issues，不抛异常。
// 真实投影里出现过 BlockStates 长度不足的区域，直接抛会让整个文件打不开。
public sealed record LitematicParseResult(
    LitematicMetadata Metadata,
    ImmutableArray<RawRegion> Regions,
    ImmutableArray<string> Issues);

public sealed record RawRegion(
    string Name,
    Vector3I Position,
    Vector3I Size,
    ImmutableArray<BlockStateDefinition> Palette,
    ImmutableArray<int> BlockIndices);
