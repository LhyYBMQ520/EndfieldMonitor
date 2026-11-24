using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using EndfieldMonitor.Models;
using EndfieldMonitor.Services;
using EndfieldMonitor.ViewModels;
using EndfieldMonitor.Views;

namespace EndfieldMonitor;

/// <summary>
/// 应用生命周期：单实例 → 托盘 → 窗口位置恢复 → 硬件采集 → 显示界面 → 托盘可用后恢复锁定。
/// 退出统一经过 <see cref="ExitApplication"/>。
/// </summary>
public partial class App : Application
{
    private SingleInstanceService? _instance;
    private SettingsService? _settingsService;
    private HardwareMonitorService? _hardware;
    private TrayService? _tray;
    private MainViewModel? _vm;
    private MainWindow? _window;
    private readonly WidgetState _state = new();
    private DispatcherTimer? _perfTimer;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        // 诊断开关：ENDFIELD_RENDER=sw 强制软件渲染，用于对比 GPU 合成开销。
        if (Environment.GetEnvironmentVariable("ENDFIELD_RENDER") == "sw")
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        var commands = SingleInstanceService.ParseArgs(e.Args);
        _instance = new SingleInstanceService();
        if (!_instance.TryAcquire(SingleInstanceService.ParseWaitPid(e.Args)))
        {
            // 已有实例：把救援 / 显示命令转交给原实例后退出。
            _instance.SendToPrimary(commands);
            _instance.Dispose();
            _instance = null;
            Shutdown(0);
            return;
        }

        _settingsService = new SettingsService();
        var settings = _settingsService.Load();
        bool resetWindow = commands.Contains(InstanceCommand.ResetWindow);
        bool unlock = commands.Contains(InstanceCommand.Unlock);
        if (resetWindow) { settings.WindowX = settings.WindowY = null; }
        if (unlock) settings.Locked = false;
        // 先取出持久化的窗口状态：托盘建立事件会触发保存，不能让默认状态覆盖它们。
        bool restoreLocked = settings.Locked;
        bool restoreTopmost = settings.Topmost;

        _hardware = new HardwareMonitorService();
        _vm = new MainViewModel(_hardware, new WindowsBatteryProvider(), _settingsService, settings, _state);
        _vm.RestartAsAdminRequested += RestartAsAdmin;

        // 1) 托盘（先恢复为未锁定状态；托盘可用后才真正应用锁定）
        _state.Restore(visible: true, topmost: restoreTopmost, locked: restoreLocked);
        _tray = new TrayService(() => new TrayMenuState(_state.Visible, _state.Topmost, _state.Locked, _vm.Scale));
        _tray.Command += OnTrayCommand;
        _tray.Established += () => _state.SetTrayReady(true);
        _tray.CloseRequested += () => Dispatcher.BeginInvoke(ExitApplication);
        bool trayOk = _tray.Initialize();
        _tray.SetTooltip("EndfieldMonitor · 协议核心");

        // 2) 窗口（位置在 SourceInitialized 中按显示器布局恢复）
        _window = new MainWindow(_vm, _state);
        _window.ExitRequested += ExitApplication;
        _state.Changed += OnStateChanged;

        // 3) 托盘建立成功时 PendingLock 转为 Locked；失败时保持未锁定，避免进入无法操作的状态。
        _state.SetTrayReady(trayOk || _tray.IsEstablished);
        Trace($"startup: restoreLocked={restoreLocked} trayOk={trayOk} state.Locked={_state.Locked} pending={_state.PendingLock}");

        // 4) 采集与显示
        _vm.Start();
        ShowMainWindow();

        _instance.CommandReceived += OnInstanceCommand;
        _instance.CloseRequested += () => Dispatcher.BeginInvoke(ExitApplication);
        _instance.StartListening();

        _perfTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _perfTimer.Tick += (_, _) => { _window?.UpdatePerformanceText(); UpdateTrayTooltip(); };
        _perfTimer.Start();

        if (commands.Contains(InstanceCommand.OpenSettings)) OpenSettingsFromTray();

