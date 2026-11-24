using EndfieldMonitor.Models;
using EndfieldMonitor.Services;

namespace EndfieldMonitor.ViewModels;

/// <summary>单个监控槽位的展示状态。只在内容真正变化时触发属性通知，避免无谓重绘。</summary>
public sealed class SlotViewModel : ObservableObject
{
    /// <summary>超过该时长没有成功采样，视为数据过期。</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(5);

    public SlotViewModel(int index) { Index = index; }

    public int Index { get; }

    private string _title = "";
    private string _value = "";
    private string _unit = "";
    private string _status = "";
    private bool _isStale;
    private string _tooltip = "";

    public string Title { get => _title; private set => Set(ref _title, value); }
    /// <summary>数值文本；空串表示没有可显示的数值。</summary>
    public string Value { get => _value; private set => Set(ref _value, value); }
    public string Unit { get => _unit; private set => Set(ref _unit, value); }
    /// <summary>无数值时的状态说明（显示在单位位置）。</summary>
    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool IsStale { get => _isStale; private set => Set(ref _isStale, value); }
    public string Tooltip { get => _tooltip; private set => Set(ref _tooltip, value); }

    /// <summary>根据配置与快照计算显示内容（纯逻辑，便于测试）。</summary>
    public void Update(SlotConfig config, TelemetrySnapshot snapshot, DateTime nowUtc, bool collectorReady)
    {
        SensorReading? reading = null;
        if (config.SensorId != null) snapshot.ById.TryGetValue(config.SensorId, out reading);

        string defaultName = reading != null
            ? SensorSelection.DefaultName(reading, Index)
            : config.SensorId == null ? "请选择监控项" : (config.SensorLabel != null ? ShortName(config.SensorLabel) : "监控项");
        Title = string.IsNullOrWhiteSpace(config.CustomName) ? defaultName : config.CustomName!.Trim();

        if (config.SensorId == null)
        {
            SetEmpty("未选择");
            Tooltip = "请通过“电能报告”右侧的 ··· 选择监控项";
            return;
        }

        if (reading == null)
        {
            // 已保存的传感器暂时不可用：保留选择，不悄悄切换。
            SetEmpty(collectorReady ? "传感器不可用" : "采集中…");
            Tooltip = $"{Title}\n{config.SensorLabel ?? config.SensorId}\n{(collectorReady ? "当前未检测到该传感器（标识可能已变化或设备已移除）" : "正在初始化硬件采集")}";
            return;
        }

        string label = SensorSelection.Label(reading);
        switch (reading.Availability)
        {
            case SensorAvailability.RequiresAdmin:
                SetEmpty("需管理员");
                Tooltip = $"{Title}\n{label}\n该传感器需要以管理员身份运行才能读取";
                return;
            case SensorAvailability.NoValue:
                SetEmpty("无数值");
                Tooltip = $"{Title}\n{label}\n传感器当前未报告数值";
                return;
            case SensorAvailability.Invalid:
                SetEmpty("数值无效");
                Tooltip = $"{Title}\n{label}\n传感器报告了无效数值";
                return;
        }

        if (reading.Value is not double v)
        {
            SetEmpty("无数值");
            Tooltip = $"{Title}\n{label}";
            return;
        }

        var f = SensorFormatting.Format(reading.Kind, v, config.Decimals);
        Value = f.Value;
        Unit = f.Unit;
        Status = "";
        bool stale = reading.Availability == SensorAvailability.Error || nowUtc - reading.ObservedAtUtc > StaleAfter;
        IsStale = stale;
        Tooltip = $"{Title}\n{label}\n{f.Value} {f.Unit}{(stale ? "\n数据已过期（最近一次成功采样：" + reading.ObservedAtUtc.ToLocalTime().ToString("HH:mm:ss") + "）" : "")}";
    }

    private void SetEmpty(string status)
    {
        Value = "";
        Unit = "";
        Status = status;
        IsStale = false;
    }

    private static string ShortName(string label)
    {
        int i = label.IndexOf('·');
        string s = i >= 0 ? label[(i + 1)..].Trim() : label;
        int p = s.IndexOf('(');
        return p > 0 ? s[..p].Trim() : s;
    }
}
