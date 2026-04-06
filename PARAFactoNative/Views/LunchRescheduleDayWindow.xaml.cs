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

    public LunchRescheduleDayWindow(DateTime day, int defaultStartMin, int defaultEndMin, CultureInfo culture)
    {
        InitializeComponent();
        DateLabel.Text = day.ToString("dddd d MMMM yyyy", culture);

        var times = Enumerable.Range(0, 96)
            .Select(i => AppointmentScheduling.FormatMinutesAsHhMm(i * 15))
            .ToList();
        StartCombo.ItemsSource = times;
        DurationCombo.ItemsSource = DurationChoices.Select(d => d.ToString()).ToList();

        var startStr = AppointmentScheduling.FormatMinutesAsHhMm(
            Math.Clamp((defaultStartMin / 15) * 15, 0, 23 * 60 + 45));
        StartCombo.SelectedItem = times.Contains(startStr) ? startStr : "12:00";

        var dur = Math.Max(15, defaultEndMin - defaultStartMin);
        var durPick = DurationChoices.OrderBy(x => Math.Abs(x - dur)).First();
        DurationCombo.SelectedItem = durPick.ToString();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (StartCombo.SelectedItem is not string sa || DurationCombo.SelectedItem is not string sdStr)
        {
            MessageBox.Show("Choisissez l’heure de début et la durée.", "Lunch", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(sdStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var durationMin) || durationMin <= 0)
        {
            MessageBox.Show("Durée invalide.", "Lunch", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TimeSpan.TryParse(sa.Trim(), CultureInfo.InvariantCulture, out var tsA))
        {
            MessageBox.Show("Heure de début invalide.", "Lunch", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var startM = (int)tsA.TotalMinutes;
        var endM = startM + durationMin;
        if (endM > 24 * 60)
        {
            MessageBox.Show("Cette plage dépasse minuit : raccourcissez la durée ou choisissez une heure plus tôt.", "Lunch",
                MessageBoxButton.OK, MessageBoxImage.Warning);
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
