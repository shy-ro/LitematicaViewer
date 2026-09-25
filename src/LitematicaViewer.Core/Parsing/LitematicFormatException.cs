namespace LitematicaViewer.Core.Parsing;

// 带错误分类的解析异常。Poly.NBT 的异常类型不足以区分"这不是 NBT"和"NBT 但结构损坏"，
// 靠 Message 关键字去猜分类会在本地化或措辞变化时静默失效，所以自带一个 Kind。
public sealed class LitematicFormatException : Exception
{
    public LitematicFormatException(LoadErrorKind kind, string message)
        : base(message)
    {
        Kind = kind;
    }

    public LitematicFormatException(LoadErrorKind kind, string message, Exception innerException)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public LoadErrorKind Kind { get; }
}
