using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace NotchPeninsula.Converters;

/// <summary>
/// 判断绑定值是否等于 ConverterParameter，用于把 int 状态映射成 XAML 里的 class 伪类
/// （如 Classes.selected、Classes.active）。
/// </summary>
public class IndexEqualsConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null) return false;
        return string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
