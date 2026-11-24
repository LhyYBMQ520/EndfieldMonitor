using EndfieldMonitor.Interop;

namespace EndfieldMonitor.Services;

public enum BatteryPresence { Present, NoBattery, Unknown, ReadFailed }

public enum PowerSource { Ac, Battery, Unknown }

public enum ChargeState
{
    /// <summary>系统明确报告正在充电。</summary>
    Charging,
    /// <summary>接通电源、未充电、电量 100%。</summary>
    Full,
    /// <summary>接通电源、未充电、电量低于 100%（可能是智能充电保护，也可能是其它原因，无法确认）。</summary>
    PluggedNotCharging,
    /// <summary>电池供电。</summary>
    Discharging,
    Unknown,
}

/// <summary>系统电池状态（已解析，不含任何推算值）。</summary>
public sealed record BatteryStatus(
    BatteryPresence Presence,
    int? Percent,
    PowerSource Source,
    ChargeState State,
    TimeSpan? RemainingDischarge,
    bool BatterySaver)
{
    public static readonly BatteryStatus Failed = new(BatteryPresence.ReadFailed, null, PowerSource.Unknown, ChargeState.Unknown, null, false);

    public string StatusText => Presence switch
    {
        BatteryPresence.NoBattery => "未检测到电池",
        BatteryPresence.ReadFailed => "电池信息不可用",
        _ => State switch
        {
            ChargeState.Charging => "充电中",
            ChargeState.Full => "已充满",
            ChargeState.PluggedNotCharging => "已接电，未充电",
            ChargeState.Discharging => "电池供电",
            _ => Source == PowerSource.Ac ? "已接通电源" : "电源状态未知",
        },
    };
}

/// <summary>
/// 通过 GetSystemPowerStatus 读取系统汇总电池状态。系统已经把多块电池合并为一个百分比，
/// 这里不再自行相加或平均。
/// </summary>
public sealed class WindowsBatteryProvider
{
    public BatteryStatus Read()
    {
        try
        {
            if (!NativeMethods.GetSystemPowerStatus(out var s)) return BatteryStatus.Failed;
            return Interpret(s.ACLineStatus, s.BatteryFlag, s.BatteryLifePercent, s.SystemStatusFlag, s.BatteryLifeTime);
        }
        catch
        {
            return BatteryStatus.Failed;
        }
    }

    public const byte FlagHigh = 1, FlagLow = 2, FlagCritical = 4, FlagCharging = 8, FlagNoBattery = 128, FlagUnknown = 255;
    public const uint UnknownTime = 0xFFFFFFFF;

    /// <summary>纯函数：把原始 SYSTEM_POWER_STATUS 字段解析为状态。</summary>
    public static BatteryStatus Interpret(byte acLine, byte batteryFlag, byte percent, byte systemStatusFlag, uint lifeTimeSeconds)
    {
        var source = acLine switch { 0 => PowerSource.Battery, 1 => PowerSource.Ac, _ => PowerSource.Unknown };
        bool saver = systemStatusFlag == 1;
        int? pct = percent <= 100 ? percent : null; // 255 = 未知

        // 先处理未知值，再解析位标志。
        if (batteryFlag == FlagUnknown)
        {
            // 标志未知时仍可能有有效百分比。
            var unknownPresence = pct.HasValue ? BatteryPresence.Present : BatteryPresence.Unknown;
            return new BatteryStatus(unknownPresence, pct, source, StateWithoutFlags(source, pct), null, saver);
        }

        if ((batteryFlag & FlagNoBattery) != 0)
            return new BatteryStatus(BatteryPresence.NoBattery, null, source, ChargeState.Unknown, null, saver);

        bool charging = (batteryFlag & FlagCharging) != 0;
        ChargeState state;
        if (charging) state = ChargeState.Charging;
        else if (source == PowerSource.Battery) state = ChargeState.Discharging;
        else if (source == PowerSource.Ac)
            state = pct == 100 ? ChargeState.Full : pct.HasValue ? ChargeState.PluggedNotCharging : ChargeState.Unknown;
        else state = ChargeState.Unknown;

        // 只有电池供电且系统给出确定值时才显示剩余放电时间；BatteryFullLifeTime 不是“充满所需时间”，不使用。
        TimeSpan? remaining = state == ChargeState.Discharging && lifeTimeSeconds != UnknownTime
            ? TimeSpan.FromSeconds(lifeTimeSeconds)
            : null;

        var presence = pct.HasValue ? BatteryPresence.Present : BatteryPresence.Unknown;
        return new BatteryStatus(presence, pct, source, state, remaining, saver);
    }

    private static ChargeState StateWithoutFlags(PowerSource source, int? pct) => source switch
    {
        PowerSource.Battery => ChargeState.Discharging,
        _ => ChargeState.Unknown,
    };
}
