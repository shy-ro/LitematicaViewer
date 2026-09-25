using System.Collections.Immutable;
using LitematicaViewer.Core.Model;

namespace LitematicaViewer.Core.Parsing;

// 失败不是异常而是返回值：调用方是要给用户看一个文件的，路径写错和文件损坏
// 都只是"这个文件打不开"，不该让 UI 去 catch。
public sealed record LoadResult(
    bool Success,
    LitematicDocument? Document,
    LoadErrorKind Error,
    string Message,
    ImmutableArray<string> Issues)
{
    public static LoadResult Ok(LitematicDocument document, ImmutableArray<string> issues) =>
        new(true, document, LoadErrorKind.None, string.Empty, issues);

    public static LoadResult Fail(LoadErrorKind kind, string message, ImmutableArray<string>? issues = null) =>
        new(false, null, kind, message, issues ?? []);
}
