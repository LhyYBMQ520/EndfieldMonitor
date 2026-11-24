namespace EndfieldMonitor.Models;

/// <summary>单个传感器读数的可用状态。</summary>
public enum SensorAvailability
{
    /// <summary>本周期成功采样且数值有效（包括真实的 0）。</summary>
    Ok,
    /// <summary>库返回 null：传感器存在但当前没有数值。</summary>
    NoValue,
    /// <summary>库返回 NaN / Infinity。</summary>
    Invalid,
    /// <summary>所属硬件本周期更新失败，数值为上一次成功采样的结果。</summary>
    Error,
    /// <summary>
    /// 普通权限下底层驱动不可用，库对该传感器返回的 0 并非真实读数（例如 CPU 温度 0 °C、封装功率 0 W）。
    /// </summary>
    RequiresAdmin,
}

/// <summary>LibreHardwareMonitor 传感器类型的本地镜像，避免 UI 直接依赖库类型。</summary>
public enum SensorKind
{
    Voltage, Current, Power, Clock, Temperature, Load, Frequency, Fan, Flow, Control, Level, Factor,
    Data, SmallData, Throughput, TimeSpan, Timing, Energy, Noise, Conductivity, Humidity, Unknown,
}

/// <summary>不可变传感器读数 DTO。UI 只读取它，不接触硬件对象。</summary>
public sealed record SensorReading(
    string Id,
    string HardwareId,
    string HardwareName,
    string HardwareType,
    string Name,
    SensorKind Kind,
    double? Value,
    string Unit,
    DateTime ObservedAtUtc,
    SensorAvailability Availability)
{
    public bool HasValue => Value.HasValue && Availability is SensorAvailability.Ok or SensorAvailability.Error;
}

public enum CollectorState
{
    Starting,
    Running,
    Degraded,
    Failed,
    Stopped,
}

/// <summary>一次采样周期的不可变快照。</summary>
public sealed class TelemetrySnapshot
{
    public static readonly TelemetrySnapshot Empty = new(DateTime.MinValue, Array.Empty<SensorReading>(), CollectorState.Starting, Array.Empty<string>());

    public TelemetrySnapshot(DateTime sampledAtUtc, IReadOnlyList<SensorReading> sensors, CollectorState state, IReadOnlyList<string> errors)
    {
        SampledAtUtc = sampledAtUtc;
        Sensors = sensors;
        State = state;
        Errors = errors;
        var map = new Dictionary<string, SensorReading>(sensors.Count, StringComparer.Ordinal);
        foreach (var s in sensors) map.TryAdd(s.Id, s);
        ById = map;
    }

    public DateTime SampledAtUtc { get; }
    public IReadOnlyList<SensorReading> Sensors { get; }
    public IReadOnlyDictionary<string, SensorReading> ById { get; }
    public CollectorState State { get; }
    public IReadOnlyList<string> Errors { get; }

    /// <summary>
    /// 结构版本：传感器 id 集合变化时改变，供设置面板判断是否需要增删列表项。
    /// </summary>
    public int StructureHash
    {
        get
        {
            var h = new HashCode();
            foreach (var s in Sensors) h.Add(s.Id);
            return h.ToHashCode();
        }
    }
}
