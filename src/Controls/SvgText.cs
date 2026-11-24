using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace EndfieldMonitor.Controls;

public enum TextAnchor { Start, Middle, End }

/// <summary>
/// 以 SVG 语义（x / 基线 y / text-anchor）绘制的轻量文字元素。文字变化只触发重绘，不触发布局。
/// 超出 <see cref="MaxTextWidth"/> 时先缩小字号到 <see cref="MinFontSize"/>，再以省略号截断。
/// </summary>
public class SvgText : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = Reg(nameof(Text), typeof(string), "");
    public static readonly DependencyProperty XProperty = Reg(nameof(X), typeof(double), 0.0);
    public static readonly DependencyProperty YProperty = Reg(nameof(Y), typeof(double), 0.0);
    public static readonly DependencyProperty FontSizeProperty = Reg(nameof(FontSize), typeof(double), 16.0);
    public static readonly DependencyProperty MinFontSizeProperty = Reg(nameof(MinFontSize), typeof(double), 0.0);
    public static readonly DependencyProperty MaxTextWidthProperty = Reg(nameof(MaxTextWidth), typeof(double), 0.0);
    public static readonly DependencyProperty FillProperty = Reg(nameof(Fill), typeof(Brush), Brushes.Black);
    public static readonly DependencyProperty FontWeightProperty = Reg(nameof(FontWeight), typeof(FontWeight), FontWeights.Normal);
    public static readonly DependencyProperty FontFamilyProperty = Reg(nameof(FontFamily), typeof(FontFamily), new FontFamily("Arial, Microsoft YaHei"));
    public static readonly DependencyProperty AnchorProperty = Reg(nameof(Anchor), typeof(TextAnchor), TextAnchor.Start);
    public static readonly DependencyProperty RotationProperty = Reg(nameof(Rotation), typeof(double), 0.0);
    public static readonly DependencyProperty CenterVerticallyProperty = Reg(nameof(CenterVertically), typeof(bool), false);

    private static DependencyProperty Reg(string name, Type type, object def) =>
        DependencyProperty.Register(name, type, typeof(SvgText), new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public double X { get => (double)GetValue(XProperty); set => SetValue(XProperty, value); }
    public double Y { get => (double)GetValue(YProperty); set => SetValue(YProperty, value); }
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public double MinFontSize { get => (double)GetValue(MinFontSizeProperty); set => SetValue(MinFontSizeProperty, value); }
    public double MaxTextWidth { get => (double)GetValue(MaxTextWidthProperty); set => SetValue(MaxTextWidthProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public FontWeight FontWeight { get => (FontWeight)GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }
    public FontFamily FontFamily { get => (FontFamily)GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
    public TextAnchor Anchor { get => (TextAnchor)GetValue(AnchorProperty); set => SetValue(AnchorProperty, value); }
    public double Rotation { get => (double)GetValue(RotationProperty); set => SetValue(RotationProperty, value); }
    public bool CenterVertically { get => (bool)GetValue(CenterVerticallyProperty); set => SetValue(CenterVerticallyProperty, value); }

    public SvgText()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = false;
    }

    internal static FormattedText Create(string text, FontFamily family, FontWeight weight, double size, Brush fill, double pixelsPerDip)
    {
        var tf = new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal);
        return new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, tf, size, fill, pixelsPerDip);
    }

    /// <summary>生成适配宽度后的文字；供派生控件复用。</summary>
    internal static FormattedText Fit(string text, FontFamily family, FontWeight weight, double size, double minSize, double maxWidth, Brush fill, double ppd)
    {
        var ft = Create(text, family, weight, size, fill, ppd);
        if (maxWidth <= 0 || ft.WidthIncludingTrailingWhitespace <= maxWidth) return ft;
        if (minSize > 0 && minSize < size)
        {
            double fitted = Math.Max(minSize, size * maxWidth / ft.WidthIncludingTrailingWhitespace);
            ft = Create(text, family, weight, Math.Floor(fitted * 2) / 2, fill, ppd);
            if (ft.WidthIncludingTrailingWhitespace <= maxWidth) return ft;
        }
        ft.MaxTextWidth = maxWidth;
        ft.MaxLineCount = 1;
        ft.Trimming = TextTrimming.CharacterEllipsis;
        return ft;
    }

    internal static Point Origin(FormattedText ft, double x, double y, TextAnchor anchor, bool centerV)
    {
        double w = ft.MaxTextWidth > 0 ? Math.Min(ft.Width, ft.MaxTextWidth) : ft.WidthIncludingTrailingWhitespace;
        double left = anchor switch { TextAnchor.Middle => x - w / 2, TextAnchor.End => x - w, _ => x };
        double top = centerV ? y - ft.Height / 2 : y - ft.Baseline;
        return new Point(left, top);
    }

    protected override void OnRender(DrawingContext dc)
    {
        string text = Text;
        if (string.IsNullOrEmpty(text)) return;
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var ft = Fit(text, FontFamily, FontWeight, FontSize, MinFontSize, MaxTextWidth, Fill, ppd);
        bool rotate = Rotation != 0;
        if (rotate) dc.PushTransform(new RotateTransform(Rotation, X, Y));
        dc.DrawText(ft, Origin(ft, X, Y, Anchor, CenterVertically));
        if (rotate) dc.Pop();
    }
}

