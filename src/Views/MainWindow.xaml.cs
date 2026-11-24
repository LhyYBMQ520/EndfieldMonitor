using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using EndfieldMonitor.Rendering;
using EndfieldMonitor.Services;
using EndfieldMonitor.ViewModels;
using static EndfieldMonitor.Interop.NativeMethods;

namespace EndfieldMonitor.Views;

/// <summary>
/// 透明桌面挂件窗口。
/// - AllowsTransparency=True + 透明背景 → WS_EX_LAYERED 逐像素 alpha 窗口；
/// - 锁定：追加 WS_EX_TRANSPARENT | WS_EX_NOACTIVATE，整个窗口（含可见像素）对鼠标透明，输入直达下层进程；
/// - 所有扩展样式修改都在读取原值的基础上增删位标志；
/// - 位置以物理像素保存并按当前显示器布局校验。
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly WidgetState _state;
    private IntPtr _hwnd;
    private HwndSource? _source;
    private Storyboard? _bandStoryboard;
    private bool _bandRunning;
    private double _shrink = 1;
    private DateTime _popupClosedAt;
    private bool _exiting;
    private bool _appliedLocked;
    private readonly System.Windows.Threading.DispatcherTimer _positionSaveTimer;

    /// <summary>Alt+F4 等关闭请求：统一走应用退出流程。</summary>
    public event Action? ExitRequested;

    public MainWindow(MainViewModel vm, WidgetState state)
    {
        _vm = vm;
        _state = state;
        InitializeComponent();
        DataContext = vm;

        TracksLayer.Drawing = GaugeDrawings.Tracks;
        CoreLayer.Drawing = GaugeDrawings.Core;
        OverlayLayer.Drawing = GaugeDrawings.Overlay;
        TopBandLayer.Drawing = GaugeDrawings.BuildBand(GaugeGeometry.TopBand);
        BottomBandLayer.Drawing = GaugeDrawings.BuildBand(GaugeGeometry.BottomBand);

        SettingsPopup.CustomPopupPlacementCallback = PlaceSettings;
        SettingsPanel.DataContext = new SettingsViewModel(vm);

        _positionSaveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _positionSaveTimer.Tick += (_, _) => { _positionSaveTimer.Stop(); SaveCurrentPosition(); };

        vm.PropertyChanged += OnViewModelChanged;
        vm.ScaleChanged += () => { ApplySize(); EnsureOnScreen(); };
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        LocationChanged += (_, _) => { if (_hwnd != IntPtr.Zero && IsVisible) { _positionSaveTimer.Stop(); _positionSaveTimer.Start(); } };
        DpiChanged += (_, _) => UpdateParticlePixelScale();
        SizeChanged += (_, _) => UpdateParticlePixelScale();
        Loaded += (_, _) => { StartBandAnimation(); UpdateParticlePixelScale(); };
        IsVisibleChanged += (_, _) => OnVisibilityChanged();
    }

    public IntPtr Handle => _hwnd;

    // ── 原生窗口初始化 ──────────────────────────────────────────
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source.AddHook(WndProc);

        // 工具窗口：不出现在任务栏与 Alt+Tab；保留 WPF 设置的 WS_EX_LAYERED。
        long ex = GetExStyle();
        ex |= WS_EX_TOOLWINDOW;
        ex &= ~WS_EX_APPWINDOW;
        SetExStyle(ex);

        ApplySize();
        PlaceInitially();
        ApplyState();
    }

    private long GetExStyle() => GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();

    private void SetExStyle(long value)
    {
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(value));
        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_NOOWNERZORDER);
    }

    /// <summary>诊断：当前扩展样式（供测试脚本与状态面板核验）。</summary>
    public long CurrentExStyle => _hwnd == IntPtr.Zero ? 0 : GetExStyle();

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_DISPLAYCHANGE:
                Dispatcher.BeginInvoke(EnsureOnScreen, System.Windows.Threading.DispatcherPriority.Background);
                break;
            case WM_SETTINGCHANGE:
                if (wParam.ToInt64() == SPI_SETWORKAREA)
                    Dispatcher.BeginInvoke(EnsureOnScreen, System.Windows.Threading.DispatcherPriority.Background);
                // 系统“显示动画”开关变化（SPI_SETCLIENTAREAANIMATION = 0x1043）
                if (wParam.ToInt64() == 0x1043)
                    Dispatcher.BeginInvoke(RefreshMotionPreference, System.Windows.Threading.DispatcherPriority.Background);
                break;
            case WM_DPICHANGED:
                // WPF 已按建议矩形处理缩放；随后再做可达性校验。
                Dispatcher.BeginInvoke(EnsureOnScreen, System.Windows.Threading.DispatcherPriority.Background);
                break;
            case WM_POWERBROADCAST:
                long ev = wParam.ToInt64();
                if (ev == PBT_APMPOWERSTATUSCHANGE || ev == PBT_APMRESUMEAUTOMATIC)
                    Dispatcher.BeginInvoke(_vm.RefreshBattery);
                break;
        }
        return IntPtr.Zero;
    }

    // ── 可见性与窗口状态 ────────────────────────────────────────
    /// <summary>显示并提升窗口；仅在明确请求且未锁定时尝试激活。</summary>
    public void ShowAndRaise(bool requestActivation)
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;

        SetWindowPos(_hwnd, Topmost ? HWND_TOPMOST : HWND_TOP, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_NOOWNERZORDER);

        if (requestActivation && !_state.Locked) Activate();
    }

    /// <summary>把 <see cref="WidgetState"/> 应用到原生窗口。</summary>
    public void ApplyState()
    {
        if (_hwnd == IntPtr.Zero) return;
        bool locked = _state.Locked;

        if (locked && !_appliedLocked) CloseTransientUi();

        long ex = GetExStyle();
        long desired = ex | WS_EX_LAYERED | WS_EX_TOOLWINDOW;
        desired = locked ? desired | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE : desired & ~(WS_EX_TRANSPARENT | WS_EX_NOACTIVATE);
        if (desired != ex) SetExStyle(desired);
        _appliedLocked = locked;
        App.Trace($"ApplyState locked={locked} topmost={_state.Topmost} visible={_state.Visible} ex 0x{ex:X} -> 0x{GetExStyle():X}");

        // 锁定后禁用全部内部控件：不再响应键盘，也不再显示工具提示；关闭按钮隐藏，避免暗示仍可点击。
        Root.IsEnabled = !locked;
        CloseButton.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
        if (locked) Keyboard.ClearFocus();

        // 原生 HWND_TOPMOST（WPF Topmost 内部即调用 SetWindowPos），与锁定独立。
        if (Topmost != _state.Topmost) Topmost = _state.Topmost;

        if (_state.Visible && !IsVisible) Show();
        else if (!_state.Visible && IsVisible)
        {
            CloseTransientUi();
            Hide();
        }
    }

    /// <summary>锁定 / 隐藏 / 退出前关闭所有可能独立拦截鼠标的弹出层。</summary>
    public void CloseTransientUi()
    {
        SettingsPopup.IsOpen = false;
        foreach (var tip in new[] { TopSlotTip, BottomSlotTip, ReportTip, CloseTip })
            tip.IsOpen = false;
        if (Mouse.Captured != null) Mouse.Capture(null);
        if (ContextMenu != null) ContextMenu.IsOpen = false;
    }

    private void OnVisibilityChanged()
    {
        bool visible = IsVisible;
        Particles.SetHostVisible(visible);
        _vm.SetPaused(!visible);
        if (visible)
        {
            ResumeBand();
            EnsureOnScreen();
        }
        else PauseBand();
    }

    // ── 黑弧往返动画 ────────────────────────────────────────────
    private void StartBandAnimation()
    {
        if (_bandStoryboard != null) return;
        _bandStoryboard = new Storyboard();
        // DesiredFrameRate 只在根时间线上生效。黑弧角速度约 1.8°/s（100% 缩放下外缘约 9 px/s），
        // 20 FPS 时每帧位移 < 0.5 px，视觉上仍是连续匀速；透明分层窗口每帧需整窗回读，帧率直接决定 CPU 占用。
        Timeline.SetDesiredFrameRate(_bandStoryboard, BandFrameRate);
        AddBand(_bandStoryboard, TopBandLayer, GaugeGeometry.TopBand);
        AddBand(_bandStoryboard, BottomBandLayer, GaugeGeometry.BottomBand);
        // 动画只创建一次；后续数据刷新、设置、锁定、置顶都不会重建或重启。
        _bandStoryboard.Begin(this, true);
        _bandRunning = true;
        RefreshMotionPreference();
    }

    private static void AddBand(Storyboard sb, FrameworkElement target, GaugeGeometry.BandSpec band)
    {
        var anim = new DoubleAnimation(band.SweepFrom, band.SweepTo, TimeSpan.FromSeconds(GaugeGeometry.BandOneWaySeconds))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            // 线性匀速：不设置缓动函数，加速 / 减速比例为 0。
            AccelerationRatio = 0,
            DecelerationRatio = 0,
        };
        Timeline.SetDesiredFrameRate(anim, BandFrameRate);
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, new PropertyPath("RenderTransform.Angle"));
        sb.Children.Add(anim);
    }

    private bool MotionAllowed => SystemParameters.ClientAreaAnimation;

    public static int BandFrameRate =>
        int.TryParse(Environment.GetEnvironmentVariable("ENDFIELD_BAND_FPS"), out int f) && f is >= 5 and <= 60 ? f : 20;

    private void PauseBand()
    {
        if (_bandStoryboard != null && _bandRunning)
        {
            _bandStoryboard.Pause(this);
            _bandRunning = false;
        }
    }

    private void ResumeBand()
    {
        if (_bandStoryboard != null && !_bandRunning && MotionAllowed && IsVisible)
        {
            _bandStoryboard.Resume(this);
            _bandRunning = true;
        }
    }

    private void RefreshMotionPreference()
    {
        if (_bandStoryboard == null) return;
        if (!MotionAllowed)
        {
            // 与参考页 prefers-reduced-motion 一致：停止动画，回到弧带原始角度（偏移 0°，仍位于色块内）。
            PauseBand();
            _bandStoryboard.Seek(this, TimeSpan.FromSeconds(GaugeGeometry.BandOneWaySeconds * (0 - GaugeGeometry.TopBand.SweepFrom) / (GaugeGeometry.TopBand.SweepTo - GaugeGeometry.TopBand.SweepFrom)), TimeSeekOrigin.BeginTime);
        }
        else ResumeBand();
        Particles.RefreshMotionPreference();
    }

    /// <summary>当前黑弧旋转角（供诊断 / 测试）。</summary>
    public (double Top, double Bottom) BandAngles =>
        (((RotateTransform)TopBandLayer.RenderTransform).Angle, ((RotateTransform)BottomBandLayer.RenderTransform).Angle);

    // ── 尺寸 / 位置 / DPI ──────────────────────────────────────
    private const double CloseButtonGap = 6;
    private double DesignToDip => GaugeGeometry.DefaultDesignToDip * _vm.Scale;
    private double LogicalWidth => GaugeGeometry.TightViewport.Width * DesignToDip;
    private double LogicalHeight => (GaugeGeometry.TightViewport.Bottom - ViewportTop) * DesignToDip;

    // 小比例下向上多保留一些画布，让不随仪表缩放的关闭按钮仍能放在监控条上方。
    private double ViewportTop => Math.Min(GaugeGeometry.TightViewport.Top,
        Canvas.GetTop(TopSlotHit) - (CloseButton.Height + 2 * CloseButtonGap) / DesignToDip);

    private void ApplySize()
    {
        var viewport = GaugeGeometry.TightViewport;
        double top = ViewportTop;
        GaugeViewport.Width = viewport.Width;
        GaugeViewport.Height = viewport.Bottom - top;
        Canvas.SetLeft(Design, -viewport.Left);
        Canvas.SetTop(Design, -top);

        Width = LogicalWidth * _shrink;
        Height = LogicalHeight * _shrink;

        double scale = DesignToDip * _shrink;
        double slotRight = Canvas.GetLeft(TopSlotHit) + TopSlotHit.Width;
        double buttonLeft = (slotRight - viewport.Left) * scale - CloseButton.Width;
        double buttonTop = (Canvas.GetTop(TopSlotHit) - top) * scale - CloseButton.Height - CloseButtonGap;
        CloseButton.Margin = new Thickness(Math.Max(0, buttonLeft), Math.Max(0, buttonTop), 0, 0);
        UpdateParticlePixelScale();
    }

    private (int W, int H) PixelSizeForDpi(uint dpi) =>
        ((int)Math.Ceiling(LogicalWidth * dpi / 96.0), (int)Math.Ceiling(LogicalHeight * dpi / 96.0));

    private void PlaceInitially()
    {
        var monitors = WindowPlacement.EnumerateMonitors();
        PxRect desired;
        if (_vm.Settings.WindowX is int x && _vm.Settings.WindowY is int y)
        {
            uint dpi = DpiAt(x, y, monitors);
            var (w, h) = PixelSizeForDpi(dpi);
            desired = new PxRect(x, y, w, h);
        }
        else desired = WindowPlacement.DefaultRect(monitors, PixelSizeForDpi);
        ApplyPlacement(WindowPlacement.Place(desired, monitors, PixelSizeForDpi));
    }

    private static uint DpiAt(int x, int y, List<MonitorArea> monitors)
    {
        foreach (var m in monitors)
            if (x >= m.Bounds.X && x < m.Bounds.Right && y >= m.Bounds.Y && y < m.Bounds.Bottom) return m.Dpi;
        return monitors.FirstOrDefault(m => m.Primary).Dpi is uint d and > 0 ? d : 96;
    }

    /// <summary>显示器或工作区变化、DPI 变化、缩放变化后保证窗口可达。</summary>
    public void EnsureOnScreen()
    {
        if (_hwnd == IntPtr.Zero || _exiting) return;
        if (!GetWindowRect(_hwnd, out var r)) return;
        var monitors = WindowPlacement.EnumerateMonitors();
        var current = new PxRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        var result = WindowPlacement.Place(current, monitors, PixelSizeForDpi);
        ApplyPlacement(result);
    }

    private void ApplyPlacement(PlacementResult p)
    {
        if (Math.Abs(p.Shrink - _shrink) > 0.001)
        {
            _shrink = p.Shrink;
            ApplySize();
        }
        if (GetWindowRect(_hwnd, out var r) && r.Left == p.Rect.X && r.Top == p.Rect.Y) return;
        SetWindowPos(_hwnd, IntPtr.Zero, p.Rect.X, p.Rect.Y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    public void ResetPosition()
    {
        var monitors = WindowPlacement.EnumerateMonitors();
        _shrink = 1;
        ApplySize();
        var rect = WindowPlacement.DefaultRect(monitors, PixelSizeForDpi);
        ApplyPlacement(WindowPlacement.Place(rect, monitors, PixelSizeForDpi));
        SaveCurrentPosition();
    }

    private void SaveCurrentPosition()
    {
        if (_hwnd == IntPtr.Zero || _exiting || WindowState == WindowState.Minimized) return;
        if (GetWindowRect(_hwnd, out var r)) _vm.SetWindowPosition(r.Left, r.Top);
    }

    private void UpdateParticlePixelScale()
    {
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        Particles.PixelScale = DesignToDip * _shrink * dpi;
    }

    // ── 拖动 ────────────────────────────────────────────────────
    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 按钮会自行处理并标记 Handled；只有非交互区域会到达这里。
        if (e.Handled || _state.Locked || e.ClickCount > 1) return;
        if (e.OriginalSource is DependencyObject d && IsInteractive(d)) return;
        try
        {
            DragMove(); // 系统移动循环：移动的是真正的桌面窗口，松开即结束
        }
        catch (InvalidOperationException) { /* 鼠标已松开 */ }
        SaveCurrentPosition();
        EnsureOnScreen();
    }

    private static bool IsInteractive(DependencyObject d)
    {
        for (var cur = d; cur != null; cur = cur is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(cur) : LogicalTreeHelper.GetParent(cur))
            if (cur is ButtonBase or TextBox or Popup or ScrollBar) return true;
        return false;
    }

    // ── 设置面板 ────────────────────────────────────────────────
    private void ReportButton_Click(object sender, RoutedEventArgs e)
    {
        // StaysOpen=False 时点击按钮本身会先关闭面板；短时间内的点击视为“关闭”而非重新打开。
        if ((DateTime.UtcNow - _popupClosedAt).TotalMilliseconds < 250) return;
        if (SettingsPopup.IsOpen) SettingsPopup.IsOpen = false;
        else OpenSettings();
    }

    /// <summary>打开设置面板（调用方需先确保窗口已显示且已解锁）。</summary>
    public void OpenSettings()
    {
        if (_state.Locked || !_state.Visible) return;
        // 面板需要键盘输入：显式激活一次窗口（未锁定时不带 WS_EX_NOACTIVATE）。
        Activate();
        if (SettingsPanel.DataContext is SettingsViewModel svm)
        {
            _vm.ActiveSettings = svm;
            svm.SyncAll();
        }
        SettingsPopup.IsOpen = true;
    }

    private void SettingsPopup_Opened(object? sender, EventArgs e)
    {
        ReportButton.Tag = "Open";
        SettingsPanel.FocusFirst();
    }

    private void SettingsPopup_Closed(object? sender, EventArgs e)
    {
        _popupClosedAt = DateTime.UtcNow;
        ReportButton.Tag = null;
        _vm.ActiveSettings = null;
        if (!_state.Locked && IsVisible && IsActive) ReportButton.Focus();
    }

    private void SettingsPanel_CloseRequested(object? sender, EventArgs e)
    {
        SettingsPopup.IsOpen = false;
        if (!_state.Locked) ReportButton.Focus();
    }

    private CustomPopupPlacement[] PlaceSettings(Size popupSize, Size targetSize, Point offset)
    {
        // 参考页面板位于胶囊右下（left 60%、top 46%）。WPF 会依次尝试这些位置，选择完整位于屏幕内的第一个；
        // 都放不下时再选择可见面积最大的一个，因此小工作区下面板仍然可操作。
        double belowY = targetSize.Height * 1.32;
        double aboveY = -popupSize.Height - targetSize.Height * 0.3;
        double rightX = targetSize.Width * 1.07;
        double leftX = -popupSize.Width - targetSize.Width * 0.07;
        double centerY = (targetSize.Height - popupSize.Height) / 2;
        return new[]
        {
            new CustomPopupPlacement(new Point(rightX, belowY), PopupPrimaryAxis.Horizontal),
            new CustomPopupPlacement(new Point(rightX, centerY), PopupPrimaryAxis.Vertical),
            new CustomPopupPlacement(new Point(leftX, belowY), PopupPrimaryAxis.Horizontal),
            new CustomPopupPlacement(new Point(leftX, centerY), PopupPrimaryAxis.Vertical),
            new CustomPopupPlacement(new Point(rightX, aboveY), PopupPrimaryAxis.Horizontal),
            new CustomPopupPlacement(new Point(leftX, aboveY), PopupPrimaryAxis.Horizontal),
        };
    }

    // ── 其它 ────────────────────────────────────────────────────
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ParticlesEnabled))
            UpdatePerformanceText();
    }

    public void UpdatePerformanceText()
    {
        _vm.PerformanceText = _vm.ParticlesEnabled
            ? $"粒子 {Particles.MeasuredFps:0} FPS · {Particles.AverageFrameMs:0.0} ms/帧（目标 {Controls.ParticleCloud.TargetFps:0} FPS）· 黑弧 {BandFrameRate} FPS"
            : $"粒子动画已关闭（静态图形）· 黑弧 {BandFrameRate} FPS";
        App.Trace("perf: " + _vm.PerformanceText);
    }

    /// <summary>
    /// 诊断：把仪表主体按设计尺寸渲染为 PNG（可选底色），用于与参考页面逐像素对照和透明通道核验。
    /// </summary>
    public void SaveSnapshot(string path, Color? background, double scale = 1)
    {
        int w = (int)Math.Round(GaugeGeometry.DesignWidth * scale), h = (int)Math.Round(GaugeGeometry.DesignHeight * scale);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            if (background is Color bg) dc.DrawRectangle(new SolidColorBrush(bg), null, new Rect(0, 0, w, h));
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.DrawRectangle(new VisualBrush(Design)
            {
                Stretch = Stretch.Fill,
                Viewbox = new Rect(0, 0, GaugeGeometry.DesignWidth, GaugeGeometry.DesignHeight),
                ViewboxUnits = BrushMappingMode.Absolute,
            },
                null, new Rect(0, 0, GaugeGeometry.DesignWidth, GaugeGeometry.DesignHeight));
            dc.Pop();
        }
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => ExitRequested?.Invoke();

    protected override void OnClosing(CancelEventArgs e)
    {
        App.Trace($"OnClosing exiting={_exiting}");
        if (!_exiting)
        {
            // 外部关闭请求（Alt+F4、任务管理器“结束任务”、taskkill）统一走应用退出流程。
            // 不在 Closing 中同步执行清理，以免与正在关闭的 Popup / 窗口嵌套。
            e.Cancel = true;
            Dispatcher.BeginInvoke(() => ExitRequested?.Invoke());
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        // 任何路径导致主窗口真正关闭时，都确保应用退出（避免只剩托盘与后台线程）。
        if (!_exiting) ExitRequested?.Invoke();
    }

    /// <summary>退出清理：关闭弹出层、停止动画与渲染回调、移除窗口钩子，然后关闭窗口。</summary>
    public void Shutdown()
    {
        if (_exiting) return;
        _positionSaveTimer.Stop();
        if (IsVisible) SaveCurrentPosition();
        _exiting = true;
        CloseTransientUi();
        _bandStoryboard?.Stop(this);
        _bandStoryboard = null;
        Particles.Stop();
        _vm.PropertyChanged -= OnViewModelChanged;
        _source?.RemoveHook(WndProc);
        Close();
    }
}
