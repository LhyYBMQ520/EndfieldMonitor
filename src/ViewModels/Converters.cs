using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace EndfieldMonitor.ViewModels;

/// <summary>RadioButton 组与整数属性的双向绑定。</summary>
public sealed class IntEqualsConverter : IValueConverter
{
    public static readonly IntEqualsConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int v && int.TryParse(parameter?.ToString(), out int p) && v == p;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true && int.TryParse(parameter?.ToString(), out int p) ? p : Binding.DoNothing;
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public static readonly InverseBoolToVisibilityConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