/// <summary>
/// 监控槽位数值：数字（Arial 等宽数字，不拉伸）+ 单位；无数值时显示 “--” 与状态说明。
/// Anchor=End 时整体右对齐到 X（上方标签），Anchor=Start 时从 X 开始（下方标签）。
/// </summary>
public sealed class SlotValueText : FrameworkElement
{
    public static readonly DependencyProperty ValueTextProperty = Reg(nameof(ValueText), typeof(string), "");
    public static readonly DependencyProperty UnitTextProperty = Reg(nameof(UnitText), typeof(string), "");
    public static readonly DependencyProperty StatusTextProperty = Reg(nameof(StatusText), typeof(string), "");
    public static readonly DependencyProperty IsStaleProperty = Reg(nameof(IsStale), typeof(bool), false);
    public static readonly DependencyProperty XProperty = Reg(nameof(X), typeof(double), 0.0);
    public static readonly DependencyProperty YProperty = Reg(nameof(Y), typeof(double), 0.0);
    public static readonly DependencyProperty AnchorProperty = Reg(nameof(Anchor), typeof(TextAnchor), TextAnchor.Start);
    public static readonly DependencyProperty AvailableWidthProperty = Reg(nameof(AvailableWidth), typeof(double), 200.0);

    private static DependencyProperty Reg(string name, Type type, object def) =>
        DependencyProperty.Register(name, type, typeof(SlotValueText), new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender));

    public string ValueText { get => (string)GetValue(ValueTextProperty); set => SetValue(ValueTextProperty, value); }
    public string UnitText { get => (string)GetValue(UnitTextProperty); set => SetValue(UnitTextProperty, value); }
    public string StatusText { get => (string)GetValue(StatusTextProperty); set => SetValue(StatusTextProperty, value); }
    public bool IsStale { get => (bool)GetValue(IsStaleProperty); set => SetValue(IsStaleProperty, value); }
    public double X { get => (double)GetValue(XProperty); set => SetValue(XProperty, value); }
    public double Y { get => (double)GetValue(YProperty); set => SetValue(YProperty, value); }
    public TextAnchor Anchor { get => (TextAnchor)GetValue(AnchorProperty); set => SetValue(AnchorProperty, value); }
    public double AvailableWidth { get => (double)GetValue(AvailableWidthProperty); set => SetValue(AvailableWidthProperty, value); }

    private static readonly FontFamily DigitFont = new("Arial, Microsoft YaHei");
    private static readonly FontFamily CjkFont = new("Microsoft YaHei UI, Microsoft YaHei, Arial");
    private static readonly Brush ValueBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)));
    private static readonly Brush StaleBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)));
    private static readonly Brush UnitBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xC9, 0xC9, 0xC4)));
    private static readonly Brush StatusBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xD8, 0xD8, 0xD3)));

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    public const double ValueFontSize = 55;
    public const double UnitFontSize = 24;
    public const double StatusFontSize = 21;
    private const double Gap = 6;

    public SlotValueText() { IsHitTestVisible = false; }

    protected override void OnRender(DrawingContext dc)
    {
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        bool hasValue = !string.IsNullOrEmpty(ValueText);
        string number = hasValue ? ValueText : "--";
        string suffix = hasValue ? UnitText ?? "" : StatusText ?? "";
        var suffixFont = hasValue ? DigitFont : CjkFont;
        double suffixSize = hasValue ? UnitFontSize : StatusFontSize;
        Brush numberBrush = IsStale || !hasValue ? StaleBrush : ValueBrush;

        var unitFt = suffix.Length > 0 ? SvgText.Fit(suffix, suffixFont, FontWeights.Normal, suffixSize, 14, AvailableWidth * 0.62, hasValue ? UnitBrush : StatusBrush, ppd) : null;
        double unitW = unitFt is null ? 0 : Math.Min(unitFt.WidthIncludingTrailingWhitespace, unitFt.MaxTextWidth > 0 ? unitFt.MaxTextWidth : double.MaxValue);
        double numberMax = Math.Max(40, AvailableWidth - unitW - (unitFt is null ? 0 : Gap));
        var numFt = SvgText.Fit(number, DigitFont, FontWeights.Normal, ValueFontSize, 30, numberMax, numberBrush, ppd);
        double numW = numFt.WidthIncludingTrailingWhitespace;
        double total = numW + (unitFt is null ? 0 : Gap + unitW);

        double left = Anchor == TextAnchor.End ? X - total : X;
        dc.DrawText(numFt, new Point(left, Y - numFt.Baseline));
        if (unitFt != null)
            dc.DrawText(unitFt, new Point(left + numW + Gap, Y - unitFt.Baseline));
    }
}
