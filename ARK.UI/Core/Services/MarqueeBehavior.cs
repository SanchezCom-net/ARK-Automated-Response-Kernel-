using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using InputMouseEventArgs = System.Windows.Input.MouseEventArgs;
using System.Windows.Media;
using MediaBrushes = System.Windows.Media.Brushes;
using System.Windows.Media.Animation;

namespace ARK.UI.Core.Services;

public static class MarqueeBehavior
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled", typeof(bool), typeof(MarqueeBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        if ((bool)e.NewValue)
        {
            tb.MouseEnter += OnMouseEnter;
            tb.MouseLeave += OnMouseLeave;
        }
        else
        {
            tb.MouseEnter -= OnMouseEnter;
            tb.MouseLeave -= OnMouseLeave;
        }
    }

    private static void OnMouseEnter(object sender, InputMouseEventArgs e)
    {
        if (sender is not TextBlock tb
            || string.IsNullOrEmpty(tb.Text)
            || tb.ActualWidth < 1) return;

        var ft = new FormattedText(
            tb.Text,
            CultureInfo.CurrentCulture,
            tb.FlowDirection,
            new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch),
            tb.FontSize,
            MediaBrushes.Black,
            VisualTreeHelper.GetDpi(tb).PixelsPerDip);

        var overflow = ft.Width - tb.ActualWidth;
        if (overflow <= 0) return;

        var transform = new TranslateTransform(0d, 0d);
        tb.RenderTransform = transform;

        var seconds = Math.Max(2.0, overflow / 60.0);
        transform.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(0d, -overflow, new Duration(TimeSpan.FromSeconds(seconds)))
            {
                RepeatBehavior = RepeatBehavior.Forever,
                AutoReverse    = true,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            });
    }

    private static void OnMouseLeave(object sender, InputMouseEventArgs e)
    {
        if (sender is not TextBlock tb
            || tb.RenderTransform is not TranslateTransform transform) return;

        transform.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(0d, new Duration(TimeSpan.FromSeconds(0.25)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }
}
