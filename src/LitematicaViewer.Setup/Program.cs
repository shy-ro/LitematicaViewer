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
    // 系统共享的「Preview Handler Surrogate Host」AppID（微软自家 handler 全指向它，
    // DllSurrogate=prevhost.exe 在 HKLM 已定义）。自建 AppID 会让代理激活在加载 dll
    // 之前就死掉——三个挂点全灭且取证日志零记录的根因。
    private const string SurrogateAppId = "{6D2B5079-2F0B-48DD-AB7F-97CEC514D30B}";
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
        Log($"main start args=[{string.Join(", ", args)}]");
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
            // COM/脚本边界没人看弹窗细节：先落日志再弹，静默装的时候日志是唯一线索。
            Log($"FATAL {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            Box(0x10 /*MB_ICONERROR*/, "失败：" + ex.Message);
            return 1;
        }
    }

    // ---------- 安装 ----------

    private static void Install(bool quiet)
    {
        Log($"install start quiet={quiet}");
        var payload = ReadResource("LitematicaViewer.ShellPreview.dll");
        var angle = ReadResource("LitematicaViewer.av_libglesv2.dll");
        var jar = ReadResource("LitematicaViewer.vanilla.jar");
        Log($"install: resources payload={payload?.Length.ToString() ?? "null"} " +
            $"angle={angle?.Length.ToString() ?? "null"} jar={jar?.Length.ToString() ?? "null"}");
        if (payload is null)
        {
            // 嵌入资源大小写敏感、名字一个字符都不能差：列出来才知道嵌没嵌、嵌的叫什么。
            Log("install: manifest resources=[" +
                string.Join(", ", typeof(Program).Assembly.GetManifestResourceNames()) + "]");
        }
        if (payload is null || jar is null || angle is null)
        {
            Box(0x10, "安装包里缺少 payload（Debug 构建的安装器不带资源，请用 Release 版）。");
            return;
        }

        if (!quiet && Box(0x40 /*MB_ICONINFORMATION*/,
                $"安装 Litematica 预览扩展（按用户安装，不需要管理员）？\n\n安装位置：{InstallDir}") != 1 /*IDOK*/)
        {
            return;
        }

        // 覆盖前先杀代理宿主：prevhost 映射着旧 dll，不杀则 WriteAllBytes 直接 IOException
        // （部署脚本静默装 rc 1 的根因）。prevhost 是共享 surrogate，误杀别的预览会按需重生。
        KillPrevhost();
        Log("install: prevhost cleared, writing files");

        Directory.CreateDirectory(InstallDir);
        var dllPath = Path.Combine(InstallDir, PayloadName);
        File.WriteAllBytes(dllPath, payload);
        // ANGLE 单文件库必须和 handler dll 同目录：EglHost 从模块目录显式加载它。
        File.WriteAllBytes(Path.Combine(InstallDir, "av_libglesv2.dll"), angle);
        // 资源包装到 <安装目录>\packs\——SceneLoader 从 dll 位置向上找 packs，第 0 层命中。
        Directory.CreateDirectory(Path.Combine(InstallDir, "packs"));
        File.WriteAllBytes(Path.Combine(InstallDir, "packs", "vanilla-1.20.1.jar"), jar);
        var exePath = Path.Combine(InstallDir, "LitematicaViewer.Setup.exe");
        File.Copy(Environment.ProcessPath!, exePath, overwrite: true);

        // 预览 handler 注册（实战检验的路线：专用 ProgID + shellex 挂 ProgID 下）。
        // ProgID 故意不带 shell\open：双击仍是系统「选择打开方式」弹窗，不绑定程序。
        // SystemFileAssociations 那份是保险带（文档定义的按扩展名位置），留着无害。
        SetValues(rf(@"Software\Classes\LitematicaViewer.Projection"),
            ("", "Litematica Projection File"));
        SetValues(rf($@"Software\Classes\LitematicaViewer.Projection\shellex\{PreviewIid}"), ("", Clsid));
        SetValues(rf(@"Software\Classes\.litematic"), ("", "LitematicaViewer.Projection"));
        SetValues(rf($@"Software\Classes\SystemFileAssociations\.litematic\shellex\{PreviewIid}"), ("", Clsid));
        SetValues(rf($@"Software\Classes\CLSID\{Clsid}"),
            ("", "Litematica Preview Handler"),
            ("AppID", SurrogateAppId),
            ("DisplayName", "Litematica Preview Handler"));
        SetValues(rf($@"Software\Classes\CLSID\{Clsid}\InprocServer32"),
            ("", dllPath),
            ("ThreadingModel", "Apartment"));
        // 不再自建 AppID 键：DllSurrogate 配置用系统共享的那份（HKLM），结构对齐微软自家 handler。

        // 预览 handler 注册三步曲的第三步：把 CLSID 登记进系统 handler 清单。
        // 预览宿主只考虑清单里的 handler——没有这一条，shellex 挂哪都不被询问
        // （本机 12 个能工作的 handler 全在 HKLM 这份清单里，实测缺失时零激活）。
        SetValues(@"Software\Microsoft\Windows\CurrentVersion\PreviewHandlers",
            (Clsid, "Litematica Preview Handler"));
        Log("install: registry done");

        // 系统卸载入口（设置→应用）。
        SetValues(rf($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{UninstallKeyName}"),
            ("DisplayName", "Litematica Preview Handler"),
            ("DisplayVersion", Version),
            ("Publisher", "LitematicaViewer"),
            ("UninstallString", $"\"{exePath}\" /uninstall"),
            ("NoModify", 1),
            ("NoRepair", 1),
            ("EstimatedSize", (payload.Length + angle.Length + jar.Length + 4 * 1024 * 1024) / 1024)); // KB，含 AOT 运行时粗估

        // 装完必重启 Explorer：预览宿主对新注册表与 dll 的感知只有重启才可靠，
        // 不重启的现象就是「装完了窗格没反应/资源管理器假死」，问都不用问。
        RestartExplorer();

        if (!quiet)
        {
            Box(0x40, "安装完成，资源管理器已重启。");
        }
    }

    private static byte[]? ReadResource(string logicalName)
    {
        var assembly = typeof(Program).Assembly;
        var stream = assembly.GetManifestResourceStream(logicalName);
        if (stream is null)
        {
            // 兜底：嵌入名被 MSBuild 加 RootNamespace 前缀时（csproj 换嵌入方式的坑），
            // 按后缀找唯一命中，别让 payload 静默变 null 装出个空壳。
            var match = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(logicalName, StringComparison.Ordinal));
            if (match is not null)
            {
                Log($"resource '{logicalName}' found as '{match}' (prefixed)");
                stream = assembly.GetManifestResourceStream(match);
            }
        }

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

        // 只删自己注册的东西；历史挂点一并清。
        Log("uninstall-go: deleting registry");

        // prevhost 映射着装好的 dll，不先杀则目录永远删不干净（部署脚本里
        // 「DIR STILL THERE」的根因）。
        KillPrevhost();
        TryDelete(rf(@"Software\Classes\LitematicaViewer.Projection"));
        TryDelete(rf(@"Software\Classes\.litematic"));
        TryDelete(rf($@"Software\Classes\SystemFileAssociations\.litematic\shellex\{PreviewIid}"));
        TryDelete(rf(@"Software\Classes\SystemFileAssociations\.litematic\shellex"));
        TryDelete(rf(@"Software\Classes\SystemFileAssociations\.litematic"));
        TryDelete(rf($@"Software\Classes\.litematic\shellex\{PreviewIid}"));
        TryDelete(rf(@"Software\Classes\.litematic\shellex"));
        TryDelete(rf($@"Software\Classes\CLSID\{Clsid}"));
        TryDelete(rf($@"Software\Classes\AppID\{Clsid}"));
        TryDeleteValue(@"Software\Microsoft\Windows\CurrentVersion\PreviewHandlers", Clsid);
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

        // 卸载完同样必重启 Explorer：清掉壳里残留的 handler 激活状态。
        RestartExplorer();

        if (!quiet)
        {
            Box(0x40, "卸载完成，资源管理器已重启。");
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

    // PreviewHandlers 是系统共享键（HKLM 12 个自家 handler 也在里面），只能删自己的值。
    private static void TryDeleteValue(string path, string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(path, writable: true);
            key?.DeleteValue(name, throwOnMissingValue: false);
        }
        catch (Exception)
        {
            // 同上：不存在 = 目标状态
        }
    }

    private static void RestartExplorer()
    {
        // 每步自吞异常：重启只是收尾体验，失败不该把装好的安装判成 rc 1。
        // 但每步都落日志，远端排查时能看见卡在哪。
        try
        {
            var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
            Log($"restart-explorer: sys={sys}");
            Process.Start(new ProcessStartInfo(Path.Combine(sys, "taskkill.exe"), "/f /im explorer.exe")
            {
                CreateNoWindow = true,
            });
            // 等旧壳真退干净再起新的：立刻 Start 会出现新旧两份 shell 抢任务栏。
            for (var i = 0; i < 30 && Process.GetProcessesByName("explorer").Length > 0; i++)
            {
                Thread.Sleep(100);
            }

            Log("restart-explorer: launching new shell");
            // explorer.exe 在 C:\Windows 下，不在 System32——跟 taskkill 同目录取
            // 会拿不到文件（「装完 Explorer 没起来要手动重启」的原始根因）。
            // 必须 CreateProcess（UseShellExecute=false）：ShellExecute 路径在无壳
            // 状态下静默不启动（返回成功但进程没出现，实测）。
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            Process.Start(new ProcessStartInfo(Path.Combine(windows, "explorer.exe"))
            {
                UseShellExecute = false,
            });
            Log($"restart-explorer: done from {windows}");
        }
        catch (Exception ex)
        {
            Log($"restart-explorer FAILED {ex.GetType().Name}: {ex.Message}");
        }
    }

    // 杀掉共享预览代理宿主并等它退净：它映射着我们装的 dll，
    // 不等退净，覆盖写/删目录都会撞 IOException。
    private static void KillPrevhost()
    {
        Log("kill-prevhost: enter");
        try
        {
            var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
            Log($"kill-prevhost: sys={sys}");
            Process.Start(new ProcessStartInfo(Path.Combine(sys, "taskkill.exe"), "/f /im prevhost.exe")
            {
                CreateNoWindow = true,
            });
            Log("kill-prevhost: taskkill spawned");
        }
        catch (Exception ex)
        {
            Log($"kill-prevhost FAILED {ex.GetType().Name}: {ex.Message}");
            return;
        }

        var left = 0;
        for (var i = 0; i < 30; i++)
        {
            left = Process.GetProcessesByName("prevhost").Length;
            if (left == 0) break;
            Thread.Sleep(100);
        }
        Log($"kill-prevhost: done left={left}");
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint hwnd, string text, string caption, uint type);

    private static int Box(uint type, string text) =>
        MessageBoxW(0, text, "Litematica Preview", type);
}
