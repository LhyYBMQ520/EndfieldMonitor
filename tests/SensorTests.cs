using EndfieldMonitor.Models;
using EndfieldMonitor.Services;
using EndfieldMonitor.ViewModels;

namespace EndfieldMonitor.Tests;

public class SensorFormattingTests
{
    [Theory]
    [InlineData(SensorKind.Temperature, "°C")]
    [InlineData(SensorKind.Load, "%")]
    [InlineData(SensorKind.Power, "W")]
    [InlineData(SensorKind.Fan, "RPM")]
    [InlineData(SensorKind.Clock, "MHz")]
    [InlineData(SensorKind.Data, "GB")]
    [InlineData(SensorKind.SmallData, "MB")]
    [InlineData(SensorKind.Throughput, "B/s")]
    [InlineData(SensorKind.Energy, "mWh")]
    public void BaseUnits_MatchLibraryDefinitions(SensorKind kind, string unit) =>
        Assert.Equal(unit, SensorFormatting.BaseUnit(kind));

    [Fact]
    public void Throughput_ScalesValueAndUnitTogether_Base1024()
    {
        Assert.Equal(new SensorFormatting.Formatted("512.0", "B/s"), SensorFormatting.Format(SensorKind.Throughput, 512, 1));
        Assert.Equal(new SensorFormatting.Formatted("1.5", "KB/s"), SensorFormatting.Format(SensorKind.Throughput, 1536, 1));
        Assert.Equal(new SensorFormatting.Formatted("31.39", "MB/s"), SensorFormatting.Format(SensorKind.Throughput, 32910340, 2));
    }

    [Fact]
    public void SmallData_PromotesToGB()
    {
        Assert.Equal(new SensorFormatting.Formatted("512.0", "MB"), SensorFormatting.Format(SensorKind.SmallData, 512, 1));
        Assert.Equal(new SensorFormatting.Formatted("1.9", "GB"), SensorFormatting.Format(SensorKind.SmallData, 1992.55, 1));
        Assert.Equal("MB", SensorFormatting.Format(SensorKind.SmallData, 1000, 1).Unit);
        Assert.Equal(new SensorFormatting.Formatted("7.96", "GB"), SensorFormatting.Format(SensorKind.SmallData, 8151, 2));
    }

    [Theory]
    [InlineData(54.281, 0, "54")]
    [InlineData(54.281, 1, "54.3")]
    [InlineData(54.281, 2, "54.28")]
    [InlineData(0, 1, "0.0")]
    [InlineData(-0.04, 1, "0.0")]
    public void Decimals_AreDisplayOnly(double value, int decimals, string expected) =>
        Assert.Equal(expected, SensorFormatting.Format(SensorKind.Temperature, value, decimals).Value);

    [Fact]
    public void Duration_FormatsHms_AndRejectsInvalid()
    {
        Assert.Equal("01:02:05", SensorFormatting.FormatDuration(3725));
        Assert.Equal("--:--:--", SensorFormatting.FormatDuration(double.NaN));
        Assert.Equal("--:--:--", SensorFormatting.FormatDuration(-1));
    }
}

public class SlotDisplayTests
{
    private static SensorReading Reading(string id, double? value, SensorAvailability a, DateTime at, SensorKind kind = SensorKind.Temperature, string hwType = "GpuNvidia") =>
        new(id, "/gpu-nvidia/0", "NVIDIA GeForce RTX 5070 Laptop GPU", hwType, "GPU Core", kind, value, SensorFormatting.BaseUnit(kind), at, a);

    private static TelemetrySnapshot Snap(params SensorReading[] r) => new(DateTime.UtcNow, r, CollectorState.Running, Array.Empty<string>());

    [Fact]
    public void RealZero_IsShownAsValue()
    {
        var now = DateTime.UtcNow;
        var vm = new SlotViewModel(1);
        vm.Update(new SlotConfig { SensorId = "a", Decimals = 1 }, Snap(Reading("a", 0, SensorAvailability.Ok, now, SensorKind.Load)), now, true);
        Assert.Equal("0.0", vm.Value);
        Assert.Equal("%", vm.Unit);
    }

