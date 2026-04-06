using System.Windows;
using System.Windows.Input;

namespace PARAFactoNative.Views;

public enum ActionChoiceResult
{
    Cancel,
    Primary,
    Secondary
}

public partial class ActionChoiceWindow : Window
{
    public ActionChoiceResult Choice { get; private set; } = ActionChoiceResult.Cancel;

    public ActionChoiceWindow(
        string title,
        string message,
        string primaryLabel,
        string secondaryLabel,
        string? cancelLabel = null)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        PrimaryButton.Content = primaryLabel;
        SecondaryButton.Content = secondaryLabel;
        if (string.IsNullOrWhiteSpace(cancelLabel))
        {
            CancelButton.Visibility = Visibility.Collapsed;
            CancelButton.IsCancel = false;
            // Échap sans 3e bouton : fermer comme annulation (évite doublon « Annuler » + 2e bouton déjà « Annuler »)
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.Escape) return;
                Choice = ActionChoiceResult.Cancel;
                DialogResult = false;
                e.Handled = true;
            };
        }
        else
        {
            CancelButton.Content = cancelLabel;
            CancelButton.Visibility = Visibility.Visible;
            CancelButton.IsCancel = true;
        }
    }

    private void Primary_OnClick(object sender, RoutedEventArgs e)
    {
        Choice = ActionChoiceResult.Primary;
        DialogResult = true;
    }

    private void Secondary_OnClick(object sender, RoutedEventArgs e)
    {
        Choice = ActionChoiceResult.Secondary;
        DialogResult = true;
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e)
    {
        Choice = ActionChoiceResult.Cancel;
        DialogResult = false;
    }
}
