// 单文件安装器：把预览 handler 装进当前用户（HKCU + LocalAppData，免管理员）。
// 只注册预览 shellex，不做 .litematic 双击关联——用户明确要求「绑定关了，只要预览」。
// 卸载走系统「设置→应用」标准入口，清理注册表与文件。
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

internal static class Program
{
    private const string Clsid = "{8E5B1A47-3F2E-4C6D-9A1B-7C2D5E8F0A31}";
    private const string PreviewIid = "{8895b1c6-b41f-4c1c-a562-0d564250836f}";
    private const string PayloadName = "LitematicaViewer.ShellPreview.dll";
    private const string UninstallKeyName = "LitematicaViewerPreview";
    private const string Version = "1.0.0";

    private static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LitematicaViewer", "Preview");

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "lvsetup.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {message}\n");
        }
        catch
        {
            // 日志只是取证口，写不进去别挡安装
        }
    }

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length >= 1 && args[0] == "/uninstall-go")
            {
                UninstallGo(quiet: args.Contains("/quiet"));
                return 0;
            }

            if (args.Length >= 1 && args[0] == "/uninstall")
            {
                // 卸载的进程自己就住在 InstallDir 里，删不掉运行中的自己：
                // 把自己复制到 %TEMP% 从那边接着干，原进程立刻退出解锁目录。
                var quiet = args.Contains("/quiet");
                Log($"uninstall start self={Environment.ProcessPath} quiet={quiet}");
                var tempSelf = Path.Combine(Path.GetTempPath(), "LitematicaViewerUninstall.exe");
                File.Copy(Environment.ProcessPath!, tempSelf, overwrite: true);
                Log($"copied to {tempSelf}");
                Process.Start(new ProcessStartInfo(tempSelf, quiet ? "/uninstall-go /quiet" : "/uninstall-go")
                {
                    UseShellExecute = true,
                });
                Log("detached, exiting");
                return 0;
            }

            Install(quiet: args.Contains("/quiet"));
            return 0;
        }
        catch (Exception ex)
        {
            Box(0x10 /*MB_ICONERROR*/, "失败：" + ex.Message);
            return 1;
        }
    }

    // ---------- 安装 ----------

    private static void Install(bool quiet)
    {
        var payload = ReadPayload();
        if (payload is null)
        {
            Box(0x10, "安装包里没有预览 handler 的 dll（Debug 构建的安装器不带 payload，请用 Release 版）。");
            return;
        }

        if (!quiet && Box(0x40 /*MB_ICONINFORMATION*/,
                $"安装 Litematica 预览扩展（按用户安装，不需要管理员）？\n\n安装位置：{InstallDir}") != 1 /*IDOK*/)
        {
            return;
        }

        Directory.CreateDirectory(InstallDir);
        var dllPath = Path.Combine(InstallDir, PayloadName);
        File.WriteAllBytes(dllPath, payload);
        var exePath = Path.Combine(InstallDir, "LitematicaViewer.Setup.exe");
        File.Copy(Environment.ProcessPath!, exePath, overwrite: true);

        // 预览 handler 的最小注册面：扩展名 shellex + CLSID + AppID。
        // 故意不写 .litematic 的默认 ProgID / shell\open——不做双击关联。
        SetValues(rf($@"Software\Classes\.litematic\shellex\{PreviewIid}"), ("", Clsid));
        SetValues(rf($@"Software\Classes\CLSID\{Clsid}"),
            ("", "Litematica Preview Handler"),
            ("AppID", Clsid)); // 必须是 CLSID 键下的「值」，COM 代理激活读它，写成子键静默失效
        SetValues(rf($@"Software\Classes\CLSID\{Clsid}\InprocServer32"),
            ("", dllPath),
            ("ThreadingModel", "Apartment"));
        SetValues(rf($@"Software\Classes\AppID\{Clsid}"),
            ("DllSurrogate", "prevhost.exe"));

        // 系统卸载入口（设置→应用）。
        SetValues(rf($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{UninstallKeyName}"),
            ("DisplayName", "Litematica Preview Handler"),
            ("DisplayVersion", Version),
            ("Publisher", "LitematicaViewer"),
            ("UninstallString", $"\"{exePath}\" /uninstall"),
            ("NoModify", 1),
            ("NoRepair", 1),
            ("EstimatedSize", (payload.Length + 4 * 1024 * 1024) / 1024)); // KB，含 AOT 运行时粗估

        if (!quiet && Box(0x40, "安装完成。立即重启资源管理器让预览生效？（会关闭已打开的文件夹窗口）")
            == 6 /*IDYES*/)
        {
            RestartExplorer();
        }
    }

    private static byte[]? ReadPayload()
    {
        using var stream = typeof(Program).Assembly
            .GetManifestResourceStream("LitematicaViewer.ShellPreview.dll");
        if (stream is null)
        {
            return null;
        }

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    // ---------- 卸载 ----------

    private static void UninstallGo(bool quiet)
    {
        if (!quiet && Box(0x40, "卸载 Litematica 预览扩展？") != 6 /*IDYES*/)
        {
            return;
        }

        // 只删自己注册的东西；.litematic 留给用户可能的其它关联，删空壳父键靠 try。
        Log("uninstall-go: deleting registry");
        TryDelete(rf($@"Software\Classes\.litematic\shellex\{PreviewIid}"));
        TryDelete(rf(@"Software\Classes\.litematic\shellex"));
        TryDelete(rf(@"Software\Classes\.litematic"));
        TryDelete(rf($@"Software\Classes\CLSID\{Clsid}"));
        TryDelete(rf($@"Software\Classes\AppID\{Clsid}"));
        TryDelete(rf($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{UninstallKeyName}"));
        Log("uninstall-go: registry done, deleting files");

        // 装文件的进程可能还没退干净，目录删除带重试。
        var self = Environment.ProcessPath!;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if (Directory.Exists(InstallDir))
                {
                    Directory.Delete(InstallDir, recursive: true);
                }

                break;
            }
            catch (IOException) when (attempt < 19)
            {
                Thread.Sleep(500);
            }
            catch (UnauthorizedAccessException) when (attempt < 19)
            {
                Thread.Sleep(500);
            }
        }

        // 自删：cmd 延迟一拍删自己的临时副本（本进程退出后文件才解锁）。
        var sysCmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        Process.Start(new ProcessStartInfo(sysCmd, $"/c timeout /t 1 /nobreak >nul & del /f /q \"{self}\"")
        {
            CreateNoWindow = true,
        });

        if (quiet)
        {
            return;
        }

        Box(0x40, "卸载完成。");
        if (Box(0x40, "立即重启资源管理器清理残留的预览状态？") == 6)
        {
            RestartExplorer();
        }
    }

    // ---------- 工具 ----------

    // 反斜杠路径不能写进 verbatim 字符串的字面量里直接拼，统一从这里规范化。
    private static string rf(string path) => path.Replace('/', '\\');

    private static void SetValues(string path, params (string Name, object Value)[] values)
    {
        using var key = Registry.CurrentUser.CreateSubKey(path, writable: true);
        foreach (var (name, value) in values)
        {
            key.SetValue(name, value);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
        catch (Exception)
        {
            // 键不存在 = 目标状态，不报错
        }
    }

    private static void RestartExplorer()
    {
        var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        Process.Start(new ProcessStartInfo(Path.Combine(sys, "taskkill.exe"), "/f /im explorer.exe")
        {
            CreateNoWindow = true,
        });
        Thread.Sleep(800);
        Process.Start(new ProcessStartInfo(Path.Combine(sys, "explorer.exe")));
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint hwnd, string text, string caption, uint type);

    private static int Box(uint type, string text) =>
        MessageBoxW(0, text, "Litematica Preview", type);
}
