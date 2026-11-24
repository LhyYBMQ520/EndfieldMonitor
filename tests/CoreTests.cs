using EndfieldMonitor.Models;
using EndfieldMonitor.Rendering;
using EndfieldMonitor.Services;

namespace EndfieldMonitor.Tests;

public class SettingsTests
{
    [Fact]
    public void Parse_ClampsOutOfRangeValues()
    {
        var s = SettingsService.Parse("""
            { "Version": 1, "Scale": 3.7, "TopSlot": { "Decimals": 9, "CustomName": "   " },
              "WindowX": 100, "WindowY": null }
            """, out var diag);
        Assert.Equal(1.6, s.Scale);
        Assert.Equal(2, s.TopSlot.Decimals);
        Assert.Null(s.TopSlot.CustomName);
        Assert.Null(s.WindowX); // 坐标不完整 → 使用默认位置
        Assert.NotNull(diag);
    }

    [Fact]
    public void Parse_RejectsCorruptJson()
    {
        Assert.Throws<InvalidDataException>(() => SettingsService.Parse("{ not json", out _));
        Assert.Throws<InvalidDataException>(() => SettingsService.Parse("null", out _));
    }

    [Fact]
    public void Parse_NewerVersion_IsReadWithDiagnostic()
    {
        var s = SettingsService.Parse("""{ "Version": 99, "Topmost": true, "UnknownFutureField": 1 }""", out var diag);
        Assert.True(s.Topmost);
        Assert.Equal(AppSettings.CurrentVersion, s.Version);
        Assert.Contains("99", diag);
    }

