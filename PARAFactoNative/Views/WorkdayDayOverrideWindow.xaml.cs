using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using PARAFactoNative.Services;

namespace PARAFactoNative.Views;

public partial class WorkdayDayOverrideWindow : Window
{
    public string? SavedStartHhMm { get; private set; }
    public string? SavedEndHhMm { get; private set; }

    private readonly IReadOnlyList<string> _quarterHours;

    public WorkdayDayOverrideWindow(
        DateTime day,
        IReadOnlyList<string> quarterHourChoices,
        int initialStartMin,
        int initialEndMin,
        CultureInfo culture)
    {
        InitializeComponent();
        _quarterHours = quarterHourChoices;
        DateLabel.Text = day.ToString("dddd d MMMM yyyy", culture);

        StartCombo.ItemsSource = _quarterHours;
        EndCombo.ItemsSource = _quarterHours;

        var startStr = SnapToQuarter(initialStartMin);
        var endStr = SnapToQuarter(initialEndMin);
        StartCombo.SelectedItem = _quarterHours.Contains(startStr) ? startStr : _quarterHours[36];
        EndCombo.SelectedItem = _quarterHours.Contains(endStr) ? endStr : _quarterHours[84];
        StartCombo.SelectionChanged += (_, _) => CoerceEndAfterStart();
        CoerceEndAfterStart();
    }

    private static string SnapToQuarter(int minutesSinceMidnight)
    {
        var m = Math.Clamp((minutesSinceMidnight / 15) * 15, 0, 23 * 60 + 45);
        return AppointmentScheduling.FormatMinutesAsHhMm(m);
    }

    private void CoerceEndAfterStart()
    {
        if (StartCombo.SelectedItem is not string ss
            || EndCombo.SelectedItem is not string es
            || !TimeSpan.TryParse(ss.Trim(), CultureInfo.InvariantCulture, out var tsS)
            || !TimeSpan.TryParse(es.Trim(), CultureInfo.InvariantCulture, out var tsE))
            return;

        var sm = (int)tsS.TotalMinutes;
        var em = (int)tsE.TotalMinutes;
        if (em <= sm + 14)
        {
            var need = sm + 15;
            var pick = _quarterHours.FirstOrDefault(h =>
                TimeSpan.TryParse(h, CultureInfo.InvariantCulture, out var t) && (int)t.TotalMinutes >= need);
            if (pick is not null)
                EndCombo.SelectedItem = pick;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (StartCombo.SelectedItem is not string startStr || EndCombo.SelectedItem is not string endStr)
        {
            MessageBox.Show("Choisissez l’heure de début et de fin.", "Horaires du jour", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!AppointmentScheduling.TryParseTimeToMinutes(startStr.Trim(), out var newStart)
            || !AppointmentScheduling.TryParseTimeToMinutes(endStr.Trim(), out var newClose))
        {
            MessageBox.Show("Heures invalides.", "Horaires du jour", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (newStart >= newClose - 15)
        {
            MessageBox.Show(
                "L’heure de début de journée doit être au moins 15 minutes avant la fin de journée.",
                "Horaires du jour",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        SavedStartHhMm = AppointmentScheduling.FormatMinutesAsHhMm(newStart);
        SavedEndHhMm = AppointmentScheduling.FormatMinutesAsHhMm(newClose);
        DialogResult = true;
    }
}
