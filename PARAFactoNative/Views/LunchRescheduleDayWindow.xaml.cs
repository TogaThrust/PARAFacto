using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using PARAFactoNative.Services;

namespace PARAFactoNative.Views;

public partial class LunchRescheduleDayWindow : Window
{
    private static readonly int[] DurationChoices =
        { 15, 30, 45, 60, 75, 90, 105, 120, 135, 150, 165, 180 };

    public string StartHhMm { get; private set; } = "";
    public string EndHhMm { get; private set; } = "";

    /// <summary>Dernière heure / durée choisies (évite une sélection perdue par la ListBox si un seul panneau est manipulé).</summary>
    private string _lastStartSelection = "";

    private string _lastDurSelection = "";

    public LunchRescheduleDayWindow(DateTime day, int defaultStartMin, int defaultEndMin, CultureInfo culture)
    {
        InitializeComponent();
        DateLabel.Text = day.ToString("dddd d MMMM yyyy", culture);

        var times = Enumerable.Range(0, 96)
            .Select(i => AppointmentScheduling.FormatMinutesAsHhMm(i * 15))
            .ToList();
        StartList.ItemsSource = times;

        var durationStrs = DurationChoices.Select(d => d.ToString(CultureInfo.InvariantCulture)).ToList();
        DurationList.ItemsSource = durationStrs;

        var startStr = AppointmentScheduling.FormatMinutesAsHhMm(
            Math.Clamp((defaultStartMin / 15) * 15, 0, 23 * 60 + 45));
        StartList.SelectedItem = times.FirstOrDefault(t => t == startStr) ?? times.First(t => t == "12:00");

        var dur = Math.Max(15, defaultEndMin - defaultStartMin);
        var durPick = DurationChoices.OrderBy(x => Math.Abs(x - dur)).First();
        var durIdx = Array.IndexOf(DurationChoices, durPick);
        DurationList.SelectedItem = durationStrs[durIdx >= 0 ? durIdx : 0];

        _lastStartSelection = (string)StartList.SelectedItem!;
        _lastDurSelection = (string)DurationList.SelectedItem!;

        StartList.SelectionChanged += (_, _) =>
        {
            if (StartList.SelectedItem is string s)
                _lastStartSelection = s;
        };
        DurationList.SelectionChanged += (_, _) =>
        {
            if (DurationList.SelectedItem is string s)
                _lastDurSelection = s;
        };

        Loaded += (_, _) =>
        {
            if (StartList.SelectedItem != null)
                StartList.ScrollIntoView(StartList.SelectedItem);
            if (DurationList.SelectedItem != null)
                DurationList.ScrollIntoView(DurationList.SelectedItem);
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var sa = (StartList.SelectedItem as string) ?? _lastStartSelection;
        var sdStr = (DurationList.SelectedItem as string) ?? _lastDurSelection;
        if (string.IsNullOrWhiteSpace(sa) || string.IsNullOrWhiteSpace(sdStr))
        {
            MessageBox.Show(
                "Choisissez l’heure de début et la durée (une ligne dans chaque liste), puis Enregistrer.",
                "Lunch",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(sdStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var durationMin) || durationMin <= 0)
        {
            MessageBox.Show("Durée invalide.", "Lunch", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!AppointmentScheduling.TryParseTimeToMinutes(sa.Trim(), out var startM))
        {
            MessageBox.Show("Heure de début invalide.", "Lunch", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var endM = startM + durationMin;
        if (endM > 24 * 60)
        {
            MessageBox.Show("Cette plage dépasse minuit : raccourcissez la durée ou choisissez une heure plus tôt.", "Lunch",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (endM <= startM)
        {
            MessageBox.Show("La fin doit être après le début.", "Lunch", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        StartHhMm = AppointmentScheduling.FormatMinutesAsHhMm(startM);
        EndHhMm = AppointmentScheduling.FormatMinutesAsHhMm(endM);
        DialogResult = true;
    }
}
