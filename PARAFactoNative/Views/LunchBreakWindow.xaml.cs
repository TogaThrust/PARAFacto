using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using PARAFactoNative.Services;

namespace PARAFactoNative.Views;

public partial class LunchBreakWindow : Window
{
    private const int DefaultLunchDurationMinutes = 30;

    public int StartTotalMinutes { get; private set; }
    public int EndTotalMinutes { get; private set; }

    public LunchBreakWindow(string? initialStartHhMm, string? initialEndHhMm)
    {
        InitializeComponent();
        var times = Enumerable.Range(0, 96)
            .Select(i => AppointmentScheduling.FormatMinutesAsHhMm(i * 15))
            .ToList();
        StartCombo.ItemsSource = times;
        EndCombo.ItemsSource = times;

        var defStart = string.IsNullOrWhiteSpace(initialStartHhMm) ? "12:00" : initialStartHhMm.Trim();
        var defEnd = string.IsNullOrWhiteSpace(initialEndHhMm) ? "13:00" : initialEndHhMm.Trim();
        StartCombo.SelectedItem = times.Contains(defStart) ? defStart : "12:00";
        EndCombo.SelectedItem = times.Contains(defEnd) ? defEnd : "13:00";

        // Quand le début change, proposer automatiquement une fin à +30 min (modifiable ensuite).
        StartCombo.SelectionChanged += StartCombo_OnSelectionChanged;
    }

    private void StartCombo_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (StartCombo.SelectedItem is not string startHhMm) return;
        if (!AppointmentScheduling.TryParseTimeToMinutes(startHhMm, out var startMin)) return;

        var endMin = startMin + DefaultLunchDurationMinutes;
        if (endMin >= 24 * 60) endMin = (24 * 60) - 15;

        var endHhMm = AppointmentScheduling.FormatMinutesAsHhMm(endMin);
        if (EndCombo.ItemsSource is not System.Collections.Generic.IEnumerable<string> choices) return;
        if (choices.Contains(endHhMm))
            EndCombo.SelectedItem = endHhMm;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (StartCombo.SelectedItem is not string sa || EndCombo.SelectedItem is not string sb)
        {
            MessageBox.Show("Choisissez l’heure de début et de fin.", "Lunch", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TimeSpan.TryParse(sa.Trim(), CultureInfo.InvariantCulture, out var tsA)
            || !TimeSpan.TryParse(sb.Trim(), CultureInfo.InvariantCulture, out var tsB))
        {
            MessageBox.Show("Heures invalides.", "Lunch", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var a = (int)tsA.TotalMinutes;
        var b = (int)tsB.TotalMinutes;
        if (b <= a)
        {
            MessageBox.Show("L’heure de fin doit être après l’heure de début.", "Lunch", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        StartTotalMinutes = a;
        EndTotalMinutes = b;
        DialogResult = true;
    }
}
