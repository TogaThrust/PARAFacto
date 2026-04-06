using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using PARAFactoNative.Services;

namespace PARAFactoNative.Views;

public partial class UnavailabilityRescheduleWindow : Window
{
    public string StartHhMm { get; private set; } = "";
    public string EndHhMm { get; private set; } = "";
    public string? ReasonText { get; private set; }

    public UnavailabilityRescheduleWindow(DateTime day, UnavailabilityRow row, CultureInfo culture)
    {
        InitializeComponent();
        IntroLine.Text =
            $"Ajustez la plage d’indisponibilité du {day:dddd d MMMM yyyy} pour libérer le créneau de la récurrence.\n" +
            $"Plage actuelle : {NormalizeTime(row.StartTime)} – {NormalizeTime(row.EndTime)}.";

        ReasonBox.Text = row.Reason ?? "";

        var times = Enumerable.Range(0, 96)
            .Select(i => AppointmentScheduling.FormatMinutesAsHhMm(i * 15))
            .ToList();
        StartCombo.ItemsSource = times;

        var startNorm = NormalizeToQuarter(row.StartTime);
        StartCombo.SelectedItem = times.Contains(startNorm) ? startNorm : times[36];

        RebuildEndComboForStart();
        var endNorm = NormalizeToQuarter(row.EndTime);
        if (EndCombo.ItemsSource is System.Collections.IEnumerable en && en.Cast<string>().Contains(endNorm))
            EndCombo.SelectedItem = endNorm;
    }

    private static string NormalizeToQuarter(string t)
    {
        t = (t ?? "").Trim();
        if (TimeSpan.TryParse(t, CultureInfo.InvariantCulture, out var ts))
            return AppointmentScheduling.FormatMinutesAsHhMm((int)ts.TotalMinutes / 15 * 15);
        return "09:00";
    }

    private static string NormalizeTime(string t)
    {
        t = (t ?? "").Trim();
        if (TimeSpan.TryParse(t, CultureInfo.InvariantCulture, out var ts))
            return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}";
        return t;
    }

    private void RebuildEndComboForStart()
    {
        if (StartCombo.SelectedItem is not string ss
            || !TimeSpan.TryParse(ss.Trim(), CultureInfo.InvariantCulture, out var ts))
        {
            EndCombo.ItemsSource = Array.Empty<string>();
            return;
        }

        var startM = (int)ts.TotalMinutes;
        var ends = Enumerable.Range(0, 96)
            .Select(i => i * 15)
            .Where(m => m > startM)
            .Select(m => AppointmentScheduling.FormatMinutesAsHhMm(m))
            .ToList();
        EndCombo.ItemsSource = ends;
        if (EndCombo.SelectedItem is not string cur || !ends.Contains(cur))
            EndCombo.SelectedItem = ends.FirstOrDefault();
    }

    private void StartCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e) => RebuildEndComboForStart();

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (StartCombo.SelectedItem is not string startStr || EndCombo.SelectedItem is not string endStr)
        {
            MessageBox.Show("Choisissez l’heure de début et de fin.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TimeSpan.TryParse(startStr.Trim(), CultureInfo.InvariantCulture, out var tsStart)
            || !TimeSpan.TryParse(endStr.Trim(), CultureInfo.InvariantCulture, out var tsEnd))
        {
            MessageBox.Show("Heures invalides.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var startMin = (int)tsStart.TotalMinutes;
        var endMin = (int)tsEnd.TotalMinutes;
        if (endMin <= startMin)
        {
            MessageBox.Show("L’heure de fin doit être après l’heure de début.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        StartHhMm = AppointmentScheduling.FormatMinutesAsHhMm(startMin);
        EndHhMm = AppointmentScheduling.FormatMinutesAsHhMm(endMin);
        var r = ReasonBox.Text?.Trim();
        ReasonText = string.IsNullOrEmpty(r) ? null : r;
        DialogResult = true;
    }
}
