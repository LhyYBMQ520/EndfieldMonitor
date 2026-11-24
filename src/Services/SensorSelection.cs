using EndfieldMonitor.Models;

namespace EndfieldMonitor.Services;

/// <summary>首次运行的默认传感器选择与默认名称。</summary>
public static class SensorSelection
{
    /// <summary>上方：优先 CPU Package 温度，其次 Tctl/Tdie、Core Max 等 CPU 温度。</summary>
    public static SensorReading? PickTopDefault(IReadOnlyList<SensorReading> sensors)
    {
        static bool IsCpu(SensorReading s) => s.HardwareType == "Cpu";
        SensorReading? best = null;
        int bestScore = int.MinValue;
        foreach (var s in sensors)
        {
            if (!IsCpu(s) || s.Kind != SensorKind.Temperature) continue;
            string n = s.Name.ToLowerInvariant();
            int score = n.Contains("package") ? 100
                : n.Contains("tctl") || n.Contains("tdie") ? 90
                : n.Contains("core max") ? 80
                : n.Contains("core average") ? 70
                : n.StartsWith("core") ? 50
                : n.Contains("ccd") ? 40 : 10;
            if (s.Availability == SensorAvailability.Ok && s.Value is > 0) score += 5;
            if (score > bestScore) { bestScore = score; best = s; }
        }
        return best;
    }

    /// <summary>下方：优先独立 GPU 的 “GPU Core” 负载（总体负载），其次核显。</summary>
    public static SensorReading? PickBottomDefault(IReadOnlyList<SensorReading> sensors)
    {
        SensorReading? best = null;
        int bestScore = int.MinValue;
        foreach (var s in sensors)
        {
            if (!s.HardwareType.StartsWith("Gpu", StringComparison.Ordinal) || s.Kind != SensorKind.Load) continue;
            string n = s.Name.ToLowerInvariant();
            int score = n == "gpu core" ? 100 : n.Contains("d3d 3d") ? 60 : n.Contains("core") ? 50 : 0;
            if (score == 0) continue;
            // 独立显卡优先：NVIDIA / AMD 独显名中通常不含 "(TM) ... M" 核显标记，按类型 + 名称粗略区分。
            if (s.HardwareType == "GpuNvidia") score += 20;
            else if (s.HardwareType == "GpuAmd" && !IsLikelyIntegrated(s.HardwareName)) score += 15;
            else if (s.HardwareType == "GpuIntel" && s.HardwareName.Contains("Arc", StringComparison.OrdinalIgnoreCase)) score += 10;
            if (score > bestScore) { bestScore = score; best = s; }
        }
        return best;
    }

    private static bool IsLikelyIntegrated(string name) =>
        name.Contains("Radeon(TM)", StringComparison.OrdinalIgnoreCase) && !name.Contains("RX", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Graphics", StringComparison.OrdinalIgnoreCase);

    /// <summary>根据传感器语义生成默认名称（4 个汉字左右，适配标签宽度）。</summary>
    public static string DefaultName(SensorReading? s, int slot)
    {
        if (s is null) return slot == 0 ? "上方监控" : "下方监控";
        string dev = DevicePrefix(s.HardwareType);
        string kind = s.Kind switch
        {
            SensorKind.Temperature => "温度",
            SensorKind.Load => s.Name.Contains("Memory", StringComparison.OrdinalIgnoreCase) && dev == "内存" ? "占用" : "负载",
            SensorKind.Power => "功率",
            SensorKind.Clock => "频率",
            SensorKind.Fan => "风扇",
            SensorKind.Voltage => "电压",
            SensorKind.Data or SensorKind.SmallData => "数据",
            SensorKind.Throughput => "速率",
            SensorKind.Level => "水平",
            _ => SensorFormatting.KindDisplayName(s.Kind),
        };
        return dev + kind;
    }

    public static string DevicePrefix(string hardwareType) => hardwareType switch
    {
        "Cpu" => "CPU",
        "GpuNvidia" or "GpuAmd" or "GpuIntel" => "GPU",
        "Memory" => "内存",
        "Storage" => "硬盘",
        "Motherboard" or "SuperIO" => "主板",
        "Battery" => "电池",
        "Network" => "网络",
        "EmbeddedController" => "EC",
        "Psu" => "电源",
        "Cooler" => "散热",
        _ => "",
    };

    public static string DeviceGroupName(string hardwareType) => hardwareType switch
    {
        "Cpu" => "处理器",
        "GpuNvidia" or "GpuAmd" or "GpuIntel" => "显卡",
        "Memory" => "内存",
        "Storage" => "存储",
        "Motherboard" or "SuperIO" or "EmbeddedController" => "主板",
        "Battery" => "电池",
        "Network" => "网络",
        "Psu" => "电源",
        "Cooler" => "散热",
        _ => hardwareType,
    };

    public static string Label(SensorReading s) => $"{s.HardwareName} · {s.Name} ({SensorFormatting.KindDisplayName(s.Kind)})";

    /// <summary>搜索：设备名、传感器名、类型（中英文）任一包含关键字即匹配；多个关键字需全部匹配。</summary>
    public static bool Matches(SensorReading s, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        foreach (var term in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            bool hit = s.HardwareName.Contains(term, StringComparison.OrdinalIgnoreCase)
                || s.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                || s.Kind.ToString().Contains(term, StringComparison.OrdinalIgnoreCase)
                || SensorFormatting.KindDisplayName(s.Kind).Contains(term, StringComparison.OrdinalIgnoreCase)
                || DeviceGroupName(s.HardwareType).Contains(term, StringComparison.OrdinalIgnoreCase)
                || s.HardwareType.Contains(term, StringComparison.OrdinalIgnoreCase);
            if (!hit) return false;
        }
        return true;
    }
}
