using System;
using System.Globalization;
using System.Windows;

namespace PARAFactoNative.Views;

public partial class CopyDayUntilDateWindow : Window
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-BE");
    private readonly DateTime _sourceDay;

    public DateTime EndDateInclusive { get; set; }
    public string SourceDayLabel { get; }

    public CopyDayUntilDateWindow(DateTime sourceDay)
    {
        _sourceDay = sourceDay.Date;
        EndDateInclusive = _sourceDay.AddDays(28);
        SourceDayLabel = $"Journée source : {_sourceDay.ToString("dddd d MMMM yyyy", Fr)}";
        DataContext = this;
        InitializeComponent();
    }

    private void Confirm_OnClick(object sender, RoutedEventArgs e)
    {
        if (EndDateInclusive.Date <= _sourceDay)
        {
            MessageBox.Show(
                "La date de fin doit être postérieure à la journée source.",
                "Agenda — copie journée",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
