using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EndfieldMonitor.Rendering;

namespace EndfieldMonitor.Controls;

/// <summary>直接绘制一个（冻结的）Drawing 的轻量元素，不参与复杂布局。</summary>
public sealed class DrawingElement : FrameworkElement
{
    public static readonly DependencyProperty DrawingProperty = DependencyProperty.Register(
        nameof(Drawing), typeof(Drawing), typeof(DrawingElement),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public Drawing? Drawing { get => (Drawing?)GetValue(DrawingProperty); set => SetValue(DrawingProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        if (Drawing != null) dc.DrawDrawing(Drawing);
    }
}

/// <summary>电量弧：Fraction 为 null 时不绘制（中性状态，只剩灰色底轨）。</summary>
public sealed class ChargeRing : FrameworkElement
{
    public static readonly DependencyProperty FractionProperty = DependencyProperty.Register(
        nameof(Fraction), typeof(double?), typeof(ChargeRing),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public double? Fraction { get => (double?)GetValue(FractionProperty); set => SetValue(FractionProperty, value); }

    private static readonly Pen RingPen = GaugeDrawings.Frozen(new Pen(GaugeDrawings.Yellow, GaugeGeometry.ChargeRingThickness));

    public ChargeRing() { IsHitTestVisible = false; }

    protected override void OnRender(DrawingContext dc)
    {
        if (Fraction is not double f) return;
        var g = GaugeGeometry.ChargeArc(Math.Clamp(f, 0, 1));
        if (g != null) dc.DrawGeometry(null, RingPen, g);
    }
}

/// <summary>
/// 中央粒子层。粒子用软件光栅化写入一张复用的 WriteableBitmap（仅粒子层是位图，带透明通道），
/// 核心柔光与细线仍以矢量绘制。默认约 30 FPS，由 DispatcherTimer 驱动，不强制整个界面持续重绘；
/// 隐藏、禁用或系统减少动画时停止计时器，并保留一帧静态图形。
/// 元素尺寸为设计坐标中的 506×506 粒子画布。
/// </summary>
public sealed class ParticleCloud : FrameworkElement
{
    public static readonly DependencyProperty IsAnimatingProperty = DependencyProperty.Register(
        nameof(IsAnimating), typeof(bool), typeof(ParticleCloud),
        new PropertyMetadata(true, (d, _) => ((ParticleCloud)d).SyncTimer()));

    /// <summary>设计单位 → 物理像素 的比例（窗口缩放 × DPI），决定位图分辨率。</summary>
    public static readonly DependencyProperty PixelScaleProperty = DependencyProperty.Register(
        nameof(PixelScale), typeof(double), typeof(ParticleCloud),
        new PropertyMetadata(1.0, (d, _) => ((ParticleCloud)d).OnPixelScaleChanged()));

    public bool IsAnimating { get => (bool)GetValue(IsAnimatingProperty); set => SetValue(IsAnimatingProperty, value); }
    public double PixelScale { get => (double)GetValue(PixelScaleProperty); set => SetValue(PixelScaleProperty, value); }

    public const double TargetFps = 30;
    private const double MaxStepSeconds = 0.1; // 与参考一致：单帧最多推进 0.1 秒，恢复时不跳变

    private readonly ParticleModel _model = new(ParticleModel.CreateReferenceRandom());
    private readonly ParticleRasterizer _raster = new();
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private WriteableBitmap? _bitmap;
    private double _particleTime;
    private long _lastTicks = -1;
    private bool _hostVisible = true;
    private bool _dirtyStatic = true;

    // 统计（供状态面板显示）
    public double LastFrameMs { get; private set; }
    public double AverageFrameMs { get; private set; }
    /// <summary>最近 2 秒内实际达到的粒子帧率。</summary>
    public double MeasuredFps { get; private set; }
    private int _framesInWindow;
    private long _fpsWindowStart = Stopwatch.GetTimestamp();

    private static readonly Geometry ClipGeometry = GaugeDrawings.Frozen(new EllipseGeometry(
        new Point(ParticleRasterizer.CanvasCenter, ParticleRasterizer.CanvasCenter), ParticleRasterizer.ClipRadius, ParticleRasterizer.ClipRadius));

    private static readonly RadialGradientBrush CoreBrush = BuildCoreBrush();
    private static readonly Pen[] LinePens = BuildLinePens();
    private static readonly Geometry[] LineArcs = BuildLineArcs();

    public ParticleCloud()
    {
        Width = Height = ParticleRasterizer.CanvasSize;
        IsHitTestVisible = false;
        // Win32 计时器按约 15.6 ms 的系统时钟粒度向上取整：33 ms 间隔实际为 46.9 ms（≈21 FPS）。
        // 31 ms 取整为 2 个时钟周期（31.25 ms ≈ 32 FPS），使实测帧率达到约 30 FPS 的目标。
        // 粒子时间按单调时钟累计，因此帧间隔变化不会影响播放速度。
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(31) };
        _timer.Tick += OnTick;
        Loaded += (_, _) => { SyncTimer(); };
        Unloaded += (_, _) => { _timer.Stop(); _clock.Stop(); };
        IsVisibleChanged += (_, _) => { _hostVisible = IsVisible; SyncTimer(); };
        _model.Update(0);
    }

    private static RadialGradientBrush BuildCoreBrush()
    {
        // canvas: createRadialGradient(-30,-35,15, 0,0,145)；内圆半径 15 折算为 offset≈0.1。
        var b = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            Center = new Point(0, 0),
            GradientOrigin = new Point(-30, -35),
            RadiusX = 145,
            RadiusY = 145,
        };
        b.GradientStops.Add(new GradientStop(GaugeDrawings.Hex("#adadad", 0.52), 0.1));
        b.GradientStops.Add(new GradientStop(GaugeDrawings.Hex("#a5a5a5", 0.48), 0.86));
        b.GradientStops.Add(new GradientStop(GaugeDrawings.Hex("#a5a5a5", 0.05), 1));
        return GaugeDrawings.Frozen(b);
    }

    private static Pen[] BuildLinePens()
    {
        var pens = new Pen[15];
        for (int i = 0; i < 15; i++)
            pens[i] = GaugeDrawings.Frozen(new Pen(GaugeDrawings.Solid("#e9e9e9", 0.08 + (i % 3) * 0.022), 1));
        return pens;
    }

    /// <summary>ctx.ellipse(0, -8 + i*.7, 142 - i*.9, 72 + i*3.3, i*.016, .15, 4.1) 的矢量等价。</summary>
    private static Geometry[] BuildLineArcs()
    {
        var arcs = new Geometry[15];
        for (int i = 0; i < 15; i++)
        {
            double cy = -8 + i * 0.7, rx = 142 - i * 0.9, ry = 72 + i * 3.3, rot = i * 0.016;
            const double a0 = 0.15, a1 = 4.1;
            Point P(double t)
            {
                double x = rx * Math.Cos(t), y = ry * Math.Sin(t);
                return new Point(x * Math.Cos(rot) - y * Math.Sin(rot), cy + x * Math.Sin(rot) + y * Math.Cos(rot));
            }
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(P(a0), false, false);
                c.ArcTo(P(a1), new Size(rx, ry), rot * 180 / Math.PI, a1 - a0 > Math.PI, SweepDirection.Clockwise, true, false);
            }
            arcs[i] = GaugeDrawings.Frozen(g);
        }
        return arcs;
    }

