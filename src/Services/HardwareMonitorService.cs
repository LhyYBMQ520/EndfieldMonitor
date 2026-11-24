using System.Diagnostics;
using EndfieldMonitor.Models;
using LibreHardwareMonitor.Hardware;

namespace EndfieldMonitor.Services;

/// <summary>
/// LibreHardwareMonitorLib 采集服务。
/// - 只 Open 一次，退出时 Close；
/// - 后台专用线程按固定周期递归更新 Hardware / SubHardware / Sensors，同一时刻仅一个周期；
/// - 每个硬件单独 try/catch，单设备失败不影响其它设备；
/// - 每个周期发布一个不可变快照，UI 只读取快照。
/// </summary>
public sealed class HardwareMonitorService : IDisposable
{
    private readonly TimeSpan _interval;
    private readonly object _gate = new();
    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private readonly AutoResetEvent _wake = new(false);
    private Computer? _computer;
    private volatile TelemetrySnapshot _snapshot = TelemetrySnapshot.Empty;
    private readonly Dictionary<string, SensorReading> _lastGood = new(StringComparer.Ordinal);
    private bool _disposed;

    public HardwareMonitorService(TimeSpan? interval = null)
    {
        _interval = interval ?? TimeSpan.FromMilliseconds(1000);
    }

    /// <summary>最新快照（线程安全读取）。</summary>
    public TelemetrySnapshot Snapshot => _snapshot;

    /// <summary>每个采样周期结束后在后台线程触发。</summary>
    public event Action<TelemetrySnapshot>? SnapshotUpdated;

    public string? OpenError { get; private set; }
    public TimeSpan? LastCycleDuration { get; private set; }
    public bool DriverAvailable { get; private set; }
    public bool DriverInstalled { get; private set; }
    public bool IsAdmin { get; private set; }

    public void Start()
    {
        lock (_gate)
        {
            if (_thread != null || _disposed) return;
            _cts = new CancellationTokenSource();
            _thread = new Thread(Run) { IsBackground = true, Name = "EndfieldMonitor.Hardware", Priority = ThreadPriority.BelowNormal };
            _thread.Start(_cts.Token);
        }
    }

    /// <summary>请求立即进行一次采样（例如窗口重新显示时）。</summary>
    public void RequestRefresh() => _wake.Set();

    private void Run(object? state)
    {
        var token = (CancellationToken)state!;
        try
        {
            bool admin = IsAdministrator();
            var computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsMotherboardEnabled = true,
                // 存储：普通权限下 LHM 0.9.6 在本机枚举不到任何存储设备，但其依赖 DiskInfoToolkit 仍会创建
                // 一个隐藏顶层窗口与消息循环线程；该窗口收到 WM_CLOSE（例如不带 /F 的 taskkill、关机时的广播）后
                // 消息循环会空转占满约半个 CPU 核心（已用独立程序复现）。因此仅在管理员模式下启用存储采集。
                IsStorageEnabled = admin,
                IsControllerEnabled = true,
                IsBatteryEnabled = true,
                IsPsuEnabled = true,
                IsPowerMonitorEnabled = true,
                // 网络适配器在本机有数十个虚拟过滤器节点，默认不采集以减少噪声和开销。
                IsNetworkEnabled = false,
            };
            computer.Open();
            _computer = computer;
            IsAdmin = admin;
            DriverInstalled = TryGetPawnIoInstalled();
            // 普通权限下无法打开驱动；管理员下是否真正可读取由数值本身体现（不再推测）。
            DriverAvailable = IsAdmin;
        }
        catch (Exception ex)
        {
            OpenError = ex.Message;
            Publish(new TelemetrySnapshot(DateTime.UtcNow, Array.Empty<SensorReading>(), CollectorState.Failed, new[] { "硬件监控初始化失败：" + ex.Message }));
            return;
        }

