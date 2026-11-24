using System.Globalization;
using EndfieldMonitor.Models;

namespace EndfieldMonitor.Services;

/// <summary>
/// 传感器单位与格式化。基础单位按 LibreHardwareMonitorLib 0.9.6（提交 3d331e3）源码核对：
/// Data = GB，SmallData = MB，Throughput = B/s（库自身 UI 以 1024 为进制换算 KB/s、MB/s），
/// Energy = mWh，TimeSpan = 秒，Timing = ns。
/// </summary>
public static class SensorFormatting
{
    public static string BaseUnit(SensorKind kind) => kind switch
    {
        SensorKind.Voltage => "V",
        SensorKind.Current => "A",
        SensorKind.Power => "W",
        SensorKind.Clock => "MHz",
        SensorKind.Temperature => "°C",
        SensorKind.Load => "%",
        SensorKind.Frequency => "Hz",
        SensorKind.Fan => "RPM",
        SensorKind.Flow => "L/h",
        SensorKind.Control => "%",
        SensorKind.Level => "%",
        SensorKind.Factor => "",
        SensorKind.Data => "GB",
        SensorKind.SmallData => "MB",
        SensorKind.Throughput => "B/s",
        SensorKind.TimeSpan => "s",
        SensorKind.Timing => "ns",
        SensorKind.Energy => "mWh",
        SensorKind.Noise => "dBA",
        SensorKind.Conductivity => "µS/cm",
        SensorKind.Humidity => "%",
        _ => "",
    };

    public static string KindDisplayName(SensorKind kind) => kind switch
    {
        SensorKind.Voltage => "电压",
        SensorKind.Current => "电流",
        SensorKind.Power => "功率",
        SensorKind.Clock => "频率",
        SensorKind.Temperature => "温度",
        SensorKind.Load => "负载",
        SensorKind.Frequency => "频率(Hz)",
        SensorKind.Fan => "风扇",
        SensorKind.Flow => "流量",
        SensorKind.Control => "控制",
        SensorKind.Level => "水平",
        SensorKind.Factor => "系数",
        SensorKind.Data => "数据",
        SensorKind.SmallData => "数据",
        SensorKind.Throughput => "吞吐",
        SensorKind.TimeSpan => "时长",
        SensorKind.Timing => "时序",
        SensorKind.Energy => "能量",
        SensorKind.Noise => "噪声",
        SensorKind.Conductivity => "电导率",
        SensorKind.Humidity => "湿度",
        _ => "其它",
    };

    /// <summary>显示用数值与单位（自动缩放时两者一起变化）。</summary>
    public readonly record struct Formatted(string Value, string Unit);

    public static Formatted Format(SensorKind kind, double value, int decimals)
    {
        decimals = Math.Clamp(decimals, 0, 2);
        var (scaled, unit) = Scale(kind, value);
        if (kind == SensorKind.TimeSpan)
            return new Formatted(FormatDuration(value), "");
        return new Formatted(FormatNumber(scaled, decimals), unit);
    }

    public static (double Value, string Unit) Scale(SensorKind kind, double value)
    {
        switch (kind)
        {
            case SensorKind.Throughput:
            {
                double abs = Math.Abs(value);
                if (abs >= 1024d * 1024 * 1024) return (value / (1024d * 1024 * 1024), "GB/s");
                if (abs >= 1024d * 1024) return (value / (1024d * 1024), "MB/s");
                if (abs >= 1024d) return (value / 1024d, "KB/s");
                return (value, "B/s");
            }
            case SensorKind.SmallData:
                return Math.Abs(value) >= 1024 ? (value / 1024, "GB") : (value, "MB");
            case SensorKind.Data:
                return Math.Abs(value) >= 1024 ? (value / 1024, "TB") : (value, "GB");
            case SensorKind.Energy:
                return Math.Abs(value) >= 1000 ? (value / 1000, "Wh") : (value, "mWh");
            case SensorKind.Clock:
                return (value, "MHz");
            default:
                return (value, BaseUnit(kind));
        }
    }

    public static string FormatNumber(double value, int decimals)
    {
        string s = value.ToString("F" + decimals, CultureInfo.InvariantCulture);
        // 避免 "-0" / "-0.0"
        if (s.StartsWith('-') && double.Parse(s, CultureInfo.InvariantCulture) == 0) s = s[1..];
        return s;
    }

    public static string FormatDuration(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) return "--:--:--";
        var ts = TimeSpan.FromSeconds(Math.Round(seconds));
        return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
    }

    /// <summary>设置面板中的当前值文本（含单位）。</summary>
    public static string Describe(SensorReading? reading, int decimals = 1)
    {
        if (reading is null) return "不可用";
        return reading.Availability switch
        {
            SensorAvailability.NoValue => "无数值",
            SensorAvailability.Invalid => "无效",
            SensorAvailability.RequiresAdmin => "需管理员",
            _ when reading.Value is double v => Join(Format(reading.Kind, v, decimals)),
            _ => "无数值",
        };
    }

    private static string Join(Formatted f) => f.Unit.Length == 0 ? f.Value : $"{f.Value} {f.Unit}";
}