    /// <summary>宿主窗口显示 / 隐藏时调用（窗口隐藏时 IsVisible 也会变化，此处作为显式保障）。</summary>
    public void SetHostVisible(bool visible)
    {
        _hostVisible = visible;
        SyncTimer();
    }

    private bool ShouldAnimate => IsAnimating && _hostVisible && IsLoaded && SystemParameters.ClientAreaAnimation;

    private void SyncTimer()
    {
        if (ShouldAnimate)
        {
            if (!_timer.IsEnabled)
            {
                _lastTicks = -1; // 恢复时不累计隐藏期间的时间
                _framesInWindow = 0;
                _fpsWindowStart = Stopwatch.GetTimestamp();
                _clock.Start();
                _timer.Start();
            }
        }
        else
        {
            _timer.Stop();
            _clock.Stop();
            _lastTicks = -1;
            MeasuredFps = 0;
            if (!SystemParameters.ClientAreaAnimation)
            {
                // 系统减少动画：与参考页相同，显示 t=0 的静态形态。
                _particleTime = 0;
                _model.Update(0);
            }
            _dirtyStatic = true;
            InvalidateVisual();
        }
    }

    /// <summary>系统动画设置变化时由窗口调用。</summary>
    public void RefreshMotionPreference() => SyncTimer();

    private void OnPixelScaleChanged()
    {
        _dirtyStatic = true;
        InvalidateVisual();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        long now = _clock.ElapsedTicks;
        if (_lastTicks >= 0)
        {
            double dt = (now - _lastTicks) / (double)Stopwatch.Frequency;
            _particleTime += Math.Min(dt, MaxStepSeconds);
        }
        _lastTicks = now;

        long t0 = Stopwatch.GetTimestamp();
        _model.Update(_particleTime);
        RenderBitmap();
        double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        LastFrameMs = ms;
        AverageFrameMs = AverageFrameMs == 0 ? ms : AverageFrameMs * 0.95 + ms * 0.05;
        _framesInWindow++;
        long nowTs = Stopwatch.GetTimestamp();
        double windowSec = (nowTs - _fpsWindowStart) / (double)Stopwatch.Frequency;
        if (windowSec >= 2)
        {
            MeasuredFps = _framesInWindow / windowSec;
            _framesInWindow = 0;
            _fpsWindowStart = nowTs;
        }
        InvalidateVisual();
    }

    private void RenderBitmap()
    {
        int px = (int)Math.Ceiling(ParticleRasterizer.CanvasSize * Math.Max(0.1, PixelScale));
        _raster.EnsureSize(px);
        if (_bitmap == null || _bitmap.PixelWidth != _raster.PixelSize)
            _bitmap = new WriteableBitmap(_raster.PixelSize, _raster.PixelSize, 96, 96, PixelFormats.Pbgra32, null);
        _raster.Render(_model);
        _bitmap.WritePixels(new Int32Rect(0, 0, _raster.PixelSize, _raster.PixelSize), _raster.Pixels, _raster.PixelSize * 4, 0);
        _dirtyStatic = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_dirtyStatic || _bitmap == null) RenderBitmap();
        var box = new Rect(0, 0, ParticleRasterizer.CanvasSize, ParticleRasterizer.CanvasSize);
        dc.PushClip(ClipGeometry);
        dc.DrawImage(_bitmap, box);

        double c = ParticleRasterizer.CanvasCenter;
        double fit = _model.Fit;
        var n = _model.Noise;
        var m = Matrix.Identity;
        m.Rotate((-0.17 + n[1] * 0.04) * 180 / Math.PI);
        m.Scale(fit, fit);
        m.Translate(c, c);
        dc.PushTransform(new MatrixTransform(m));
        dc.DrawEllipse(CoreBrush, null, new Point(0, 0), 146 + n[6] * 3, 125 + n[7] * 3);
        for (int i = 0; i < 15; i++) dc.DrawGeometry(null, LinePens[i], LineArcs[i]);
        dc.Pop();
        dc.Pop();
    }

    public void Stop()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
        _clock.Stop();
    }
}
