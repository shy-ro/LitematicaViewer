using System.Collections.Immutable;
using System.Diagnostics;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using LitematicaViewer.Core.Model;
using LitematicaViewer.Previewer;
using LitematicaViewer.Previewer.Sample;

namespace LitematicaViewer.SamplePreviewer;

// 只做展台的预览器宿主：没有模式切换、没有设置面板、没有拾取与自检。
// 与 Sample 的 MainWindow 平行存在而不是复用它——那份代码的骨架就是「两种模式来回切」，
// 把一半拆掉剩下的比新写还难读。共用的零件（相机模型、转台控制器、数据源）以
// 链接源文件的方式进来，行为与 Sample 完全一致，见 csproj。
public partial class MainWindow : Window
{
    private readonly PreviewerInputAdapter _input;
    private readonly CameraModel _camera;
    private readonly DocumentSource _source = new();

    // 展台那一个。构造时就摆好相机、点亮光环；载入文件后整体重建换目标表。
    private TurntableController? _turntable;

    private ImmutableArray<ShowcaseTarget> _showcaseTargets = ShowcaseTargets.All;

    // 状态条是不是还在「没有文件」的演示态：ImmutableArray 是值类型，不拿 ReferenceEquals 比。
    private bool _showingDemo = true;

    public MainWindow()
    {
        InitializeComponent();

        _input = new PreviewerInputAdapter(Viewport);

        _camera = new CameraModel(CameraState.Default, Vector3.Zero);
        Viewport.SetCamera(_camera.Camera);

#if DEBUG
        CameraModel.VerifyCameraMath();
#endif

        // 展台手势从第一帧就定死：按住左键拖。这个宿主没有另一种视角，
        // 也就没有「切回来要恢复手势」这回事。
        _input.Gesture = LookGesture.DragPrimaryButton;

        // 左右键在 region 之间换页。展台的滚轮（缩放）由转台控制器自己订阅 Scrolled。
        Viewport.KeyChanged += OnViewportKeyChanged;

        _source.Completed += OnDocumentCompleted;

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, OnDrop);

        Activated += OnActivated;
        Deactivated += OnDeactivated;
        Closed += OnClosed;

        // 转台在窗口真正开出来之后再上：构造即 Frame（SetCamera/SetPedestal），
        // 而 GL 控件要等宿主布局完成才 init——太早调用不炸，但第一批调用会落在
        // 「控件还没有上下文」的窗口期里，与 Sample 的时序保持一致省得对账。
        var initial = Program.InitialLitematic;
        Opened += OnFirstOpened;

        void OnFirstOpened(object? sender, EventArgs e)
        {
            _turntable = new TurntableController(Viewport, _camera, _showcaseTargets);

            if (initial is not null)
                _source.Load(initial);

            // 开窗这一份取景之后才需要刷一次状态条。
            UpdateStatus();
        }
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var path = (e.DataTransfer.TryGetFiles() ?? Enumerable.Empty<IStorageItem>())
            .Select(f => f.Path.LocalPath)
            .FirstOrDefault(p => p.EndsWith(".litematic", StringComparison.OrdinalIgnoreCase));

        if (path is not null)
            _source.Load(path);
    }

    private void OnDocumentCompleted(string path, DocumentSource.LoadedDocument? document)
    {
        if (document is null)
        {
            StatusText.Text = $"载入失败：{Path.GetFileName(path)}（看日志）";
            if (Program.ShotPath is not null)
                ((IClassicDesktopStyleApplicationLifetime?)Application.Current?.ApplicationLifetime)?.Shutdown();

            return;
        }

        var empty = document.MergedIndices.Length == 0;
        Viewport.SetMesh(
            empty ? null : document.MergedVertices,
            empty ? null : document.MergedIndices,
            empty ? null : document.AtlasLevels,
            empty ? 0 : document.AtlasWidth,
            empty ? 0 : document.AtlasHeight);

        _showcaseTargets = document.Targets.IsEmpty ? ShowcaseTargets.All : document.Targets;
        _showingDemo = document.Targets.IsEmpty;

        // 远平面按模型尺寸重设：默认 far=100 只够演示立方体（与 Sample 同一条算式）。
        var far = MathF.Max(100f, document.WholeRadius * 20f + 64f);
        _camera.Reset(_camera.Camera with { Far = far });

        // 转台整体重建：目标表换新的一张，取景与光环跟着 Frame() 落位。
        _turntable?.Dispose();
        _turntable = new TurntableController(Viewport, _camera, _showcaseTargets);

        // --shot-dist：在转台取景之上乘一个距离倍数，复现「凑近看」的画面。
        if (Program.ShotDistanceFactor != 1f && _turntable is { } turntable)
        {
            var target = turntable.Current;
            _camera.FrameTurntable(
                target.Centre,
                TurntableController.DefaultPitch,
                target.Radius * TurntableController.FrameFactor * Program.ShotDistanceFactor);
            Viewport.SetCamera(_camera.Camera);
        }

        UpdateStatus();

        if (Program.ShotPath is not null && !empty)
            Viewport.RequestCapture((pixels, width, height) =>
            {
                ShotWriter.Write(Program.ShotPath!, pixels, width, height);
                Debug.WriteLine($"[PREVIEW][shot] saved={Program.ShotPath} size={width}x{height}");
                ((IClassicDesktopStyleApplicationLifetime?)Application.Current?.ApplicationLifetime)?.Shutdown();
            });
    }

    private void OnViewportKeyChanged(Key key, bool isDown)
    {
        if (!isDown) return;

        switch (key)
        {
            case Key.Left:
                _turntable!.Previous();
                UpdateStatus();
                break;

            case Key.Right:
                _turntable!.Next();
                UpdateStatus();
                break;
        }
    }

    private void UpdateStatus()
    {
        StatusText.Text = _showingDemo || _turntable is not { } turntable
            ? "（拖入 .litematic 或用命令行传路径）"
            : $"{turntable.Current.Name}  {turntable.Index + 1}/{turntable.Count}";
    }

    private void OnActivated(object? sender, EventArgs e) =>
        Debug.WriteLine($"[PREVIEW][window.activated] client={ClientSize}");

    // 失活时清手势与角速度，语义与 Sample 一致：还按着的拖动不该在切回来之后接着生效。
    private void OnDeactivated(object? sender, EventArgs e)
    {
        _input.ReleaseLook();
        _turntable?.ReleaseDrag();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Viewport.KeyChanged -= OnViewportKeyChanged;
        _turntable?.Dispose();
        _input.Dispose();
    }
}
