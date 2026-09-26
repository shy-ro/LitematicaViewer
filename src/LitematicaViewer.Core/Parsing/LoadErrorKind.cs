namespace LitematicaViewer.Core.Parsing;

public enum LoadErrorKind
{
    None,
    FileNotFound,
    IoFailure,
    NotNbt,
    Truncated,
    Malformed,
    RegionDecodeFailed
}
