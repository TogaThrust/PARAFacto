using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace PARAFactoNative.Views;

public partial class InvoiceDateWindow : Window
{
    /// <summary>Date au format YYYY-MM-DD.</summary>
    public string InvoiceDateIso { get; private set; } = "";

    /// <summary>Période au format YYYY-MM (mois des séances).</summary>
    public string PeriodYYYYMM { get; private set; } = "";

    private sealed record MonthItem(int Month, string Label);

    public InvoiceDateWindow(string defaultPeriodYYYYMM, string periodLabel)
    {
        InitializeComponent();

        var defaultPeriod = ParsePeriodOrDefault(defaultPeriodYYYYMM);
        SeedMonthYearPickers(defaultPeriod.Year, defaultPeriod.Month);

        // Texte de contexte (facultatif) + fallback sur format de période
        PeriodTextBlock.Text = !string.IsNullOrEmpty(periodLabel)
            ? periodLabel
            : "Période des séances : " + FormatPeriod($"{defaultPeriod.Year:0000}-{defaultPeriod.Month:00}");

        ApplySelectedPeriodToState();
    }

    private static string FormatPeriod(string periodYYYYMM)
    {
        if (string.IsNullOrWhiteSpace(periodYYYYMM)) return "";
        var p = periodYYYYMM.Trim();
        int y, m;
        if (p.Length >= 7 && p[4] == '-')
        {
            if (!int.TryParse(p.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out y)
                || !int.TryParse(p.AsSpan(5, 2), NumberStyles.None, CultureInfo.InvariantCulture, out m))
                return p;
        }
        else if (p.Length >= 7 && p[2] == '-')
        {
            if (!int.TryParse(p.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out m)
                || !int.TryParse(p.AsSpan(3, 4), NumberStyles.None, CultureInfo.InvariantCulture, out y))
                return p;
        }
        else
            return p;
        var d = new DateTime(y, m, 1);
        return d.ToString("MMMM yyyy", CultureInfo.GetCultureInfo("fr-BE"));
    }

    private static DateTime GetFirstDayOfNextMonth(string periodYYYYMM)
    {
        if (string.IsNullOrWhiteSpace(periodYYYYMM))
            return new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(1);
        var p = periodYYYYMM.Trim();
        if (p.Length >= 7)
        {
            if (p[4] == '-') // YYYY-MM
            {
                if (int.TryParse(p.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var y)
                    && int.TryParse(p.AsSpan(5, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var m))
                    return new DateTime(y, m, 1).AddMonths(1);
            }
            else if (p[2] == '-') // MM-YYYY
            {
                if (int.TryParse(p.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var m)
                    && int.TryParse(p.AsSpan(3, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var y))
                    return new DateTime(y, m, 1).AddMonths(1);
            }
        }
        return new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(1);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        ApplySelectedPeriodToState();

        if (!InvoiceDatePicker.SelectedDate.HasValue)
        {
            MessageBox.Show("Veuillez sélectionner une date.", "PARAFacto", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        InvoiceDateIso = InvoiceDatePicker.SelectedDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        DialogResult = true;
        Close();
    }

    private void Period_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Quand l'utilisateur change la période, mettre à jour le texte et la date par défaut
        ApplySelectedPeriodToState(updateInvoiceDateDefault: true);
    }

    private void SeedMonthYearPickers(int year, int month)
    {
        var culture = CultureInfo.GetCultureInfo("fr-BE");
        var months = Enumerable.Range(1, 12)
            .Select(m => new MonthItem(m, new DateTime(2000, m, 1).ToString("MMMM", culture)))
            .ToList();

        MonthComboBox.ItemsSource = months;
        MonthComboBox.DisplayMemberPath = nameof(MonthItem.Label);

        // Années proposées : (année courante - 5) -> (année courante + 1)
        int currentYear = DateTime.Today.Year;
        var years = Enumerable.Range(currentYear - 5, 7).ToList();
        YearComboBox.ItemsSource = years;

        MonthComboBox.SelectedItem = months.FirstOrDefault(x => x.Month == month) ?? months.First();
        YearComboBox.SelectedItem = years.Contains(year) ? year : currentYear;
    }

    private void ApplySelectedPeriodToState(bool updateInvoiceDateDefault = false)
    {
        var monthItem = MonthComboBox.SelectedItem as MonthItem;
        var year = YearComboBox.SelectedItem as int? ?? DateTime.Today.Year;
        var month = monthItem?.Month ?? DateTime.Today.Month;

        PeriodYYYYMM = $"{year:0000}-{month:00}";

        var d = new DateTime(year, month, 1);
        var culture = CultureInfo.GetCultureInfo("fr-BE");
        PeriodTextBlock.Text = $"Période des séances : {d.ToString("MMMM yyyy", culture)}";

        if (updateInvoiceDateDefault || !InvoiceDatePicker.SelectedDate.HasValue)
            InvoiceDatePicker.SelectedDate = d.AddMonths(1); // par défaut : 1er jour du mois suivant la période
    }

    private static DateTime ParsePeriodOrDefault(string periodYYYYMM)
    {
        // Défaut demandé : mois précédent, année courante
        var fallback = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-1);
        if (string.IsNullOrWhiteSpace(periodYYYYMM)) return fallback;

        var p = periodYYYYMM.Trim();
        if (p.Length >= 7 && p[4] == '-')
        {
            if (int.TryParse(p.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var y)
                && int.TryParse(p.AsSpan(5, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var m)
                && m is >= 1 and <= 12)
                return new DateTime(y, m, 1);
        }
        return fallback;
    }
}
