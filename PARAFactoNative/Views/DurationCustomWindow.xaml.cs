using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace PARAFactoNative.Views;

public partial class DurationCustomWindow : Window
{
    public int TotalMinutes { get; private set; }

    /// <summary>0, 5, …, 55 (pas de 60 — plafond 55 minutes).</summary>
    private static readonly int[] MinuteSteps = Enumerable.Range(0, 12).Select(i => i * 5).ToArray();

    public DurationCustomWindow(int initialMinutes)
    {
        InitializeComponent();

        var hours = new List<int> { 0 };
        hours.AddRange(Enumerable.Range(1, 10));
        HoursCombo.ItemsSource = hours;
        MinutesCombo.ItemsSource = MinuteSteps;

        var safe = System.Math.Max(1, initialMinutes);
        var h = System.Math.Min(10, safe / 60);
        var mRem = safe % 60;
        var mPick = MinuteSteps.OrderBy(x => System.Math.Abs(x - mRem)).First();

        HoursCombo.SelectedItem = hours.Contains(h) ? h : 0;
        MinutesCombo.SelectedItem = mPick;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (HoursCombo.SelectedItem is not int h || MinutesCombo.SelectedItem is not int m)
        {
            MessageBox.Show("Sélectionnez les heures et les minutes.", "Durée", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var total = h * 60 + m;
        if (total <= 0)
        {
            MessageBox.Show("La durée doit être d’au moins 1 minute.", "Durée", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (total > 12 * 60)
        {
            MessageBox.Show("La durée ne peut pas dépasser 12 heures.", "Durée", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        TotalMinutes = total;
        DialogResult = true;
    }
}
