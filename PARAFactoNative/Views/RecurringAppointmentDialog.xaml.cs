using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using PARAFactoNative.Services;

namespace PARAFactoNative.Views;

public sealed class RecurrenceWizardResult
{
    public RecurrencePatternKind Kind { get; init; }
    public DayOfWeek? FixedWeekday { get; init; }
    public int? FixedDayOfMonth { get; init; }
    public bool LimitByEndDate { get; init; }
    public DateTime EndDateInclusive { get; init; }
    public int OccurrenceCount { get; init; }
}

public partial class RecurringAppointmentDialog : Window
{
    private readonly DateTime _anchorDate;
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-BE");

    private sealed class PatternPick
    {
        public string Label { get; init; } = "";
        public RecurrencePatternKind Kind { get; init; }
    }

    private sealed class WeekdayPick
    {
        public string Label { get; init; } = "";
        public DayOfWeek Dow { get; init; }
    }

    public RecurrenceWizardResult? Result { get; private set; }

    public RecurringAppointmentDialog(DateTime anchorDate)
    {
        InitializeComponent();
        _anchorDate = anchorDate.Date;

        EndByDateRadio.Checked += (_, _) => ApplyEndModeEnabled();
        EndByCountRadio.Checked += (_, _) => ApplyEndModeEnabled();

        PatternCombo.ItemsSource = new List<PatternPick>
        {
            new() { Label = "Tous les jours", Kind = RecurrencePatternKind.Daily },
            new() { Label = "Toutes les semaines (même jour de la semaine)", Kind = RecurrencePatternKind.WeeklySameWeekdayAsAnchor },
            new() { Label = "Tous les mois (même quantième, ex. le 15 de chaque mois)", Kind = RecurrencePatternKind.MonthlySameDayAsAnchor },
            new() { Label = "Tous les lundis, mardis, … (jour fixe)", Kind = RecurrencePatternKind.WeeklyOnFixedWeekday },
            new() { Label = "Tous les X du mois (ex. le 3 de chaque mois)", Kind = RecurrencePatternKind.MonthlyOnFixedDayOfMonth }
        };
        PatternCombo.SelectedIndex = 1;

        WeekdayCombo.ItemsSource = new List<WeekdayPick>
        {
            new() { Label = "lundi", Dow = DayOfWeek.Monday },
            new() { Label = "mardi", Dow = DayOfWeek.Tuesday },
            new() { Label = "mercredi", Dow = DayOfWeek.Wednesday },
            new() { Label = "jeudi", Dow = DayOfWeek.Thursday },
            new() { Label = "vendredi", Dow = DayOfWeek.Friday },
            new() { Label = "samedi", Dow = DayOfWeek.Saturday },
            new() { Label = "dimanche", Dow = DayOfWeek.Sunday }
        };
        var anchorDow = _anchorDate.DayOfWeek;
        WeekdayCombo.SelectedItem = ((List<WeekdayPick>)WeekdayCombo.ItemsSource).First(w => w.Dow == anchorDow);

        for (var d = 1; d <= 31; d++)
            DayOfMonthCombo.Items.Add(d);
        DayOfMonthCombo.SelectedItem = Math.Clamp(_anchorDate.Day, 1, 31);

        for (var c = 1; c <= 100; c++)
            CountCombo.Items.Add(c);
        CountCombo.SelectedItem = 10;

        EndDatePick.SelectedDate = _anchorDate.AddMonths(2);
        EndDatePick.Language = System.Windows.Markup.XmlLanguage.GetLanguage(Fr.Name);
        ApplyEndModeEnabled();
    }

    private void PatternCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PatternCombo.SelectedItem is not PatternPick p)
        {
            WeekdayPanel.Visibility = Visibility.Collapsed;
            DayOfMonthPanel.Visibility = Visibility.Collapsed;
            return;
        }

        WeekdayPanel.Visibility = p.Kind == RecurrencePatternKind.WeeklyOnFixedWeekday
            ? Visibility.Visible
            : Visibility.Collapsed;
        DayOfMonthPanel.Visibility = p.Kind == RecurrencePatternKind.MonthlyOnFixedDayOfMonth
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ApplyEndModeEnabled()
    {
        if (EndDatePick is null || CountCombo is null || EndByDateRadio is null || EndByCountRadio is null)
            return;
        EndDatePick.IsEnabled = EndByDateRadio.IsChecked == true;
        CountCombo.IsEnabled = EndByCountRadio.IsChecked == true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (PatternCombo.SelectedItem is not PatternPick pick)
        {
            MessageBox.Show("Choisissez un type de répétition.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DayOfWeek? fixedDow = null;
        int? fixedDom = null;
        if (pick.Kind == RecurrencePatternKind.WeeklyOnFixedWeekday)
        {
            if (WeekdayCombo.SelectedItem is not WeekdayPick wp)
            {
                MessageBox.Show("Choisissez un jour de la semaine.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            fixedDow = wp.Dow;
        }
        else if (pick.Kind == RecurrencePatternKind.MonthlyOnFixedDayOfMonth)
        {
            if (DayOfMonthCombo.SelectedItem is null)
            {
                MessageBox.Show("Choisissez le jour du mois.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            fixedDom = Convert.ToInt32(DayOfMonthCombo.SelectedItem);
        }

        var limitByDate = EndByDateRadio.IsChecked == true;
        DateTime endD = _anchorDate;
        var count = 10;
        if (limitByDate)
        {
            if (EndDatePick.SelectedDate is not { } ed)
            {
                MessageBox.Show("Indiquez la date de fin de la série.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            endD = ed.Date;
            if (endD < _anchorDate)
            {
                MessageBox.Show("La date de fin doit être au moins égale à la date du premier rendez-vous.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        else
        {
            if (CountCombo.SelectedItem is null)
            {
                MessageBox.Show("Choisissez le nombre de rendez-vous.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            count = Convert.ToInt32(CountCombo.SelectedItem);
        }

        Result = new RecurrenceWizardResult
        {
            Kind = pick.Kind,
            FixedWeekday = fixedDow,
            FixedDayOfMonth = fixedDom,
            LimitByEndDate = limitByDate,
            EndDateInclusive = endD,
            OccurrenceCount = count
        };
        DialogResult = true;
    }
}
