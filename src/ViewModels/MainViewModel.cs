using System.Windows.Threading;
using EndfieldMonitor.Models;
using EndfieldMonitor.Services;

namespace EndfieldMonitor.ViewModels;

/// <summary>
/// 主界面状态。挂件可见 / 置顶 / 锁定的唯一来源是 <see cref="WidgetState"/>；
/// 其余显示设置与槽位配置的唯一来源是 <see cref="Settings"/>。
/// 采集线程只发布快照，本类在 UI 线程上以 1 秒节奏读取最新快照。
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly HardwareMonitorService _hardware;
    private readonly WindowsBatteryProvider _battery;
    private readonly SettingsService _settingsService;
    private readonly DispatcherTimer _uiTimer;
    private TelemetrySnapshot _lastApplied = TelemetrySnapshot.Empty;
    private bool _paused;

    public MainViewModel(HardwareMonitorService hardware, WindowsBatteryProvider battery, SettingsService settingsService, AppSettings settings, WidgetState state)
    {
        _hardware = hardware;
        _battery = battery;
        _settingsService = settingsService;
        Settings = settings;
        State = state;
        TopSlot = new SlotViewModel(0);
        BottomSlot = new SlotViewModel(1);
        Catalog = new SensorCatalog();
        _uiTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _uiTimer.Tick += (_, _) => Refresh();
        state.Changed += () => SaveSettings();
    }

    public AppSettings Settings { get; }
    public WidgetState State { get; }
    public SlotViewModel TopSlot { get; }
    public SlotViewModel BottomSlot { get; }
    public SensorCatalog Catalog { get; }
    public string ConfigPath => _settingsService.FilePath;
    public bool IsAdmin => _hardware.IsAdmin || HardwareMonitorService.IsAdministrator();

    public event Action? RestartAsAdminRequested;
    /// <summary>缩放变化（窗口需重新计算尺寸并校验位置）。</summary>
    public event Action? ScaleChanged;

    // ── 电池 ───────────────────────────────────────────────────
    private string _chargeText = "--";
    private double? _chargeFraction;
    private string _timeText = "";
    private bool _timeVisible;
    private string _batteryStatusText = "";

    public string ChargeText { get => _chargeText; private set => Set(ref _chargeText, value); }
    /// <summary>电量弧比例；null 表示中性状态（无电池 / 未知），只显示灰色底轨。</summary>
    public double? ChargeFraction { get => _chargeFraction; private set => Set(ref _chargeFraction, value); }
    public string TimeText { get => _timeText; private set => Set(ref _timeText, value); }
    public bool TimeVisible { get => _timeVisible; private set => Set(ref _timeVisible, value); }
    public string BatteryStatusText { get => _batteryStatusText; private set => Set(ref _batteryStatusText, value); }

    public BatteryStatus LastBattery { get; private set; } = BatteryStatus.Failed;

    // ── 显示设置 ────────────────────────────────────────────────
    public double Scale => Settings.Scale;
    public bool ParticlesEnabled => Settings.ParticlesEnabled;

    // ── 运行状态 ────────────────────────────────────────────────
    private string _collectorText = "正在初始化…";
    private string _lastUpdateText = "--";
    private string _privilegeText = "";
    private string _errorText = "";
    private string _performanceText = "";

    public string CollectorText { get => _collectorText; private set => Set(ref _collectorText, value); }
    public string LastUpdateText { get => _lastUpdateText; private set => Set(ref _lastUpdateText, value); }
    public string PrivilegeText { get => _privilegeText; private set => Set(ref _privilegeText, value); }
    public string ErrorText { get => _errorText; private set => Set(ref _errorText, value); }
    public string PerformanceText { get => _performanceText; set => Set(ref _performanceText, value); }

    /// <summary>设置面板打开时由窗口设置，用于同步刷新面板中的数值。</summary>
    public SettingsViewModel? ActiveSettings { get; set; }

    public void Start()
    {
        _hardware.Start();
        Refresh();
        _uiTimer.Start();
    }

    /// <summary>隐藏时降低 UI 刷新（采集继续低频运行，保证恢复后数据及时）。</summary>
    public void SetPaused(bool paused)
    {
        if (_paused == paused) return;
        _paused = paused;
        if (paused) _uiTimer.Stop();
        else
        {
            _hardware.RequestRefresh();
            Refresh();
            _uiTimer.Start();
        }
    }

    /// <summary>电源状态变化（WM_POWERBROADCAST）时立即刷新电池。</summary>
    public void RefreshBattery() => ApplyBattery(_battery.Read());

    public void Refresh()
    {
        ApplyBattery(_battery.Read());
        var snap = _hardware.Snapshot;
        var now = DateTime.UtcNow;
        bool ready = snap.State is CollectorState.Running or CollectorState.Degraded;

        if (!ReferenceEquals(snap, _lastApplied))
        {
            _lastApplied = snap;
            Catalog.Update(snap);
            if (!Settings.SlotsInitialized && ready && snap.Sensors.Count > 0)
                InitializeDefaultSlots(snap);
        }

        TopSlot.Update(Settings.TopSlot, snap, now, ready);
        BottomSlot.Update(Settings.BottomSlot, snap, now, ready);
        UpdateStatus(snap, now);
        ActiveSettings?.RefreshValues();
    }

    private void ApplyBattery(BatteryStatus b)
    {
        LastBattery = b;
        if (b.Presence == BatteryPresence.Present && b.Percent is int p)
        {
            ChargeText = p.ToString();
            ChargeFraction = p / 100.0;
        }
        else
        {
            ChargeText = "--";
            ChargeFraction = null;
        }
        BatteryStatusText = b.StatusText;
        if (b.RemainingDischarge is TimeSpan t)
        {
            TimeText = SensorFormatting.FormatDuration(t.TotalSeconds);
            TimeVisible = true;
        }
        else
        {
            TimeText = "--:--:--";
            TimeVisible = b.Presence == BatteryPresence.Present && b.State == ChargeState.Discharging;
        }
    }

    private void UpdateStatus(TelemetrySnapshot snap, DateTime now)
    {
        int count = snap.Sensors.Count;
        CollectorText = snap.State switch
        {
            CollectorState.Starting => "正在初始化硬件采集…",
            CollectorState.Running => $"运行中 · {count} 个传感器 · 周期 {(_hardware.LastCycleDuration?.TotalMilliseconds ?? 0):0} ms",
            CollectorState.Degraded => $"部分设备异常 · {count} 个传感器",
            CollectorState.Failed => "采集失败",
            _ => "已停止",
        };
        if (snap.SampledAtUtc > DateTime.MinValue)
        {
            double age = (now - snap.SampledAtUtc).TotalSeconds;
            LastUpdateText = snap.SampledAtUtc.ToLocalTime().ToString("HH:mm:ss") + (age > 5 ? $"（{age:0} 秒前，已过期）" : "");
        }
        PrivilegeText = IsAdmin
            ? (_hardware.DriverInstalled ? "管理员 · 已检测到 PawnIO 驱动服务" : "管理员 · 未检测到 PawnIO 驱动，部分 CPU 传感器可能仍不可用")
            : "普通权限 · CPU 温度 / 功率 / 频率等需管理员权限" + (_hardware.DriverInstalled ? "（已检测到 PawnIO 驱动服务）" : "");
        var errs = snap.Errors;
        string err = errs.Count == 0 ? "" : string.Join("\n", errs.Take(3)) + (errs.Count > 3 ? $"\n…另有 {errs.Count - 3} 条" : "");
        if (_hardware.OpenError != null) err = "初始化：" + _hardware.OpenError;
        if (_settingsService.LoadDiagnostic != null) err = (err.Length > 0 ? err + "\n" : "") + _settingsService.LoadDiagnostic;
        ErrorText = err;
    }

    private void InitializeDefaultSlots(TelemetrySnapshot snap)
    {
        var top = SensorSelection.PickTopDefault(snap.Sensors);
        var bottom = SensorSelection.PickBottomDefault(snap.Sensors);
        if (Settings.TopSlot.SensorId == null && top != null)
        {
            Settings.TopSlot.SensorId = top.Id;
            Settings.TopSlot.SensorLabel = SensorSelection.Label(top);
        }
        if (Settings.BottomSlot.SensorId == null && bottom != null)
        {
            Settings.BottomSlot.SensorId = bottom.Id;
            Settings.BottomSlot.SensorLabel = SensorSelection.Label(bottom);
        }
        Settings.SlotsInitialized = true;
        SaveSettings();
    }

    // ── 槽位设置（设置面板调用） ─────────────────────────────────
    public void SetSlotSensor(int slot, SensorReading reading)
    {
        var cfg = Settings[slot];
        if (cfg.SensorId == reading.Id) return;
        cfg.SensorId = reading.Id;
        cfg.SensorLabel = SensorSelection.Label(reading);
        Settings.SlotsInitialized = true;
        SaveSettings();
        Refresh();
    }

    public void SetSlotName(int slot, string? name)
    {
        var cfg = Settings[slot];
        string? n = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        if (n is { Length: > 32 }) n = n[..32];
        if (cfg.CustomName == n) return;
        cfg.CustomName = n;
        SaveSettings();
        RefreshSlotsOnly();
    }

    public void SetSlotDecimals(int slot, int decimals)
    {
        var cfg = Settings[slot];
        decimals = Math.Clamp(decimals, 0, 2);
        if (cfg.Decimals == decimals) return;
        cfg.Decimals = decimals;
        SaveSettings();
        RefreshSlotsOnly();
    }

    private void RefreshSlotsOnly()
    {
        var snap = _hardware.Snapshot;
        bool ready = snap.State is CollectorState.Running or CollectorState.Degraded;
        TopSlot.Update(Settings.TopSlot, snap, DateTime.UtcNow, ready);
        BottomSlot.Update(Settings.BottomSlot, snap, DateTime.UtcNow, ready);
    }

    public void ResetSlotsToDefault()
    {
        Settings.TopSlot = new SlotConfig();
        Settings.BottomSlot = new SlotConfig();
        Settings.SlotsInitialized = false;
        var snap = _hardware.Snapshot;
        if (snap.Sensors.Count > 0) InitializeDefaultSlots(snap);
        SaveSettings();
        Refresh();
    }

    // ── 显示设置 ────────────────────────────────────────────────
    public void SetScale(double scale)
    {
        scale = AppSettings.SnapScale(scale);
        if (Math.Abs(Settings.Scale - scale) < 0.0001) return;
        Settings.Scale = scale;
        Raise(nameof(Scale));
        ScaleChanged?.Invoke();
        SaveSettings();
    }

    public void SetParticles(bool enabled)
    {
        if (Settings.ParticlesEnabled == enabled) return;
        Settings.ParticlesEnabled = enabled;
        Raise(nameof(ParticlesEnabled));
        SaveSettings();
    }

    public void SetWindowPosition(int x, int y)
    {
        if (Settings.WindowX == x && Settings.WindowY == y) return;
        Settings.WindowX = x;
        Settings.WindowY = y;
        SaveSettings();
    }

    public void ClearWindowPosition()
    {
        Settings.WindowX = Settings.WindowY = null;
        SaveSettings();
    }

    public void RequestRestartAsAdmin() => RestartAsAdminRequested?.Invoke();

    public void SaveSettings()
    {
        Settings.Topmost = State.Topmost;
        // 托盘尚未建立时暂缓的锁定也需要保留。
        Settings.Locked = State.Locked || State.PendingLock;
        _settingsService.SaveDebounced(Settings);
    }

    public void Dispose()
    {
        _uiTimer.Stop();
        SaveSettings();
    }
}
