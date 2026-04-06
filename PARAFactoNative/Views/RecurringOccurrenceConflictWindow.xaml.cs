using System.Windows;

namespace PARAFactoNative.Views;

public enum RecurringConflictDecision
{
    None,
    MoveRecurring,
    MoveExisting,
    Skip
}

public partial class RecurringOccurrenceConflictWindow : Window
{
    public RecurringConflictDecision Decision { get; private set; }

    /// <param name="moveBlockingActionCaption">Libellé du bouton pour déplacer le blocage (RDV, indispo ou lunch).</param>
    /// <param name="otherPartyLabel">Sous-titre bloc droit (ex. patient du RDV, ou « Indisponibilité »).</param>
    public RecurringOccurrenceConflictWindow(
        string dateTitle,
        string detailText,
        string phoneRecurringDisplay,
        string phoneOtherDisplay,
        string moveBlockingActionCaption,
        string? otherPartyLabel = null,
        string? otherPartySubLine = null)
    {
        InitializeComponent();
        BtnMoveBlocking.Content = moveBlockingActionCaption;
        TitleLine.Text = dateTitle;
        DetailLine.Text = detailText;
        PhoneRecurringLine.Text = string.IsNullOrWhiteSpace(phoneRecurringDisplay)
            ? "(téléphone non renseigné dans la fiche patient)"
            : phoneRecurringDisplay;

        if (!string.IsNullOrWhiteSpace(otherPartyLabel))
            PhoneOtherLabel.Text = otherPartyLabel;

        if (!string.IsNullOrWhiteSpace(otherPartySubLine))
        {
            PhoneOtherLine.Text = otherPartySubLine;
        }
        else
        {
            PhoneOtherLine.Text = string.IsNullOrWhiteSpace(phoneOtherDisplay)
                ? "(téléphone non renseigné dans la fiche patient)"
                : phoneOtherDisplay;
        }
    }

    private void BtnMoveRecurring_OnClick(object sender, RoutedEventArgs e)
    {
        Decision = RecurringConflictDecision.MoveRecurring;
        DialogResult = true;
    }

    private void BtnMoveBlocking_OnClick(object sender, RoutedEventArgs e)
    {
        Decision = RecurringConflictDecision.MoveExisting;
        DialogResult = true;
    }

    private void BtnSkip_OnClick(object sender, RoutedEventArgs e)
    {
        Decision = RecurringConflictDecision.Skip;
        DialogResult = true;
    }

    private void BtnClose_OnClick(object sender, RoutedEventArgs e)
    {
        Decision = RecurringConflictDecision.Skip;
        DialogResult = false;
    }
}
