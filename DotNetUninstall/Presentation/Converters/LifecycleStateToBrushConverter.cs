using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace DotNetUninstall.Presentation.Converters;

public sealed class LifecycleStateToBrushConverter : IValueConverter
{
    public SolidColorBrush EolBrush { get; set; } = Fetch("Brush.Lifecycle.Eol", 0x60, 0x1F, 0x1F);
    public SolidColorBrush ExpiringBrush { get; set; } = Fetch("Brush.Lifecycle.Expiring", 0x7F, 0x5A, 0x15);
    public SolidColorBrush SupportedBrush { get; set; } = Fetch("Brush.Lifecycle.Supported", 0x1F, 0x3A, 0x52);
    public SolidColorBrush PreviewBrush { get; set; } = Fetch("Brush.Lifecycle.Preview", 0x4B, 0x2F, 0x60);

    private static SolidColorBrush Fetch(string key, byte r, byte g, byte b)
    {
        var res = Microsoft.UI.Xaml.Application.Current?.Resources;
        if (res != null && res.TryGetValue(key, out var obj) && obj is SolidColorBrush scb)
            return scb;
        return new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, r, g, b));
    }

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
