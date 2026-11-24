using EndfieldMonitor.Services;

namespace EndfieldMonitor.Tests;

public class BatteryStatusTests
{
    private const byte AcOffline = 0, AcOnline = 1, AcUnknown = 255;

    [Fact]
    public void UnknownPercent_IsNotShownAs255OrFull()
    {
        var s = WindowsBatteryProvider.Interpret(AcOnline, 1, 255, 0, WindowsBatteryProvider.UnknownTime);
        Assert.Null(s.Percent);
        Assert.Equal(BatteryPresence.Unknown, s.Presence);
    }

    [Fact]
    public void NoBatteryFlag_ReportsNoBattery_WithoutPercent()
    {
        var s = WindowsBatteryProvider.Interpret(AcOnline, 128, 255, 0, WindowsBatteryProvider.UnknownTime);
        Assert.Equal(BatteryPresence.NoBattery, s.Presence);
        Assert.Null(s.Percent);
        Assert.Equal("未检测到电池", s.StatusText);
    }

    [Fact]
    public void UnknownFlag_IsHandledBeforeBitParsing()
    {
        // 255 的所有位都为 1，若先按位解析会被误判为“无电池 + 充电中”。
        var s = WindowsBatteryProvider.Interpret(AcOnline, 255, 60, 0, WindowsBatteryProvider.UnknownTime);
        Assert.Equal(BatteryPresence.Present, s.Presence);
        Assert.Equal(60, s.Percent);
        Assert.NotEqual(ChargeState.Charging, s.State);
    }

    [Fact]
    public void RealZeroPercent_IsDisplayed()
    {
        var s = WindowsBatteryProvider.Interpret(AcOffline, 4, 0, 0, 120);
        Assert.Equal(0, s.Percent);
        Assert.Equal(BatteryPresence.Present, s.Presence);
        Assert.Equal(ChargeState.Discharging, s.State);
    }

    [Fact]
    public void Charging_RequiresChargingFlag()
    {
        var s = WindowsBatteryProvider.Interpret(AcOnline, 8, 55, 0, WindowsBatteryProvider.UnknownTime);
        Assert.Equal(ChargeState.Charging, s.State);
        Assert.Equal("充电中", s.StatusText);
    }

    [Fact]
    public void AcOnline_BelowFull_NotCharging_IsNotTreatedAsCharging()
    {
        // 智能充电保护等场景：接电但未充电，电量 80%。
        var s = WindowsBatteryProvider.Interpret(AcOnline, 1, 80, 0, WindowsBatteryProvider.UnknownTime);
        Assert.Equal(ChargeState.PluggedNotCharging, s.State);
        Assert.Equal("已接电，未充电", s.StatusText);
        Assert.Equal(80, s.Percent); // 不按充电上限重新归一化
    }

    [Fact]
    public void AcOnline_Full_IsFull()
    {
        var s = WindowsBatteryProvider.Interpret(AcOnline, 1, 100, 0, WindowsBatteryProvider.UnknownTime);
        Assert.Equal(ChargeState.Full, s.State);
        Assert.Equal("已充满", s.StatusText);
    }

    [Fact]
    public void RemainingTime_OnlyWhenDischargingAndKnown()
    {
        var discharging = WindowsBatteryProvider.Interpret(AcOffline, 1, 70, 0, 3725);
        Assert.Equal(TimeSpan.FromSeconds(3725), discharging.RemainingDischarge);

        var unknownTime = WindowsBatteryProvider.Interpret(AcOffline, 1, 70, 0, WindowsBatteryProvider.UnknownTime);
        Assert.Null(unknownTime.RemainingDischarge);

        // 接电时系统给出的时间不作为“充满所需时间”。
        var charging = WindowsBatteryProvider.Interpret(AcOnline, 8, 70, 0, 3725);
        Assert.Null(charging.RemainingDischarge);
    }

    [Fact]
    public void UnknownAcLine_IsNotReportedAsBattery()
    {
        var s = WindowsBatteryProvider.Interpret(AcUnknown, 1, 70, 0, WindowsBatteryProvider.UnknownTime);
        Assert.Equal(PowerSource.Unknown, s.Source);
        Assert.Equal(ChargeState.Unknown, s.State);
    }

    [Fact]
    public void RealProvider_ReturnsValidRangeOrNull()
    {
        var s = new WindowsBatteryProvider().Read();
        if (s.Percent is int p) Assert.InRange(p, 0, 100);
    }
}
