using System.Text.Json.Serialization;

namespace EndfieldMonitor.Models;

public sealed class SlotConfig
{
    /// <summary>传感器稳定 id（LibreHardwareMonitor Identifier 字符串）；null 表示尚未选择。</summary>
    public string? SensorId { get; set; }
    /// <summary>选择时记录的传感器显示信息，用于传感器暂时不可用时仍显示原选择。</summary>
    public string? SensorLabel { get; set; }
    /// <summary>自定义名称；空表示使用默认名称。</summary>
    public string? CustomName { get; set; }
    public int Decimals { get; set; } = 1;

    public SlotConfig Clone() => (SlotConfig)MemberwiseClone();
}

public sealed class AppSettings
{
    public const int CurrentVersion = 1;
    public static readonly double[] ScaleSteps = { 0.6, 0.8, 1.0, 1.2, 1.4, 1.6 };

    public int Version { get; set; } = CurrentVersion;
    public SlotConfig TopSlot { get; set; } = new();
    public SlotConfig BottomSlot { get; set; } = new();
    /// <summary>首次运行是否已完成自动选择（之后不再悄悄切换）。</summary>
    public bool SlotsInitialized { get; set; }

    /// <summary>
    /// 窗口左上角（物理像素，虚拟屏幕坐标，可为负）。以物理像素保存可避免混合 DPI 下 DIP 坐标的歧义。
    /// null 表示使用默认位置。
    /// </summary>
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public double Scale { get; set; } = 1.0;
    public bool Topmost { get; set; }
    public bool Locked { get; set; }
    public bool ParticlesEnabled { get; set; } = true;

    [JsonIgnore]
    public SlotConfig this[int slot] => slot == 0 ? TopSlot : BottomSlot;

    public AppSettings Clone()
    {
        var c = (AppSettings)MemberwiseClone();
        c.TopSlot = TopSlot.Clone();
        c.BottomSlot = BottomSlot.Clone();
        return c;
    }

    /// <summary>修正越界值；返回是否做过修正。</summary>
    public bool Normalize()
    {
        bool changed = false;
        TopSlot ??= new SlotConfig();
        BottomSlot ??= new SlotConfig();
        foreach (var s in new[] { TopSlot, BottomSlot })
        {
            if (s.Decimals is < 0 or > 2) { s.Decimals = Math.Clamp(s.Decimals, 0, 2); changed = true; }
            if (s.CustomName != null && string.IsNullOrWhiteSpace(s.CustomName)) { s.CustomName = null; changed = true; }
            if (s.CustomName is { Length: > 32 }) { s.CustomName = s.CustomName[..32]; changed = true; }
            if (s.SensorId is { Length: 0 }) { s.SensorId = null; changed = true; }
        }
        double snapped = SnapScale(Scale);
        if (snapped != Scale) { Scale = snapped; changed = true; }
        if (WindowX is int x && Math.Abs(x) > 200000) { WindowX = null; changed = true; }
        if (WindowY is int y && Math.Abs(y) > 200000) { WindowY = null; changed = true; }
        if (WindowX is null != WindowY is null) { WindowX = WindowY = null; changed = true; }
        return changed;
    }

    public static double SnapScale(double scale)
    {
        if (double.IsNaN(scale) || double.IsInfinity(scale)) return 1.0;
        double best = 1.0, bestD = double.MaxValue;
        foreach (var s in ScaleSteps)
        {
            double d = Math.Abs(s - scale);
            if (d < bestD) { bestD = d; best = s; }
        }
        return best;
    }
}
