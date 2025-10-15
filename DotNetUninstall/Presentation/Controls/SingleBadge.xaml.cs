using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DotNetUninstall.Presentation.Controls;

public sealed partial class SingleBadge : UserControl
{
    public SingleBadge()
    {
        InitializeComponent();
        Loaded += (_, _) => EnsureContent();
    }

    private void EnsureContent()
    {
        if (Content == null && !string.IsNullOrEmpty(Text))
        {
            Content = new TextBlock
            {
                Text = Text,
                FontSize = FontSize,
                Foreground = Foreground,
                VerticalAlignment = VerticalAlignment.Center
            };
        }
    }

    public string? Text
    {
        get => (string?)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(SingleBadge), new PropertyMetadata(null, OnTextChanged));

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SingleBadge b && b.Content == null && e.NewValue is string s && !string.IsNullOrEmpty(s))
        {
            b.Content = new TextBlock
            {
                Text = s,
                FontSize = b.FontSize,
                Foreground = b.Foreground,
                VerticalAlignment = VerticalAlignment.Center
            };
        }
    }
}
