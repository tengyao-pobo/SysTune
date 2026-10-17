using System.Globalization;
using System.Windows;
using System.Windows.Data;

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

/// <summary>Boolean negation.</summary>
public sealed class InverseBool : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
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