    [Fact]
    public void LoadAndSave_IgnoresRemovedBackgroundDecorSetting()
    {
        string dir = Path.Combine(Path.GetTempPath(), "em-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var svc = new SettingsService(dir);
            File.WriteAllText(svc.FilePath, """{ "Version": 1, "BackgroundDecor": true, "ParticlesEnabled": false, "Scale": 1.4 }""");

            var settings = svc.Load();
            Assert.Null(svc.LoadDiagnostic);
            Assert.False(settings.ParticlesEnabled);
            Assert.Equal(1.4, settings.Scale);

            svc.SaveNow(settings);
            Assert.DoesNotContain("BackgroundDecor", File.ReadAllText(svc.FilePath));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void SaveAndLoad_RoundTrips_Atomically()
    {
        string dir = Path.Combine(Path.GetTempPath(), "em-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var svc = new SettingsService(dir);
            var s = new AppSettings { Scale = 1.4, Topmost = true, Locked = true, WindowX = -1500, WindowY = 200 };
            s.TopSlot.SensorId = "/amdcpu/0/temperature/2";
            s.BottomSlot.CustomName = "显卡";
            svc.SaveNow(s);
            svc.SaveNow(s); // 第二次走 File.Replace 路径
            Assert.False(File.Exists(svc.FilePath + ".tmp"));
            var loaded = new SettingsService(dir).Load();
            Assert.Equal(1.4, loaded.Scale);
            Assert.True(loaded.Locked);
            Assert.Equal(-1500, loaded.WindowX);
            Assert.Equal("/amdcpu/0/temperature/2", loaded.TopSlot.SensorId);
            Assert.Equal("显卡", loaded.BottomSlot.CustomName);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void CorruptFile_FallsBackToDefaults_AndKeepsBackup()
    {
        string dir = Path.Combine(Path.GetTempPath(), "em-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, SettingsService.FileName), "{{{ broken");
            var svc = new SettingsService(dir);
            var s = svc.Load();
            Assert.Equal(1.0, s.Scale);
            Assert.NotNull(svc.LoadDiagnostic);
            Assert.NotEmpty(Directory.GetFiles(dir, SettingsService.FileName + ".corrupt-*"));
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class WidgetStateTests
{
    [Fact]
    public void RestoredLock_WaitsForTray()
    {
        var s = new WidgetState();
        s.Restore(visible: true, topmost: false, locked: true);
        Assert.False(s.Locked);
        Assert.True(s.PendingLock);
        s.SetTrayReady(true);
        Assert.True(s.Locked);
        Assert.False(s.PendingLock);
    }

    [Fact]
    public void CannotLock_WithoutTray()
    {
        var s = new WidgetState();
        Assert.False(s.SetLocked(true));
        Assert.False(s.Locked);
    }

    [Fact]
    public void ShowAndHide_DoNotChangeLock()
    {
        var s = new WidgetState();
        s.SetTrayReady(true);
        s.SetLocked(true);
        s.SetVisible(false);
        s.SetVisible(true);
        Assert.True(s.Locked);
    }

    [Fact]
    public void OpenSettings_ShowsAndUnlocks()
    {
        var s = new WidgetState();
        s.SetTrayReady(true);
        s.SetLocked(true);
        s.SetVisible(false);
        s.PrepareForSettings();
        Assert.True(s.Visible);
        Assert.False(s.Locked);
    }

    [Fact]
    public void TopmostAndLock_AreIndependent()
    {
        var s = new WidgetState();
        s.SetTrayReady(true);
        foreach (var top in new[] { false, true })
        foreach (var locked in new[] { false, true })
        {
            s.SetTopmost(top);
            s.SetLocked(locked);
            Assert.Equal(top, s.Topmost);
            Assert.Equal(locked, s.Locked);
        }
    }

    [Fact]
    public void RescueUnlock_ClearsPendingLock()
    {
        var s = new WidgetState();
        s.Restore(true, false, locked: true);
        s.RescueUnlock();
        s.SetTrayReady(true);
        Assert.False(s.Locked);
    }
}

public class WindowPlacementTests
{
    private static (int, int) Size96(uint dpi) => ((int)(480 * dpi / 96.0), (int)(475.5 * dpi / 96.0));

    private static readonly MonitorArea Primary = new(new PxRect(0, 0, 2560, 1600), new PxRect(0, 0, 2560, 1528), 144, true);
    private static readonly MonitorArea LeftNegative = new(new PxRect(-1920, -200, 1920, 1080), new PxRect(-1920, -200, 1920, 1040), 96, false);

    [Fact]
    public void Position_OnNegativeMonitor_IsKept()
    {
        var r = WindowPlacement.Place(new PxRect(-1500, 0, 480, 475), new[] { Primary, LeftNegative }, Size96);
        Assert.Equal(1, r.MonitorIndex);
        Assert.Equal(-1500, r.Rect.X);
        Assert.Equal(0, r.Rect.Y);
        Assert.Equal(1, r.Shrink);
    }

    [Fact]
    public void DisconnectedMonitor_MovesWindowToNearestVisibleArea()
    {
        // 保存位置在已断开的左侧显示器上。
        var r = WindowPlacement.Place(new PxRect(-1500, 0, 480, 475), new[] { Primary }, Size96);
        Assert.Equal(0, r.MonitorIndex);
        Assert.InRange(r.Rect.X, 0, 2560 - r.Rect.Width);
        Assert.InRange(r.Rect.Y, 0, 1528 - r.Rect.Height);
    }

    [Fact]
    public void PartiallyOffscreen_IsClampedSoCloseButtonIsReachable()
    {
        var r = WindowPlacement.Place(new PxRect(2400, -100, 720, 713), new[] { Primary }, Size96);
        Assert.True(r.Rect.Right <= 2560);
        Assert.True(r.Rect.Y >= 0);
    }

    [Fact]
    public void TinyWorkArea_ShrinksInsteadOfOverflowing()
    {
        var tiny = new MonitorArea(new PxRect(0, 0, 640, 400), new PxRect(0, 0, 640, 360), 144, true);
        var r = WindowPlacement.Place(new PxRect(0, 0, 720, 713), new[] { tiny }, Size96);
        Assert.True(r.Shrink < 1);
        Assert.True(r.Rect.Width <= 640 && r.Rect.Height <= 360);
    }

    [Fact]
    public void Default_IsTopRightOfPrimaryWorkArea()
    {
        var rect = WindowPlacement.DefaultRect(new[] { LeftNegative, Primary }, Size96);
        Assert.Equal(2560 - 720 - 48, rect.X);
        Assert.Equal(48, rect.Y);
    }
}

public class GeometryAndAnimationTests
{
    [Fact]
    public void TightViewport_ContainsStaticDrawingsAndSweepingBands()
    {
        var viewport = GaugeGeometry.TightViewport;
        viewport.Inflate(-2, -2); // Require clearance for antialiasing.
        foreach (var drawing in new[] { GaugeDrawings.Tracks, GaugeDrawings.Core, GaugeDrawings.Overlay })
            Assert.True(viewport.Contains(drawing.Bounds), $"Static drawing bounds {drawing.Bounds} exceed {viewport}.");

        foreach (var band in new[] { GaugeGeometry.TopBand, GaugeGeometry.BottomBand })
        {
            var rotated = new System.Windows.Media.DrawingGroup();
            rotated.Children.Add(GaugeDrawings.BuildBand(band));
            var transform = new System.Windows.Media.RotateTransform(0, GaugeGeometry.CenterX, GaugeGeometry.CenterY);
            rotated.Transform = transform;
            for (int i = 0; i <= 240; i++)
            {
                transform.Angle = band.SweepFrom + (band.SweepTo - band.SweepFrom) * i / 240.0;
                Assert.True(viewport.Contains(rotated.Bounds), $"{band.Label} at {transform.Angle:F2}° exceeds {viewport}.");
            }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BandSweep_StaysInsideColourZone(bool top)
    {
        var band = top ? GaugeGeometry.TopBand : GaugeGeometry.BottomBand;
        for (double t = 0; t <= 96; t += 0.25)
        {
            double a = GaugeGeometry.BandAngleAt(band, t);
            Assert.True(band.Start + a >= band.ZoneStart - 1e-9, $"start {band.Start + a} < zone {band.ZoneStart} at t={t}");
            Assert.True(band.End + a <= band.ZoneEnd + 1e-9, $"end {band.End + a} > zone {band.ZoneEnd} at t={t}");
        }
    }

    [Fact]
    public void BandSweep_MatchesReferenceOffsets()
    {
        Assert.Equal(-36.7, GaugeGeometry.TopBand.SweepFrom, 6);
        Assert.Equal(6.4, GaugeGeometry.TopBand.SweepTo, 6);
        Assert.Equal(-37.0, GaugeGeometry.BottomBand.SweepFrom, 6);
        Assert.Equal(6.6, GaugeGeometry.BottomBand.SweepTo, 6);
    }

    [Fact]
    public void BandSweep_IsLinear_24sOneWay_48sRoundTrip()
    {
        var b = GaugeGeometry.TopBand;
        Assert.Equal(b.SweepFrom, GaugeGeometry.BandAngleAt(b, 0), 9);
        Assert.Equal((b.SweepFrom + b.SweepTo) / 2, GaugeGeometry.BandAngleAt(b, 12), 9);
        Assert.Equal(b.SweepTo, GaugeGeometry.BandAngleAt(b, 24), 9);
        Assert.Equal((b.SweepFrom + b.SweepTo) / 2, GaugeGeometry.BandAngleAt(b, 36), 9);
        Assert.Equal(b.SweepFrom, GaugeGeometry.BandAngleAt(b, 48), 9);
        // 匀速：相同时间间隔的角度变化相同
        double d1 = GaugeGeometry.BandAngleAt(b, 3) - GaugeGeometry.BandAngleAt(b, 1);
        double d2 = GaugeGeometry.BandAngleAt(b, 21) - GaugeGeometry.BandAngleAt(b, 19);
        Assert.Equal(d1, d2, 9);
    }

    [Fact]
    public void Particles_StayInsideCentralClearance()
    {
        var model = new ParticleModel(ParticleModel.CreateReferenceRandom());
        double clearance = GaugeGeometry.ChargeRingRadius - GaugeGeometry.ChargeRingThickness / 2; // 265.5
        for (double t = 0; t < 400; t += 3.7)
        {
            model.Update(t);
            Assert.True(model.MaxRadius * model.Fit <= ParticleModel.FitRadius + 1e-6);
            // 最大粒子半宽 1.6×1.25 设计单位
            Assert.True(ParticleModel.FitRadius + 2 < clearance);
            Assert.True(ParticleModel.FitRadius + 2 <= GaugeGeometry.ParticleCanvasClip);
        }
    }

    [Fact]
    public void Particles_DeformContinuously_WithoutJumps()
    {
        var model = new ParticleModel(ParticleModel.CreateReferenceRandom());
        model.Update(10);
        var prevX = (float[])model.X.Clone();
        var prevY = (float[])model.Y.Clone();
        double maxStep = 0;
        for (int frame = 1; frame <= 900; frame++) // 30 秒 @ 30 FPS
        {
            model.Update(10 + frame / 30.0);
            for (int i = 0; i < ParticleModel.ParticleCount; i += 37)
            {
                double dx = model.X[i] - prevX[i], dy = model.Y[i] - prevY[i];
                maxStep = Math.Max(maxStep, Math.Sqrt(dx * dx + dy * dy));
            }
            Array.Copy(model.X, prevX, prevX.Length);
            Array.Copy(model.Y, prevY, prevY.Length);
        }
        // 单帧位移远小于一个粒子间距级别，说明没有跳变。
        Assert.True(maxStep < 4, $"max per-frame step {maxStep:0.00}");
        Assert.True(maxStep > 0.01, "particles should be moving");
    }

    [Fact]
    public void Particles_AreDeterministicAndStableTopology()
    {
        var a = new ParticleModel(ParticleModel.CreateReferenceRandom());
        var b = new ParticleModel(ParticleModel.CreateReferenceRandom());
        a.Update(42.5); b.Update(42.5);
        Assert.Equal(a.X, b.X);
        Assert.Equal(ParticleModel.RowCount * ParticleModel.ColumnCount, a.X.Length);
    }

    [Fact]
    public void Rasterizer_ProducesTransparentOutsideCircle()
    {
        var model = new ParticleModel(ParticleModel.CreateReferenceRandom());
        model.Update(0);
        var r = new ParticleRasterizer();
        r.EnsureSize(253);
        r.Render(model);
        Assert.Equal(0u, r.Pixels[0]);                       // 左上角
        Assert.Equal(0u, r.Pixels[r.PixelSize * r.PixelSize - 1]); // 右下角
        Assert.Contains(r.Pixels, p => p != 0);              // 有粒子
        // 所有像素都是预乘格式：颜色分量不超过 alpha
        foreach (uint p in r.Pixels)
            Assert.True(((p >> 16) & 0xFF) <= (p >> 24));
    }
}

public class InstanceCommandTests
{
    [Fact]
    public void ParseArgs_OnlyKnownCommands()
    {
        Assert.Equal(new[] { InstanceCommand.Show }, SingleInstanceService.ParseArgs(Array.Empty<string>()));
        Assert.Equal(new[] { InstanceCommand.Unlock, InstanceCommand.ResetWindow },
            SingleInstanceService.ParseArgs(new[] { "--unlock", "--reset-window" }));
        Assert.Equal(new[] { InstanceCommand.Show }, SingleInstanceService.ParseArgs(new[] { "calc.exe", "--exec" }));
    }

    [Fact]
    public void ParseWaitPid()
    {
        Assert.Equal(1234, SingleInstanceService.ParseWaitPid(new[] { "--wait-pid", "1234" }));
        Assert.Null(SingleInstanceService.ParseWaitPid(new[] { "--wait-pid", "x" }));
    }
}
