using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace DotNetUninstall.Presentation.Controls;

public sealed partial class TwoPartBadge : UserControl
{
    public TwoPartBadge()
    {
        InitializeComponent();
        // Default content for value if plain text provided via Value property
        Loaded += (_, _) => EnsureValueContent();
    }

    private void EnsureValueContent()
    {
        if (ValueContent is null && !string.IsNullOrEmpty(Value))
        {
            // Provide a TextBlock inside a horizontal stack so callers can optionally inject a button etc. later via ValueContent
            ValueContent = new TextBlock
            {
                Text = Value,
                FontSize = BadgeFontSize,
                Foreground = ValueForeground
            };
        }
    }

    #region Label / Value text
    public string? Label
    {
        get => (string?)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(TwoPartBadge), new PropertyMetadata(default(string)));

    public string? Value
    {
        get => (string?)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(string), typeof(TwoPartBadge), new PropertyMetadata(default(string), OnValueChanged));

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TwoPartBadge badge)
        {
            // Reset value content if simple value used
            if (badge.ValueContent is null && e.NewValue is string s && !string.IsNullOrEmpty(s))
            {
                badge.ValueContent = new TextBlock
                {
                    Text = s,
                    FontSize = badge.BadgeFontSize,
                    Foreground = badge.ValueForeground
                };
            }
        }
    }
    #endregion

    #region Info Button
    public object? InfoTag
    {
        get => GetValue(InfoTagProperty);
        set => SetValue(InfoTagProperty, value);
    }
    public static readonly DependencyProperty InfoTagProperty =
        DependencyProperty.Register(nameof(InfoTag), typeof(object), typeof(TwoPartBadge), new PropertyMetadata(null, OnInfoChanged));

    public string? InfoToolTip
    {
        get => (string?)GetValue(InfoToolTipProperty);
        set => SetValue(InfoToolTipProperty, value);
    }
    public static readonly DependencyProperty InfoToolTipProperty =
        DependencyProperty.Register(nameof(InfoToolTip), typeof(string), typeof(TwoPartBadge), new PropertyMetadata(null, OnInfoChanged));

    public string InfoGlyph
    {
        get => (string)GetValue(InfoGlyphProperty);
        set => SetValue(InfoGlyphProperty, value);
    }
    public static readonly DependencyProperty InfoGlyphProperty =
        DependencyProperty.Register(nameof(InfoGlyph), typeof(string), typeof(TwoPartBadge), new PropertyMetadata("i", OnInfoChanged));

    public double InfoGlyphFontSize
    {
        get => (double)GetValue(InfoGlyphFontSizeProperty);
        set => SetValue(InfoGlyphFontSizeProperty, value);
    }
    public static readonly DependencyProperty InfoGlyphFontSizeProperty =
        DependencyProperty.Register(nameof(InfoGlyphFontSize), typeof(double), typeof(TwoPartBadge), new PropertyMetadata(10d, OnInfoChanged));

    public Brush InfoGlyphForeground
    {
        get => (Brush)GetValue(InfoGlyphForegroundProperty);
        set => SetValue(InfoGlyphForegroundProperty, value);
    }
    public static readonly DependencyProperty InfoGlyphForegroundProperty =
        DependencyProperty.Register(nameof(InfoGlyphForeground), typeof(Brush), typeof(TwoPartBadge), new PropertyMetadata(new SolidColorBrush(Microsoft.UI.Colors.White), OnInfoChanged));

    // Internal visibility DP consumed by XAML (auto-calculated)
    public Visibility InternalInfoVisibility
    {
        get => (Visibility)GetValue(InternalInfoVisibilityProperty);
        private set => SetValue(InternalInfoVisibilityProperty, value);
    }
    public static readonly DependencyProperty InternalInfoVisibilityProperty =
        DependencyProperty.Register(nameof(InternalInfoVisibility), typeof(Visibility), typeof(TwoPartBadge), new PropertyMetadata(Visibility.Collapsed));

    private static void OnInfoChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TwoPartBadge badge)
        {
            badge.UpdateInfoVisibility();
        }
    }

    private void UpdateInfoVisibility()
    {
        bool hasTag = InfoTag is not null && (InfoTag is not string s || !string.IsNullOrWhiteSpace(s));
        bool hasTooltip = !string.IsNullOrWhiteSpace(InfoToolTip);
        InternalInfoVisibility = (hasTag || hasTooltip) ? Visibility.Visible : Visibility.Collapsed;
    }
    #endregion

    #region Appearance Brushes
    public Brush? LabelBackground
    {
        get => (Brush?)GetValue(LabelBackgroundProperty);
        set => SetValue(LabelBackgroundProperty, value);
    }
    public static readonly DependencyProperty LabelBackgroundProperty =
        DependencyProperty.Register(nameof(LabelBackground), typeof(Brush), typeof(TwoPartBadge), new PropertyMetadata(null));

    public Brush? ValueBackground
    {
        get => (Brush?)GetValue(ValueBackgroundProperty);
        set => SetValue(ValueBackgroundProperty, value);
    }
    public static readonly DependencyProperty ValueBackgroundProperty =
        DependencyProperty.Register(nameof(ValueBackground), typeof(Brush), typeof(TwoPartBadge), new PropertyMetadata(null));

    public Brush LabelForeground
    {
        get => (Brush)GetValue(LabelForegroundProperty);
        set => SetValue(LabelForegroundProperty, value);
    }
    public static readonly DependencyProperty LabelForegroundProperty =
        DependencyProperty.Register(nameof(LabelForeground), typeof(Brush), typeof(TwoPartBadge), new PropertyMetadata(new SolidColorBrush(Microsoft.UI.Colors.White)));

    public Brush ValueForeground
    {
        get => (Brush)GetValue(ValueForegroundProperty);
        set => SetValue(ValueForegroundProperty, value);
    }
    public static readonly DependencyProperty ValueForegroundProperty =
        DependencyProperty.Register(nameof(ValueForeground), typeof(Brush), typeof(TwoPartBadge), new PropertyMetadata(new SolidColorBrush(Microsoft.UI.Colors.White)));
    #endregion

    #region CornerRadius
    public CornerRadius LabelCornerRadius
    {
        get => (CornerRadius)GetValue(LabelCornerRadiusProperty);
        set => SetValue(LabelCornerRadiusProperty, value);
    }
    public static readonly DependencyProperty LabelCornerRadiusProperty =
        DependencyProperty.Register(nameof(LabelCornerRadius), typeof(CornerRadius), typeof(TwoPartBadge), new PropertyMetadata(new CornerRadius(4,0,0,4)));

    public CornerRadius ValueCornerRadius
    {
        get => (CornerRadius)GetValue(ValueCornerRadiusProperty);
        set => SetValue(ValueCornerRadiusProperty, value);
    }
    public static readonly DependencyProperty ValueCornerRadiusProperty =
        DependencyProperty.Register(nameof(ValueCornerRadius), typeof(CornerRadius), typeof(TwoPartBadge), new PropertyMetadata(new CornerRadius(0,4,4,0)));
    #endregion

    #region FontSize
    public double BadgeFontSize
    {
        get => (double)GetValue(BadgeFontSizeProperty);
        set => SetValue(BadgeFontSizeProperty, value);
    }
    public static readonly DependencyProperty BadgeFontSizeProperty =
        DependencyProperty.Register(nameof(BadgeFontSize), typeof(double), typeof(TwoPartBadge), new PropertyMetadata(11d));
    #endregion

    #region ValueContent
    public object? ValueContent
    {
        get => GetValue(ValueContentProperty);
        set => SetValue(ValueContentProperty, value);
    }
    public static readonly DependencyProperty ValueContentProperty =
        DependencyProperty.Register(nameof(ValueContent), typeof(object), typeof(TwoPartBadge), new PropertyMetadata(null));
    #endregion
}