        var sw = new Stopwatch();
        while (!token.IsCancellationRequested)
        {
            sw.Restart();
            try { Sample(token); }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                Publish(new TelemetrySnapshot(DateTime.UtcNow, _snapshot.Sensors.Select(MarkStale).ToArray(), CollectorState.Degraded, new[] { "采样失败：" + ex.Message }));
            }
            LastCycleDuration = sw.Elapsed;
            var wait = _interval - sw.Elapsed;
            if (wait < TimeSpan.FromMilliseconds(100)) wait = TimeSpan.FromMilliseconds(100);
            WaitHandle.WaitAny(new[] { token.WaitHandle, _wake }, wait);
        }
    }

    private static bool TryGetPawnIoInstalled()
    {
        // LibreHardwareMonitor 的 PawnIo.IsInstalled 在本机（驱动位于 DriverStore）返回 false，
        // 因此同时检查驱动服务注册表项（普通用户可读）。
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
            if (key != null) return true;
        }
        catch { }
        try { return LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled; }
        catch { return false; }
    }

    public static bool IsAdministrator()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>
    /// LibreHardwareMonitorLib 0.9.x 通过 PawnIO 驱动读取 CPU MSR / SMU（温度、封装功率、核心频率）。
    /// 未以管理员运行或驱动未安装时，这些传感器仍会出现在树中但值恒为 0 / null，并非真实读数。
    /// 只对这类“已知需要驱动”的传感器且值为 0 / null 时标记 RequiresAdmin，其它真实 0（如负载 0%）保持原样。
    /// </summary>
    public static bool NeedsPrivilegedAccess(string hardwareType, SensorKind kind, double? value, bool privileged)
    {
        if (privileged) return false;
        if (hardwareType != "Cpu") return false;
        if (kind is not (SensorKind.Temperature or SensorKind.Power or SensorKind.Clock or SensorKind.Factor)) return false;
        return value is null or 0;
    }

    private void Sample(CancellationToken token)
    {
        var computer = _computer;
        if (computer == null) return;
        var now = DateTime.UtcNow;
        var readings = new List<SensorReading>(_snapshot.Sensors.Count > 0 ? _snapshot.Sensors.Count : 256);
        var errors = new List<string>();
        IList<IHardware> roots;
        try { roots = computer.Hardware.ToArray(); }
        catch (Exception ex) { errors.Add(ex.Message); roots = Array.Empty<IHardware>(); }

        foreach (var hw in roots)
        {
            if (token.IsCancellationRequested) return;
            Visit(hw, now, readings, errors);
        }

        var state = errors.Count == 0 ? CollectorState.Running : CollectorState.Degraded;
        Publish(new TelemetrySnapshot(now, readings, state, errors));
    }

    private void Visit(IHardware hw, DateTime now, List<SensorReading> readings, List<string> errors)
    {
        bool ok = true;
        try { hw.Update(); }
        catch (Exception ex)
        {
            ok = false;
            errors.Add($"{hw.Name}: {ex.Message}");
        }

        string hwId = SafeId(hw.Identifier);
        string hwType = hw.HardwareType.ToString();
        ISensor[] sensors;
        try { sensors = hw.Sensors; } catch { sensors = Array.Empty<ISensor>(); }
        foreach (var s in sensors)
        {
            string id = SafeId(s.Identifier);
            if (!ok)
            {
                // 更新失败：沿用上次成功值与其时间戳，标记为错误，避免旧值看起来像新值。
                if (_lastGood.TryGetValue(id, out var prev))
                    readings.Add(prev with { Availability = SensorAvailability.Error });
                else
                    readings.Add(Build(s, id, hwId, hw.Name, hwType, null, now, SensorAvailability.Error));
                continue;
            }

            float? raw;
            try { raw = s.Value; } catch { raw = null; }
            SensorReading r;
            var kind = MapKind(s.SensorType);
            if (NeedsPrivilegedAccess(hwType, kind, raw, DriverAvailable))
                r = Build(s, id, hwId, hw.Name, hwType, null, now, SensorAvailability.RequiresAdmin);
            else if (raw is null)
                r = Build(s, id, hwId, hw.Name, hwType, null, now, SensorAvailability.NoValue);
            else if (float.IsNaN(raw.Value) || float.IsInfinity(raw.Value))
                r = Build(s, id, hwId, hw.Name, hwType, null, now, SensorAvailability.Invalid);
            else
            {
                r = Build(s, id, hwId, hw.Name, hwType, raw.Value, now, SensorAvailability.Ok);
                _lastGood[id] = r;
            }
            readings.Add(r);
        }

        IHardware[] subs;
        try { subs = hw.SubHardware; } catch { subs = Array.Empty<IHardware>(); }
        foreach (var sub in subs) Visit(sub, now, readings, errors);
    }

    private static SensorReading Build(ISensor s, string id, string hwId, string hwName, string hwType, double? value, DateTime now, SensorAvailability a)
    {
        var kind = MapKind(s.SensorType);
        return new SensorReading(id, hwId, hwName, hwType, s.Name, kind, value, SensorFormatting.BaseUnit(kind), now, a);
    }

    private static SensorReading MarkStale(SensorReading r) =>
        r.Availability == SensorAvailability.Ok ? r with { Availability = SensorAvailability.Error } : r;

    private static string SafeId(Identifier identifier)
    {
        try { return identifier.ToString(); } catch { return string.Empty; }
    }

    public static SensorKind MapKind(SensorType t) =>
        Enum.TryParse<SensorKind>(t.ToString(), out var k) ? k : SensorKind.Unknown;

    private void Publish(TelemetrySnapshot snapshot)
    {
        _snapshot = snapshot;
        try { SnapshotUpdated?.Invoke(snapshot); } catch { /* 订阅方异常不影响采集 */ }
    }

    public void Dispose()
    {
        Thread? t;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            t = _thread;
            _cts?.Cancel();
        }
        // Computer 不是线程安全的：等待采样线程结束后再 Close。
        if (t != null && !t.Join(TimeSpan.FromSeconds(5)))
            Debug.WriteLine("Hardware thread did not exit in time.");
        try { _computer?.Close(); } catch { }
        _computer = null;
        _cts?.Dispose();
        _wake.Dispose();
        SnapshotUpdated = null;
    }
}
