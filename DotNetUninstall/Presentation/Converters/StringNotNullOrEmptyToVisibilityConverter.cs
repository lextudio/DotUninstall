using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace DotNetUninstall.Presentation.Converters;

public sealed class StringNotNullOrEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value is string s && !string.IsNullOrEmpty(s) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
