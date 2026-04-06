using System.Windows;

namespace PARAFactoNative.Views;

public enum RecurrenceDeleteScope
{
    Cancel,
    OnlyCurrent,
    CurrentAndFollowing
}

public partial class RecurrenceDeleteScopeWindow : Window
{
    public RecurrenceDeleteScope Scope { get; private set; } = RecurrenceDeleteScope.Cancel;

    public RecurrenceDeleteScopeWindow()
    {
        InitializeComponent();
    }

    private void DeleteOnly_OnClick(object sender, RoutedEventArgs e)
    {
        Scope = RecurrenceDeleteScope.OnlyCurrent;
        DialogResult = true;
    }

    private void DeleteFromHere_OnClick(object sender, RoutedEventArgs e)
    {
        Scope = RecurrenceDeleteScope.CurrentAndFollowing;
        DialogResult = true;
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e)
    {
        Scope = RecurrenceDeleteScope.Cancel;
        DialogResult = false;
    }
}
