# TwoPartBadge Control

`TwoPartBadge` is a lightweight reusable UserControl that encapsulates the repeated two-segment badge pattern used throughout `MainPage.xaml` (e.g., `Type | LTS`, `Support | Active`). It reduces XAML duplication and centralizes styling.

## Features

* Separate label and value segments with individual background / foreground brushes.
* Corner radius customization for each segment.
* Simple value via `Value` or fully custom inline XAML via `ValueContent` (lets you add buttons/icons).
* Font size customization via `BadgeFontSize`.

## Dependency Properties

| Property | Type | Description |
|----------|------|-------------|
| `Label` | string | Text for the left segment. |
| `Value` | string | Plain text value (ignored if `ValueContent` provided). |
| `ValueContent` | object | Optional full content for value segment (e.g. StackPanel with text + info button). |
| `LabelBackground` / `ValueBackground` | Brush | Background brushes. |
| `LabelForeground` / `ValueForeground` | Brush | Text foreground brushes (defaults to white). |
| `LabelCornerRadius` / `ValueCornerRadius` | CornerRadius | Shape customization; defaults to `4,0,0,4` and `0`. |
| `BadgeFontSize` | double | Font size for text created from `Label` / `Value`. |

## Basic Usage

```xml
<controls:TwoPartBadge
  Label="Support"
  Value="Active"
  LabelBackground="{StaticResource Badge.Support.Label}"
  ValueBackground="{StaticResource Badge.Support.Value}" />
```

## With Custom ValueContent (adds info button)

```xml
<controls:TwoPartBadge
  Label="Type"
  LabelBackground="{StaticResource Badge.Release.Label}"
  ValueBackground="{StaticResource Badge.Release.Value}">
  <controls:TwoPartBadge.ValueContent>
    <StackPanel Orientation="Horizontal" Spacing="2">
      <TextBlock Text="LTS" />
      <Button Width="18" Height="18" Padding="0" Content="i" />
    </StackPanel>
  </controls:TwoPartBadge.ValueContent>
</controls:TwoPartBadge>
```

## Migration Notes

1. Replace the surrounding two-column `Grid` (label / value) with a single `TwoPartBadge`.
2. Move any inner TextBlock(s) and buttons that lived inside the value segment into `ValueContent`.
3. Assign background brushes that previously belonged to the two `Border` elements.
4. Adjust corner radii if the badge ends the row (e.g., closing segment should normally use `0,4,4,0`).

## Future Enhancements

* Potential single-segment `Badge` control for cases like `Reason`.
* Style resources to auto-pull `FontSize.Badge` and standard padding without needing explicit set.

---
Added in commit introducing initial refactor of Release Type & Support Phase badges.