    [Theory]
    [InlineData(SensorAvailability.NoValue, "无数值")]
    [InlineData(SensorAvailability.Invalid, "数值无效")]
    [InlineData(SensorAvailability.RequiresAdmin, "需管理员")]
    public void MissingValues_AreNeverShownAsZero(SensorAvailability a, string status)
    {
        var now = DateTime.UtcNow;
        var vm = new SlotViewModel(0);
        vm.Update(new SlotConfig { SensorId = "a" }, Snap(Reading("a", null, a, now)), now, true);
        Assert.Equal("", vm.Value);
        Assert.Equal(status, vm.Status);
    }

    [Fact]
    public void SavedSensorMissing_KeepsSelection_AndReportsUnavailable()
    {
        var cfg = new SlotConfig { SensorId = "/gpu-nvidia/1/temperature/0", SensorLabel = "GPU2 · GPU Core (温度)" };
        var vm = new SlotViewModel(0);
        vm.Update(cfg, Snap(Reading("/gpu-nvidia/0/temperature/0", 50, SensorAvailability.Ok, DateTime.UtcNow)), DateTime.UtcNow, true);
        Assert.Equal("传感器不可用", vm.Status);
        Assert.Equal("/gpu-nvidia/1/temperature/0", cfg.SensorId); // 未被悄悄切换
    }

    [Fact]
    public void OldObservation_IsMarkedStale_EvenIfValueUnchanged()
    {
        var now = DateTime.UtcNow;
        var vm = new SlotViewModel(0);
        vm.Update(new SlotConfig { SensorId = "a" }, Snap(Reading("a", 50, SensorAvailability.Ok, now.AddSeconds(-30))), now, true);
        Assert.True(vm.IsStale);
        vm.Update(new SlotConfig { SensorId = "a" }, Snap(Reading("a", 50, SensorAvailability.Ok, now)), now, true);
        Assert.False(vm.IsStale);
    }

    [Fact]
    public void FailedHardwareUpdate_IsStale()
    {
        var now = DateTime.UtcNow;
        var vm = new SlotViewModel(0);
        vm.Update(new SlotConfig { SensorId = "a" }, Snap(Reading("a", 50, SensorAvailability.Error, now)), now, true);
        Assert.True(vm.IsStale);
        Assert.Equal("50.0", vm.Value);
    }

    [Fact]
    public void CustomName_FallsBackToDefaultWhenCleared()
    {
        var now = DateTime.UtcNow;
        var snap = Snap(Reading("a", 50, SensorAvailability.Ok, now));
        var vm = new SlotViewModel(0);
        vm.Update(new SlotConfig { SensorId = "a", CustomName = "显卡" }, snap, now, true);
        Assert.Equal("显卡", vm.Title);
        vm.Update(new SlotConfig { SensorId = "a", CustomName = "  " }, snap, now, true);
        Assert.Equal("GPU温度", vm.Title);
    }

    [Fact]
    public void NoSelection_PromptsUser()
    {
        var vm = new SlotViewModel(0);
        vm.Update(new SlotConfig(), Snap(), DateTime.UtcNow, true);
        Assert.Equal("请选择监控项", vm.Title);
        Assert.Equal("", vm.Value);
    }

    [Fact]
    public void PrivilegedSensorZero_IsMarkedRequiresAdmin_ButLoadZeroIsReal()
    {
        Assert.True(HardwareMonitorService.NeedsPrivilegedAccess("Cpu", SensorKind.Temperature, 0, privileged: false));
        Assert.True(HardwareMonitorService.NeedsPrivilegedAccess("Cpu", SensorKind.Power, null, privileged: false));
        Assert.False(HardwareMonitorService.NeedsPrivilegedAccess("Cpu", SensorKind.Temperature, 0, privileged: true));
        Assert.False(HardwareMonitorService.NeedsPrivilegedAccess("Cpu", SensorKind.Load, 0, privileged: false));
        Assert.False(HardwareMonitorService.NeedsPrivilegedAccess("GpuNvidia", SensorKind.Temperature, 0, privileged: false));
        Assert.False(HardwareMonitorService.NeedsPrivilegedAccess("Cpu", SensorKind.Temperature, 45.5, privileged: false));
    }
}

