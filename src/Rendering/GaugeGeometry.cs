using System.Windows;
using System.Windows.Media;

namespace EndfieldMonitor.Rendering;

/// <summary>
/// 与参考 SVG 完全一致的设计坐标系参数（viewBox 1065×1055，圆心 526,545）。
/// 所有几何集中在此处，界面其它部分只引用这些常量。
/// </summary>
public static class GaugeGeometry
{
    public const double DesignWidth = 1065;
    public const double DesignHeight = 1055;
    public const double CenterX = 526;
    public const double CenterY = 545;
    public static readonly Point Center = new(CenterX, CenterY);

    /// <summary>保留原来 480 DIP 宽的设计画布比例，只裁掉画布外围的透明区域。</summary>
    public const double DefaultDesignToDip = 480.0 / DesignWidth;
    // 静态图形和弧带全程约占 x=99…953、y=136…950，留少量边距防止裁切。
    public static readonly Rect TightViewport = new(84, 116, 884, 850);

    // 主电池环
    public const double ChargeRingRadius = 277.5;
    public const double ChargeRingThickness = 24;
    public const double RingBaseRadius = 286;
    public const double RingBaseThickness = 43;
    public const string MechanicalBasePath =
        "M599 244A311 311 0 0 1 827 466L820 477A307 307 0 0 1 459 850L449 842A311 311 0 0 1 223 621L229 611A307 307 0 0 1 590 250Z";

    // 色块（橙色：上方 / 用电，蓝色：下方 / 发电）
    public const double OrangeInner = 324, OrangeOuter = 387, OrangeStart = 180.8, OrangeEnd = 269.1;
    public const double BlueInner = 324, BlueOuter = 383, BlueStart = 0.8, BlueEnd = 89.6;

    // 粒子区域
    public const double ParticleBoxX = 273, ParticleBoxY = 292, ParticleBoxSize = 506;
    public const double ParticleClipRadius = 250;   // SVG clipPath
    public const double ParticleCanvasClip = 246;   // canvas 内部 arc clip
    public const double ParticleFitRadius = 242;    // fit 约束半径

    /// <summary>黑色阶梯弧带参数。</summary>
    public sealed record BandSpec(
        double Inner, double ThickOuter, double ThinOuter,
        double Start, double Step, double End,
        double LabelAngle, string Label,
        double SweepFrom, double SweepTo,
        double ZoneStart, double ZoneEnd);

    /// <summary>上方黑弧：橙色边缘 180.8°–269.1°，减去弧带自身 217.5°–262.7° 得到 -36.7°…6.4°。</summary>
    public static readonly BandSpec TopBand = new(402, 415, 409, 217.5, 240, 262.7, 220, "P·IN",
        OrangeStart - 217.5, OrangeEnd - 262.7, OrangeStart, OrangeEnd);

    /// <summary>下方黑弧：蓝色边缘 0.8°–89.6°，减去弧带自身 37.8°–83° 得到 -37°…6.6°。</summary>
    public static readonly BandSpec BottomBand = new(398, 411, 405, 37.8, 60.1, 83, 40, "P·OUT",
        BlueStart - 37.8, BlueEnd - 83, BlueStart, BlueEnd);

    /// <summary>黑弧单程时长（秒），往返为其两倍。</summary>
    public const double BandOneWaySeconds = 24;

    /// <summary>
    /// 线性往返角度：t=0 位于 from，单程 24 秒到 to，然后反向（与 CSS alternate linear 一致）。
    /// </summary>
    public static double BandAngleAt(BandSpec band, double seconds)
    {
        double period = BandOneWaySeconds * 2;
        double t = seconds % period;
        if (t < 0) t += period;
        double f = t <= BandOneWaySeconds ? t / BandOneWaySeconds : 2 - t / BandOneWaySeconds;
        return band.SweepFrom + (band.SweepTo - band.SweepFrom) * f;
    }

    public static Point PointAt(double radius, double angleDeg)
    {
        double a = angleDeg * Math.PI / 180;
        return new Point(CenterX + radius * Math.Cos(a), CenterY + radius * Math.Sin(a));
    }

    /// <summary>与参考 JS arc() 相同：顺时针（SVG sweep=1）圆弧。</summary>
    public static StreamGeometry Arc(double r, double start, double end)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(PointAt(r, start), false, false);
            ctx.ArcTo(PointAt(r, end), new Size(r, r), 0, end - start > 180, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }

    /// <summary>与参考 JS sector() 相同的闭合环形扇区。</summary>
    public static StreamGeometry Sector(double inner, double outer, double start, double end)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(PointAt(outer, start), true, true);
            ctx.ArcTo(PointAt(outer, end), new Size(outer, outer), 0, end - start > 180, SweepDirection.Clockwise, true, false);
            ctx.LineTo(PointAt(inner, end), true, false);
            ctx.ArcTo(PointAt(inner, start), new Size(inner, inner), 0, false, SweepDirection.Counterclockwise, true, false);
        }
        g.Freeze();
        return g;
    }

    /// <summary>
    /// 阶梯弧带：共用连续内缘，仅外缘在交接角处收窄；单一闭合轮廓，无描边接缝。
    /// </summary>
    public static StreamGeometry SteppedBand(BandSpec b)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(PointAt(b.ThickOuter, b.Start), true, true);
            ctx.ArcTo(PointAt(b.ThickOuter, b.Step), new Size(b.ThickOuter, b.ThickOuter), 0, false, SweepDirection.Clockwise, true, false);
            ctx.LineTo(PointAt(b.ThinOuter, b.Step), true, false);
            ctx.ArcTo(PointAt(b.ThinOuter, b.End), new Size(b.ThinOuter, b.ThinOuter), 0, false, SweepDirection.Clockwise, true, false);
            ctx.LineTo(PointAt(b.Inner, b.End), true, false);
            ctx.ArcTo(PointAt(b.Inner, b.Start), new Size(b.Inner, b.Inner), 0, false, SweepDirection.Counterclockwise, true, false);
        }
        g.Freeze();
        return g;
    }

    /// <summary>
    /// 电量弧：自 12 点方向顺时针，fraction ∈ [0,1]。返回 null 表示空。
    /// </summary>
    public static Geometry? ChargeArc(double fraction)
    {
        if (double.IsNaN(fraction) || fraction <= 0) return null;
        if (fraction >= 0.99999)
            return Frozen(new EllipseGeometry(Center, ChargeRingRadius, ChargeRingRadius));
        double start = -90, end = -90 + 360 * fraction;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(PointAt(ChargeRingRadius, start), false, false);
            ctx.ArcTo(PointAt(ChargeRingRadius, end), new Size(ChargeRingRadius, ChargeRingRadius), 0,
                end - start > 180, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }
}

/// <summary>参考页使用的 Park–Miller 最小标准随机数（种子 8217），保证色块与漂移通道和参考一致。</summary>
public sealed class ParkMillerRandom
{
    private long _seed;
    public ParkMillerRandom(long seed) { _seed = seed; }
    public double Next()
    {
        _seed = _seed * 16807 % 2147483647;
        return (_seed - 1) / 2147483646.0;
    }
}
