using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace LitematicaViewer.ShellPreview;

// DLL 的 COM 导出入口。NativeAOT 会把带 EntryPoint 的 UnmanagedCallersOnly
// 静态方法放进导出表，不需要 .def 文件。
internal static unsafe partial class Exports
{
    // 固定 CLSID：注册表里 .litematic 的预览处理器指向它，改了就得重新注册。
    internal const string ClassId = "8E5B1A47-3F2E-4C6D-9A1B-7C2D5E8F0A31";
    private static readonly Guid ClassGuid = new(ClassId);
    private static readonly Guid IClassFactoryGuid = new("00000001-0000-0000-C000-000000000046");

    private static readonly StrategyBasedComWrappers Wrappers = new();

    [UnmanagedCallersOnly(EntryPoint = "DllGetClassObject")]
    public static int DllGetClassObject(Guid* clsid, Guid* riid, nint* ppvObject)
    {
        Log($"DGC clsid={*clsid} riid={*riid}");
        if (ppvObject is null)
        {
            return unchecked((int)0x80004003); // E_POINTER
        }

        *ppvObject = 0;
        if (*clsid != ClassGuid)
        {
            Log("DGC classid mismatch");
            return unchecked((int)0x80040111); // CLASS_E_CLASSNOTAVAILABLE
        }

        nint unknown = Wrappers.GetOrCreateComInterfaceForObject(
            new ClassFactory(), CreateComInterfaceFlags.None);
        var hr = QueryInterface(unknown, *riid, ppvObject);
        Log($"DGC qi hr=0x{hr:X8} out=0x{*ppvObject:X}");
        return hr;
    }

    // COM 链路问题的取证口。prevhost 由 DCOM 启动器拉起，环境变量可能不是交互用户的
    // （TEMP 解析到别处且写不进去，全部异常被吞 → 日志静默消失），所以双路写：
    // 模块安装目录（我们自己装的，必可写）+ TEMP，谁成功写谁。
    internal static void Log(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {message}\n";
        WriteTry(Path.Combine(SceneLoader.SelfModuleDirectory(), "shellpreview.log"), line);
        WriteTry(Path.Combine(Path.GetTempPath(), "shellpreview.log"), line);
    }

    private static void WriteTry(string path, string line)
    {
        try
        {
            File.AppendAllText(path, line);
        }
        catch
        {
            // 日志失败不能拖垮 COM 调用
        }
    }

    // 返回 S_FALSE：让宿主常驻我们不卸载。真卸载要保证所有对象都死透，
    // 这个判断出错就是 explorer 崩溃，不值得赌。
    [UnmanagedCallersOnly(EntryPoint = "DllCanUnloadNow")]
    public static int DllCanUnloadNow() => 1; // S_FALSE

    internal static int QueryInterface(nint unknown, Guid riid, nint* ppv)
    {
        nint* vtbl = *(nint**)unknown;
        var qi = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)vtbl[0];
        return qi(unknown, &riid, ppv);
    }

    // GeneratedComClass 是 CCW 的注册入口：没有它，StrategyBasedComWrappers
    // 生成的 QI 对所有非 IUnknown 接口一律 E_NOINTERFACE。
    [GeneratedComClass]
    private sealed partial class ClassFactory : IClassFactory
    {
        public int CreateInstance(nint outer, Guid* riid, nint* ppvObject)
        {
            Log($"CI enter riid={*riid}");
            if (ppvObject is null)
            {
                return unchecked((int)0x80004003);
            }

            *ppvObject = 0;
            if (outer != 0)
            {
                return unchecked((int)0x80040110); // CLASS_E_NOAGGREGATION
            }

            Log("CI creating handler");
            nint unknown = Wrappers.GetOrCreateComInterfaceForObject(
                new PreviewHandler(), CreateComInterfaceFlags.None);
            Log($"CI ccw=0x{unknown:X}");
            var hr = QueryInterface(unknown, *riid, ppvObject);
            Log($"CI qi hr=0x{hr:X8} out=0x{*ppvObject:X}");
            return hr;
        }

        public int LockServer(int fLock) => 0; // S_OK，常驻策略下没语义
    }
}
