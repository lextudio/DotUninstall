using System;
using DotNetUninstall.Models;
using Microsoft.UI.Xaml.Data;

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
