using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace DotNetUninstall.Presentation.Converters;

public sealed class StringEqualsToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var target = parameter as string;
        bool match = value is string s && !string.IsNullOrWhiteSpace(target) && string.Equals(s, target, StringComparison.OrdinalIgnoreCase);
        if (Invert) match = !match;
        return match ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
