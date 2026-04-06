using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PARAFactoNative.Views;

public static class TrimmedTextToolTipBehavior
{
    public static readonly DependencyProperty EnableForTrimmedTextProperty =
        DependencyProperty.RegisterAttached(
            "EnableForTrimmedText",
            typeof(bool),
            typeof(TrimmedTextToolTipBehavior),
            new PropertyMetadata(false, OnEnableForTrimmedTextChanged));

    public static bool GetEnableForTrimmedText(DependencyObject obj)
        => (bool)obj.GetValue(EnableForTrimmedTextProperty);

    public static void SetEnableForTrimmedText(DependencyObject obj, bool value)
        => obj.SetValue(EnableForTrimmedTextProperty, value);

    private static void OnEnableForTrimmedTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;

        if ((bool)e.NewValue)
        {
            tb.SizeChanged += OnTextBlockSizeChanged;
            tb.Loaded += OnTextBlockLoaded;
            tb.ToolTipOpening += OnTextBlockToolTipOpening;
        }
        else
        {
            tb.SizeChanged -= OnTextBlockSizeChanged;
            tb.Loaded -= OnTextBlockLoaded;
            tb.ToolTipOpening -= OnTextBlockToolTipOpening;
        }
    }

    private static void OnTextBlockLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBlock tb) UpdateToolTip(tb);
    }

    private static void OnTextBlockSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is TextBlock tb) UpdateToolTip(tb);
    }

    private static void OnTextBlockToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if (sender is not TextBlock tb) return;
        UpdateToolTip(tb);
        if (tb.ToolTip is null)
            e.Handled = true;
    }

    private static void UpdateToolTip(TextBlock tb)
    {
        var text = tb.Text ?? "";
        if (string.IsNullOrWhiteSpace(text))
        {
            tb.ToolTip = null;
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(tb).PixelsPerDip;
        var ft = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            tb.FlowDirection,
            new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch),
            tb.FontSize,
            Brushes.Black,
            dpi);

        var available = tb.ActualWidth - tb.Padding.Left - tb.Padding.Right;
        var isTrimmed = available > 0 && ft.Width > available;

        tb.ToolTip = isTrimmed ? text : null;
    }
}

