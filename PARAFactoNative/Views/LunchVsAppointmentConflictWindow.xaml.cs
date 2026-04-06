using System.Windows;

namespace PARAFactoNative.Views;

public partial class LunchVsAppointmentConflictWindow : Window
{
    public enum ConflictChoice
    {
        None,
        ModifyLunch,
        MoveAppointment
    }

    public ConflictChoice Choice { get; private set; } = ConflictChoice.None;

    /// <summary>Conflit lunch (textes par défaut).</summary>
    public LunchVsAppointmentConflictWindow(string detailLine)
        : this(
            detailLine,
            "Un créneau empiète maintenant sur votre temps de lunch. Voulez-vous changer votre temps de lunch ou déplacer le rendez-vous ?",
            "Modifier le lunch",
            "Déplacer le rendez-vous",
            "Agenda — conflit lunch / rendez-vous")
    {
    }

    public LunchVsAppointmentConflictWindow(
        string detailLine,
        string mainMessage,
        string modifyIntervalButtonText,
        string moveAppointmentButtonText,
        string windowTitle)
    {
        InitializeComponent();
        Title = windowTitle;
        MessageText.Text = mainMessage;
        if (!string.IsNullOrWhiteSpace(detailLine))
            MessageText.Text += "\n\n" + detailLine.Trim();
        LunchBtn.Content = modifyIntervalButtonText;
        RdvBtn.Content = moveAppointmentButtonText;
    }

    private void LunchBtn_Click(object sender, RoutedEventArgs e)
    {
        Choice = ConflictChoice.ModifyLunch;
        DialogResult = true;
    }

    private void RdvBtn_Click(object sender, RoutedEventArgs e)
    {
        Choice = ConflictChoice.MoveAppointment;
        DialogResult = true;
    }
}
