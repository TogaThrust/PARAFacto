using System;
using System.Windows;
using System.Windows.Controls;

namespace PARAFactoNative.Views;

public static class PasswordBoxAssistant
{
    public static readonly DependencyProperty BoundPasswordProperty =
        DependencyProperty.RegisterAttached(
            "BoundPassword",
            typeof(string),
            typeof(PasswordBoxAssistant),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnBoundPasswordChanged));

    public static readonly DependencyProperty BindPasswordProperty =
        DependencyProperty.RegisterAttached(
            "BindPassword",
            typeof(bool),
            typeof(PasswordBoxAssistant),
            new PropertyMetadata(false, OnBindPasswordChanged));

    private static readonly DependencyProperty IsUpdatingProperty =
        DependencyProperty.RegisterAttached(
            "IsUpdating",
            typeof(bool),
            typeof(PasswordBoxAssistant),
            new PropertyMetadata(false));

    public static void SetBindPassword(DependencyObject dp, bool value) => dp.SetValue(BindPasswordProperty, value);
    public static bool GetBindPassword(DependencyObject dp) => (bool)dp.GetValue(BindPasswordProperty);

    public static void SetBoundPassword(DependencyObject dp, string value) => dp.SetValue(BoundPasswordProperty, value);
    public static string GetBoundPassword(DependencyObject dp) => (string)dp.GetValue(BoundPasswordProperty);

    private static void OnBindPasswordChanged(DependencyObject dp, DependencyPropertyChangedEventArgs e)
    {
        if (dp is not PasswordBox pb) return;

        if ((bool)e.OldValue)
            pb.PasswordChanged -= HandlePasswordChanged;

        if ((bool)e.NewValue)
            pb.PasswordChanged += HandlePasswordChanged;
    }

    private static void OnBoundPasswordChanged(DependencyObject dp, DependencyPropertyChangedEventArgs e)
    {
        if (dp is not PasswordBox pb) return;
        if ((bool)pb.GetValue(IsUpdatingProperty)) return;

        pb.SetValue(IsUpdatingProperty, true);
        try
        {
            pb.Password = (e.NewValue as string) ?? "";
        }
        finally
        {
            pb.SetValue(IsUpdatingProperty, false);
        }
    }

    private static void HandlePasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not PasswordBox pb) return;

        pb.SetValue(IsUpdatingProperty, true);
        try
        {
            SetBoundPassword(pb, pb.Password);
        }
        finally
        {
            pb.SetValue(IsUpdatingProperty, false);
        }
    }
}

