using System;
using Microsoft.UI.Xaml.Data;
using DotNetUninstall.Models;

namespace DotNetUninstall.Presentation.Converters;

// Converts SecurityStatus enum to "Yes" / "No" string.
public sealed class SecurityStateYesNoConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is SecurityStatus status)
        {
            return status is SecurityStatus.SecurityPatch or SecurityStatus.Patched ? "Yes" : "No";
        }
        return "No";
    }
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
