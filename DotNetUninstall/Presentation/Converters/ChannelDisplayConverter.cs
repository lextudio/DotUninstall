using System;
using Microsoft.UI.Xaml.Data;

namespace DotNetUninstall.Presentation.Converters;

public sealed class ChannelDisplayConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is string s)
        {
            // If the channel is exactly something like "9.0" or "10.0", collapse to major only.
            // Leave other forms (e.g., "9.0-preview", "9.0.1") untouched.
            if (System.Text.RegularExpressions.Regex.IsMatch(s, "^([0-9]+)\\.0$"))
            {
                var idx = s.IndexOf('.');
                return idx > 0 ? s.Substring(0, idx) : s;
            }
            return s;
        }
        return value ?? string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