public class SensorSelectionTests
{
    private static SensorReading R(string id, string hwType, string hwName, string name, SensorKind kind, double? v = 1) =>
        new(id, id[..id.LastIndexOf('/')], hwName, hwType, name, kind, v, "", DateTime.UtcNow, SensorAvailability.Ok);

    [Fact]
    public void TopDefault_PrefersCpuPackageTemperature()
    {
        var list = new[]
        {
            R("/intelcpu/0/temperature/1", "Cpu", "Intel", "Core Max", SensorKind.Temperature),
            R("/intelcpu/0/temperature/0", "Cpu", "Intel", "CPU Package", SensorKind.Temperature),
            R("/intelcpu/0/load/0", "Cpu", "Intel", "CPU Total", SensorKind.Load),
        };
        Assert.Equal("/intelcpu/0/temperature/0", SensorSelection.PickTopDefault(list)!.Id);
    }

    [Fact]
    public void TopDefault_FallsBackToTctlOnAmd()
    {
        var list = new[] { R("/amdcpu/0/temperature/2", "Cpu", "AMD Ryzen 9 9955HX", "Core (Tctl/Tdie)", SensorKind.Temperature) };
        Assert.Equal("/amdcpu/0/temperature/2", SensorSelection.PickTopDefault(list)!.Id);
    }

    [Fact]
    public void BottomDefault_PrefersDiscreteGpuCoreLoad()
    {
        var list = new[]
        {
            R("/gpu-amd/0/load/0", "GpuAmd", "AMD Radeon(TM) 610M", "GPU Core", SensorKind.Load),
            R("/gpu-nvidia/0/load/5", "GpuNvidia", "NVIDIA GeForce RTX 5070 Laptop GPU", "D3D 3D", SensorKind.Load),
            R("/gpu-nvidia/0/load/0", "GpuNvidia", "NVIDIA GeForce RTX 5070 Laptop GPU", "GPU Core", SensorKind.Load),
        };
        Assert.Equal("/gpu-nvidia/0/load/0", SensorSelection.PickBottomDefault(list)!.Id);
    }

    [Fact]
    public void NoCandidates_ReturnsNull()
    {
        Assert.Null(SensorSelection.PickTopDefault(Array.Empty<SensorReading>()));
        Assert.Null(SensorSelection.PickBottomDefault(new[] { R("/ram/load/0", "Memory", "Total Memory", "Memory", SensorKind.Load) }));
    }

    [Fact]
    public void SameNamedSensors_AreDistinguishedById()
    {
        // 本机 NVIDIA GPU 上有多个同名 “D3D Copy” 传感器。
        var a = R("/gpu-nvidia/0/load/6", "GpuNvidia", "RTX", "D3D Copy", SensorKind.Load, 0);
        var b = R("/gpu-nvidia/0/load/7", "GpuNvidia", "RTX", "D3D Copy", SensorKind.Load, 0.135);
        var snap = new TelemetrySnapshot(DateTime.UtcNow, new[] { a, b }, CollectorState.Running, Array.Empty<string>());
        Assert.Equal(0.135, snap.ById["/gpu-nvidia/0/load/7"].Value);
        Assert.Equal(2, snap.ById.Count);
    }

    [Theory]
    [InlineData("gpu 温度", true)]
    [InlineData("RTX core", true)]
    [InlineData("显卡", true)]
    [InlineData("Temperature", true)]
    [InlineData("风扇", false)]
    public void Search_MatchesDeviceSensorAndType(string query, bool expected)
    {
        var r = R("/gpu-nvidia/0/temperature/0", "GpuNvidia", "NVIDIA GeForce RTX 5070", "GPU Core", SensorKind.Temperature);
        Assert.Equal(expected, SensorSelection.Matches(r, query));
    }
}
