using System.Windows;
using System.Windows.Media;
using static EndfieldMonitor.Rendering.GaugeGeometry;

namespace EndfieldMonitor.Rendering;

/// <summary>
/// 参考 SVG 静态部分的原生绘制。所有画刷、几何、DrawingGroup 构建一次后冻结并共享。
/// 分层与 SVG 文档顺序一致：
///   Tracks（轨道 / 色块 / 刻度）→ [黑弧动画层] → Core（端帽 / 底座 / 环）
///   → [电量弧] → [粒子层] → Overlay（雾化 / 标签底板）→ [文字与控件]
/// </summary>
public static class GaugeDrawings
{
    public static Color Hex(string hex, double alpha = 1)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex);
        c.A = (byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255);
        return c;
    }

    public static SolidColorBrush Solid(string hex, double alpha = 1) => Frozen(new SolidColorBrush(Hex(hex, alpha)));

    public static T Frozen<T>(T f) where T : Freezable { if (f.CanFreeze) f.Freeze(); return f; }

    private static Pen Pen(string hex, double width, double opacity = 1, PenLineCap cap = PenLineCap.Flat) =>
        Frozen(new Pen(Solid(hex, opacity), width) { StartLineCap = cap, EndLineCap = cap });

    // ── 共享颜色 ───────────────────────────────────────────────
    public static readonly SolidColorBrush Yellow = Solid("#fff000");
    public static readonly SolidColorBrush EmptyRing = Solid("#bfbfb7");
    public static readonly SolidColorBrush Ink = Solid("#292929");
    public static readonly SolidColorBrush BandInk = Solid("#292929");
    public static readonly SolidColorBrush BandLabel = Solid("#eeeeea");

    private static readonly Lazy<DrawingGroup> _tracks = new(BuildTracks);
    private static readonly Lazy<DrawingGroup> _core = new(BuildCore);
    private static readonly Lazy<DrawingGroup> _overlay = new(BuildOverlay);

    public static DrawingGroup Tracks => _tracks.Value;
    public static DrawingGroup Core => _core.Value;
    public static DrawingGroup Overlay => _overlay.Value;

    // ── 图案画刷 ───────────────────────────────────────────────
    private static DrawingBrush Tile(Drawing d, Rect tile, Transform? transform = null)
    {
        var b = new DrawingBrush(d)
        {
            TileMode = TileMode.Tile,
            Viewbox = tile,
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, tile.Width, tile.Height),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.Fill,
        };
        if (transform != null) b.Transform = transform;
        RenderOptions.SetCachingHint(b, CachingHint.Cache);
        return Frozen(b);
    }

    /// <summary>#dark-grid：4×4，深灰底 + 0.45 细网格 + 微点。</summary>
    public static readonly DrawingBrush DarkGrid = BuildDarkGrid();
    private static DrawingBrush BuildDarkGrid()
    {
        var g = new DrawingGroup();
        g.Children.Add(new GeometryDrawing(Solid("#353535"), null, new RectangleGeometry(new Rect(0, 0, 4, 4))));
        var lines = new StreamGeometry();
        using (var c = lines.Open())
        {
            c.BeginFigure(new Point(0, 0), false, false); c.LineTo(new Point(4, 0), true, false);
            c.BeginFigure(new Point(0, 0), false, false); c.LineTo(new Point(0, 4), true, false);
        }
        g.Children.Add(new GeometryDrawing(null, Pen("#606060", 0.45, 0.8), lines));
        g.Children.Add(new GeometryDrawing(Solid("#737373", 0.3), null, new RectangleGeometry(new Rect(1, 1, 0.7, 0.7))));
        g.ClipGeometry = new RectangleGeometry(new Rect(0, 0, 4, 4));
        return Tile(Frozen(g), new Rect(0, 0, 4, 4));
    }

    /// <summary>#hex-grid：7×6 六边形描边。</summary>
    private static DrawingBrush HexGrid()
    {
        var g = new DrawingGroup();
        g.Children.Add(new GeometryDrawing(null, Pen("#eeeeec", 0.8), Geometry.Parse("M1 0H4L6 3 4 6H1L-1 3Z")));
        g.ClipGeometry = new RectangleGeometry(new Rect(0, 0, 7, 6));
        return Tile(Frozen(g), new Rect(0, 0, 7, 6));
    }

    /// <summary>#micro-stripe：8×8，skewX(-32)，4 宽条纹。</summary>
    private static DrawingBrush MicroStripe()
    {
        var g = new DrawingGroup();
        g.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
        g.Children.Add(new GeometryDrawing(Solid("#b7b7b7"), null, new RectangleGeometry(new Rect(0, 0, 4, 8))));
        return Tile(Frozen(g), new Rect(0, 0, 8, 8), Frozen(new SkewTransform(-32, 0)));
    }

    private static RadialGradientBrush Radial(double r, params (double offset, string color)[] stops)
    {
        var b = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            Center = Center,
            GradientOrigin = Center,
            RadiusX = r,
            RadiusY = r,
        };
        foreach (var (o, c) in stops) b.GradientStops.Add(new GradientStop(Hex(c), o));
        return Frozen(b);
    }

    // ── Tracks：外轨、色块、细刻度、刻度线 ─────────────────────
    private static DrawingGroup BuildTracks()
    {
        var g = new DrawingGroup();
        foreach (var (start, end) in new[] { (180.0, 270.0), (0.0, 90.0) })
        {
            g.Children.Add(new GeometryDrawing(null, Pen("#adadad", 2.5), Arc(395, start, end)));
            g.Children.Add(new GeometryDrawing(null, Pen("#b0b0ae", 2.5), Arc(314, start, end)));
            g.Children.Add(new GeometryDrawing(null, Pen("#f4f4f1", 2), Arc(390, start, end)));
        }

        var orangeWash = Radial(390, (0.80, "#ffe49a"), (0.94, "#ffdc8d"), (0.945, "#ffe4b4"), (1, "#f8e9ce"));
        var blueWash = Radial(388, (0.81, "#a8e8ff"), (1, "#abe9fa"));
        g.Children.Add(new GeometryDrawing(orangeWash, null, Sector(OrangeInner, OrangeOuter, OrangeStart, OrangeEnd)));

        // 与参考页相同的 Park–Miller 序列（种子 8217）。
        var random = new ParkMillerRandom(8217);
        var orangeA = Hex("#ffad62"); var orangeB = Hex("#fff2a4");
        for (int i = 0; i < 116; i++)
        {
            double a = 181 + i * 0.756;
            double width = 0.3 + random.Next() * 0.76;
            double opacity = 0.14 + random.Next() * 0.4;
            var c = i % 4 == 0 ? orangeA : orangeB;
            g.Children.Add(new GeometryDrawing(Frozen(new SolidColorBrush(c) { Opacity = opacity }), null, Sector(324, 385, a, a + width)));
        }

        g.Children.Add(new GeometryDrawing(blueWash, null, Sector(BlueInner, BlueOuter, BlueStart, BlueEnd)));
        var blueA = Hex("#7bd0ed"); var blueB = Hex("#c3f6ff");
        for (int i = 0; i < 89; i++)
        {
            double a = 0.9 + i * 0.99;
            double width = 0.3 + random.Next() * 1.3;
            double opacity = 0.1 + random.Next() * 0.32;
            var c = i % 4 == 0 ? blueA : blueB;
            g.Children.Add(new GeometryDrawing(Frozen(new SolidColorBrush(c) { Opacity = opacity }), null, Sector(324, 382, a, a + width)));
        }

        g.Children.Add(new GeometryDrawing(null, Pen("#ff9951", 3.5), Arc(367, 181, 269)));
        g.Children.Add(new GeometryDrawing(null, Pen("#6bcef5", 3.5), Arc(320, 0, 89.6)));
        var tickA = Pen("#a5a5a0", 2, 0.4);
        for (int a = 195; a < 270; a += 15)
            g.Children.Add(new GeometryDrawing(null, tickA, Frozen(new LineGeometry(PointAt(316, a), PointAt(392, a)))));
        var tickB = Pen("#85bdd1", 2, 0.5);
        for (int a = 15; a < 90; a += 15)
            g.Children.Add(new GeometryDrawing(null, tickB, Frozen(new LineGeometry(PointAt(316, a), PointAt(391, a)))));
        return Frozen(g);
    }

    // ── Core：端帽、机械底座、环底、中央雾化底、核心晕染 ─────────
    private static DrawingGroup BuildCore()
    {
        var g = new DrawingGroup();
        var capPen = Pen("#666664", 2);
        foreach (var d in new[]
        {
            "M132 539V546M140 541H149M140 545H205M205 539V546M212 539V546",
            "M523 150H530M526 157V222M523 231H530",
            "M836 539V546M844 540H911M844 544H854M908 544H916M917 539V546",
            "M520 854H527M525 861V927M520 934H527",
        })
            g.Children.Add(new GeometryDrawing(null, capPen, Geometry.Parse(d)));
        var whitePen = Pen("#f6f6f3", 3);
        foreach (var d in new[] { "M140 545H205", "M526 157V222", "M847 542H909", "M525 861V927" })
            g.Children.Add(new GeometryDrawing(null, whitePen, Geometry.Parse(d)));

        // 机械底座路径在 SVG 中是整块填充，原页面由不透明的 r=253 圆盘遮住中央。
        // 桌面版去掉该不透明圆盘，因此将底座裁成 r≥253 的环形，保留可见的灰色结构与缺口。
        var basePath = Geometry.Parse(MechanicalBasePath);
        var baseRing = Frozen(new CombinedGeometry(GeometryCombineMode.Exclude, basePath, new EllipseGeometry(Center, 253, 253)));
        g.Children.Add(new GeometryDrawing(Solid("#c3c3c3"), null, baseRing));
        g.Children.Add(new GeometryDrawing(null, Pen("#c1c1c1", RingBaseThickness), Frozen(new EllipseGeometry(Center, RingBaseRadius, RingBaseRadius))));

        // 原页面中央为不透明 #e7e7e7 圆盘；桌面版改为柔和半透明雾化底，既能透出桌面，又保证中央读数可读。
        var disc = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            Center = Center, GradientOrigin = Center, RadiusX = 253, RadiusY = 253,
        };
        disc.GradientStops.Add(new GradientStop(Hex("#ebebe9", 0.62), 0));
        disc.GradientStops.Add(new GradientStop(Hex("#e7e7e7", 0.52), 0.8));
        disc.GradientStops.Add(new GradientStop(Hex("#e1e1df", 0.46), 1));
        g.Children.Add(new GeometryDrawing(Frozen(disc), null, Frozen(new EllipseGeometry(Center, 253, 253))));

        // 环形电量底轨（中性灰）；有效电量由独立的黄色电量弧绘制在其上。
        g.Children.Add(new GeometryDrawing(null, Frozen(new Pen(EmptyRing, ChargeRingThickness)), Frozen(new EllipseGeometry(Center, ChargeRingRadius, ChargeRingRadius))));

        // #core-wash
        var core = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
            Center = new Point(0.49, 0.46), GradientOrigin = new Point(0.49, 0.46), RadiusX = 0.54, RadiusY = 0.54,
        };
        core.GradientStops.Add(new GradientStop(Hex("#c1c1c1", 0.8), 0));
        core.GradientStops.Add(new GradientStop(Hex("#b7b7b7", 0.6), 0.75));
        core.GradientStops.Add(new GradientStop(Hex("#a5a5a5", 0.04), 1));
        g.Children.Add(new GeometryDrawing(Frozen(core), null, Frozen(new EllipseGeometry(new Point(527, 546), 147, 133))));
        return Frozen(g);
    }

    // ── Overlay：读数雾化、上下标签底板与连接线 ─────────────────
    private static DrawingGroup BuildOverlay()
    {
        var g = new DrawingGroup();
        var haze = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        haze.GradientStops.Add(new GradientStop(Hex("#f2f2f2", 0.3), 0));
        haze.GradientStops.Add(new GradientStop(Hex("#f2f2f2", 0.58), 1));
        g.Children.Add(new GeometryDrawing(Frozen(haze), null, Geometry.Parse("M294 467H758L771 574 747 631H310L283 552Z")));

        var border = Pen("#bcbcbc", 0.7);
        var stripe = MicroStripe();

        // 顶部标签
        g.Children.Add(new GeometryDrawing(Solid("#d2d0d0"), border, Frozen(new RectangleGeometry(new Rect(541, 149, 402, 85), 3, 3))));
        g.Children.Add(new GeometryDrawing(DarkGrid, null, Geometry.Parse("M544 149H766V234H544Q541 234 541 231V152Q541 149 544 149Z")));
        var s1 = new DrawingGroup { Opacity = 0.32 };
        s1.Children.Add(new GeometryDrawing(stripe, null, Frozen(new RectangleGeometry(new Rect(781, 154, 147, 9)))));
        g.Children.Add(s1);
        g.Children.Add(new GeometryDrawing(null, Pen("#c7c7c7", 1), Geometry.Parse("M951 153V231")));
        g.Children.Add(new GeometryDrawing(null, Pen("#575757", 2.5), Geometry.Parse("M951 148V153M951 231V236")));

        // 底部标签
        g.Children.Add(new GeometryDrawing(Solid("#d1cfcf"), border, Frozen(new RectangleGeometry(new Rect(107, 850, 403, 85), 3, 3))));
        var hex = new DrawingGroup { Opacity = 0.5, ClipGeometry = Frozen(new RectangleGeometry(new Rect(107, 850, 177, 85), 3, 3)) };
        hex.Children.Add(new GeometryDrawing(HexGrid(), null, Frozen(new RectangleGeometry(new Rect(107, 850, 74, 85)))));
        g.Children.Add(hex);
        var s2 = new DrawingGroup { Opacity = 0.25 };
        s2.Children.Add(new GeometryDrawing(stripe, null, Frozen(new RectangleGeometry(new Rect(121, 854, 147, 9)))));
        g.Children.Add(s2);
        g.Children.Add(new GeometryDrawing(DarkGrid, null, Geometry.Parse("M284 850H507Q510 850 510 853V932Q510 935 507 935H284Z")));
        g.Children.Add(new GeometryDrawing(null, Pen("#c7c7c7", 1), Geometry.Parse("M100 854V932")));
        g.Children.Add(new GeometryDrawing(null, Pen("#575757", 2.5), Geometry.Parse("M100 849V854M100 932V937")));
        return Frozen(g);
    }

    /// <summary>黑弧带及其端部小字（作为一个整体旋转）。</summary>
    public static DrawingGroup BuildBand(BandSpec band)
    {
        var g = new DrawingGroup();
        g.Children.Add(new GeometryDrawing(BandInk, null, SteppedBand(band)));
        var p = PointAt((band.Inner + band.ThickOuter) / 2, band.LabelAngle);
        var tf = new Typeface(new FontFamily("Arial"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        var text = new FormattedText(band.Label, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, 6, BandLabel, 1.0);
        var textGeometry = text.BuildGeometry(new Point(p.X - text.Width / 2, p.Y - text.Height / 2));
        textGeometry.Transform = new RotateTransform(-50, p.X, p.Y);
        g.Children.Add(new GeometryDrawing(BandLabel, null, Frozen(textGeometry)));
        return Frozen(g);
    }
}
