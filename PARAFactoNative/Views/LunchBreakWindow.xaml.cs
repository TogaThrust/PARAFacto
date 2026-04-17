using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using PARAFactoNative.Services;

namespace PARAFactoNative.Views;

public partial class LunchBreakWindow : Window
{
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
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var sa = (StartCombo.Text ?? "").Trim();
        var sb = (EndCombo.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(sa) || string.IsNullOrWhiteSpace(sb))
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