        // 开发诊断：--snapshot <png> 在数据就绪后把仪表渲染为图片并退出（不参与实例通信）。
        int snapIndex = Array.IndexOf(e.Args, "--snapshot");
        if (snapIndex >= 0 && snapIndex + 1 < e.Args.Length)
        {
            string target = e.Args[snapIndex + 1];
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                try
                {
                    _window.SaveSnapshot(target, Color.FromRgb(0xE7, 0xE7, 0xE7));
                    _window.SaveSnapshot(Path.ChangeExtension(target, ".alpha.png"), null);
                }
                catch (Exception ex) { File.WriteAllText(target + ".err.txt", ex.ToString()); }
                ExitApplication();
            };
            t.Start();
        }
    }

    private void OnStateChanged()
    {
        _window?.ApplyState();
    }

    private void ShowMainWindow(bool requestActivation = false)
    {
        _state.SetVisible(true);
        _window?.ShowAndRaise(requestActivation);
    }

    private void UpdateTrayTooltip()
    {
        if (_tray == null || _vm == null) return;
        string battery = _vm.ChargeText == "--" ? _vm.BatteryStatusText : $"电量 {_vm.ChargeText}% · {_vm.BatteryStatusText}";
        string top = _vm.TopSlot.Value.Length > 0 ? $"{_vm.TopSlot.Title} {_vm.TopSlot.Value}{_vm.TopSlot.Unit}" : $"{_vm.TopSlot.Title} {_vm.TopSlot.Status}";
        string bottom = _vm.BottomSlot.Value.Length > 0 ? $"{_vm.BottomSlot.Title} {_vm.BottomSlot.Value}{_vm.BottomSlot.Unit}" : $"{_vm.BottomSlot.Title} {_vm.BottomSlot.Status}";
        string lockText = _state.Locked ? "\n已锁定（鼠标穿透）" : "";
        _tray.SetTooltip($"EndfieldMonitor\n{battery}\n{top}\n{bottom}{lockText}");
    }

    // ── 托盘命令（已在 UI 线程） ─────────────────────────────────
    private void OnTrayCommand(TrayCommand cmd)
    {
        if (_exiting || _vm == null || _window == null) return;
        switch (cmd)
        {
            case TrayCommand.ShowWindow:
                ShowMainWindow(requestActivation: true);
                break;
            case TrayCommand.ToggleVisible:
                if (_state.Visible) _state.SetVisible(false);
                else ShowMainWindow();
                break;
            case TrayCommand.ToggleTopmost:
                _state.SetTopmost(!_state.Topmost);
                break;
            case TrayCommand.ToggleLock:
                if (!_state.SetLocked(!_state.Locked))
                    MessageBox.Show("托盘图标尚未建立，暂时无法锁定。", "EndfieldMonitor", MessageBoxButton.OK, MessageBoxImage.Information);
                break;
            case TrayCommand.OpenSettings:
                OpenSettingsFromTray();
                break;
            case TrayCommand.ResetPosition:
                _state.SetVisible(true);
                _window.ResetPosition();
                break;
            case TrayCommand.Exit:
                ExitApplication();
                break;
            case >= TrayCommand.Scale60 and <= TrayCommand.Scale160:
                _vm.SetScale(AppSettings.ScaleSteps[cmd - TrayCommand.Scale60]);
                break;
        }
        UpdateTrayTooltip();
    }

    /// <summary>“打开监控设置”：显示挂件、解除锁定、打开设置面板。</summary>
    private void OpenSettingsFromTray()
    {
        _state.PrepareForSettings();
        ShowMainWindow();
        // 等待窗口完成显示与样式切换后再打开 Popup。
        Dispatcher.BeginInvoke(() => _window?.OpenSettings(), DispatcherPriority.Input);
    }

    private void OnInstanceCommand(InstanceCommand cmd)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_exiting || _window == null) return;
            switch (cmd)
            {
                case InstanceCommand.Show:
                    ShowMainWindow();
                    break;
                case InstanceCommand.Unlock:
                    _state.RescueUnlock();
                    break;
                case InstanceCommand.ResetWindow:
                    _state.SetVisible(true);
                    _window.ResetPosition();
                    break;
                case InstanceCommand.OpenSettings:
                    OpenSettingsFromTray();
                    break;
                case InstanceCommand.Hide:
                    _state.SetVisible(false);
                    break;
                case InstanceCommand.Exit:
                    ExitApplication();
                    break;
            }
        });
    }

    // ── 管理员重启（用户主动选择） ───────────────────────────────
    private void RestartAsAdmin()
    {
        string? exe = Environment.ProcessPath;
        if (exe == null) return;
        try
        {
            var psi = new ProcessStartInfo(exe, $"--wait-pid {Environment.ProcessId}")
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
            };
            Process.Start(psi);
            ExitApplication();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == Interop.NativeMethods.ERROR_CANCELLED)
        {
            // 用户在 UAC 中取消：保持当前实例。
        }
        catch (Exception ex)
        {
            MessageBox.Show("无法以管理员身份启动：" + ex.Message, "EndfieldMonitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ── 统一退出 ─────────────────────────────────────────────────
    public void ExitApplication()
    {
        Trace($"ExitApplication exiting={_exiting}");
        if (_exiting) return;
        _exiting = true;
        _perfTimer?.Stop();
        try { _window?.CloseTransientUi(); } catch { }
        try { _vm?.SaveSettings(); } catch { }
        try { _window?.Shutdown(); } catch { }   // 停止动画与渲染回调、移除窗口钩子
        try { _vm?.Dispose(); } catch { }
        try { _hardware?.Dispose(); } catch { }  // 取消采样线程、关闭 Computer
        try { _tray?.Dispose(); } catch { }      // 移除托盘图标
        try { _settingsService?.Shutdown(); } catch { } // 写入最终配置
        try { _instance?.Dispose(); } catch { }  // 释放单实例资源
        Shutdown(0);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!_exiting && _instance != null) ExitApplication();
        base.OnExit(e);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        ExitApplication();
        base.OnSessionEnding(e);
    }

    /// <summary>设置环境变量 ENDFIELD_TRACE=1 时写入诊断日志。</summary>
    internal static void Trace(string message)
    {
        if (Environment.GetEnvironmentVariable("ENDFIELD_TRACE") != "1") return;
        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "EndfieldMonitor.trace.log"), $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n"); }
        catch { }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            string dir = _settingsService?.Directory ?? AppContext.BaseDirectory;
            File.AppendAllText(Path.Combine(dir, "EndfieldMonitor.error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}\n\n");
        }
        catch { }
        // 渲染或数据层的单次异常不应让挂件崩溃；锁定状态下仍保留托盘入口。
        e.Handled = true;
    }
}
