using System.Diagnostics;

namespace LitematicaViewer.Core.Parsing;

public static class LitematicLoader
{
    public static LoadResult TryLoadFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (!File.Exists(path))
        {
            return LoadResult.Fail(LoadErrorKind.FileNotFound, $"file not found: '{path}'");
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return LoadResult.Fail(LoadErrorKind.IoFailure, ex.Message);
        }

        return TryLoad(bytes, path);
    }

    public static LoadResult TryLoad(byte[] bytes, string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        string source = sourcePath ?? string.Empty;

        try
        {
            LitematicParseResult parsed = LitematicParser.ParseRaw(bytes);
            return LoadResult.Ok(LitematicParser.ToDomain(parsed, source), parsed.Issues);
        }
        catch (LitematicFormatException ex)
        {
            Debug.WriteLine($"[CORE][load] source='{source}' kind={ex.Kind} message={ex.Message}");
            return LoadResult.Fail(ex.Kind, ex.Message);
        }
        catch (EndOfStreamException ex)
        {
            // Poly.NBT 对截断的文档抛这个。与 InvalidDataException 分开记，
            // 前者通常意味着文件被裁过，后者意味着结构有问题。
            Debug.WriteLine($"[CORE][load] source='{source}' kind=Truncated message={ex.Message}");
            return LoadResult.Fail(LoadErrorKind.Truncated, ex.Message);
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or OverflowException)
        {
            Debug.WriteLine($"[CORE][load] source='{source}' kind=Malformed message={ex.Message}");
            return LoadResult.Fail(LoadErrorKind.Malformed, ex.Message);
        }
    }
}
