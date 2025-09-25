using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace DotNetUninstall.Presentation.Converters;

public sealed class LifecycleStateToBrushConverter : IValueConverter
{
    public SolidColorBrush EolBrush { get; set; } = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x60, 0x1F, 0x1F));
    public SolidColorBrush ExpiringBrush { get; set; } = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x7F, 0x5A, 0x15));
    public SolidColorBrush SupportedBrush { get; set; } = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x1F, 0x3A, 0x52));
    public SolidColorBrush PreviewBrush { get; set; } = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x4B, 0x2F, 0x60));

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var state = value as string;
        return state switch
        {
            "eol" => EolBrush,
            "expiring" => ExpiringBrush,
            "supported" => SupportedBrush,
            _ => SupportedBrush
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
