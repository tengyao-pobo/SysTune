using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace SysTune.App.Controls;

/// <summary>true → Visible, false/null → Collapsed. Parameter "invert" flips it.</summary>
public sealed class BoolToVisibility : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var b = value is true || (value is not null and not bool && value is not string { Length: 0 });
        if (parameter as string == "invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Usage percentage → brush: normal accent, warning ≥ 85 %, danger ≥ 95 %.</summary>
public sealed class UsageToBrush : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var v = value is double d ? d : 0;
        var key = v >= 95 ? "Brush.Danger" : v >= 85 ? "Brush.Warn" : parameter as string ?? "Brush.Disk";
        return Application.Current.TryFindResource(key) as Brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Compares an enum value with the converter parameter (for segmented RadioButtons).</summary>
public sealed class EnumEquals : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not null && parameter is string s && string.Equals(value.ToString(), s, StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true && parameter is string s
            ? targetType == typeof(string) ? s : Enum.Parse(targetType, s)
            : Binding.DoNothing;
}
