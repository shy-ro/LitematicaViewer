// Sample 的 DocumentSource 直接读 Program.ShotPath 打取证日志。共享源文件链接编译进
// 本程序集时带着它原地的命名空间，这里在同一个命名空间下补一份只读的宿主配置桥，
// 把本工程入口（LitematicaViewer.SamplePreviewer.Program）解析出的参数转接过去。
// 两个 Program 各在各自的命名空间，互不相见。
namespace LitematicaViewer.Previewer.Sample;

internal static class Program
{
    internal static string? ShotPath => SamplePreviewer.Program.ShotPath;

    internal static float ShotDistanceFactor => SamplePreviewer.Program.ShotDistanceFactor;
}
