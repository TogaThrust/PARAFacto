using System.Globalization;
using System.Windows;
using PARAFactoNative.Models;

namespace PARAFactoNative.Views;

public partial class TarifEditWindow : Window
{
    public Tarif? Result { get; private set; }

    public TarifEditWindow(Tarif? existing = null)
    {
        InitializeComponent();
        if (existing != null)
        {
            Title = "Modifier le tarif";
            TitleText.Text = "Modifier le tarif";
            LabelBox.Text = existing.Label ?? "";
            PartPatientBox.Text = (existing.PartPatientCents / 100m).ToString("0.00", CultureInfo.InvariantCulture);
            PartMutuelleBox.Text = (existing.PartMutuelleCents / 100m).ToString("0.00", CultureInfo.InvariantCulture);
            ActiveCheck.IsChecked = existing.Active;
        }
        else
        {
            PartPatientBox.Text = "0.00";
            PartMutuelleBox.Text = "0.00";
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var label = (LabelBox.Text ?? "").Trim();
        if (string.IsNullOrEmpty(label))
        {
            MessageBox.Show("Le libellé est obligatoire.", "PARAFacto", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!decimal.TryParse((PartPatientBox.Text ?? "").Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var pp))
        {
            MessageBox.Show("Part patient : montant invalide.", "PARAFacto", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!decimal.TryParse((PartMutuelleBox.Text ?? "").Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var pm))
        {
            MessageBox.Show("Part mutuelle : montant invalide.", "PARAFacto", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (pp < 0 || pm < 0)
        {
            MessageBox.Show("Les montants ne peuvent pas être négatifs.", "PARAFacto", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Result = new Tarif
        {
            Label = label,
            PartPatientCents = (int)Math.Round(pp * 100m, MidpointRounding.AwayFromZero),
            PartMutuelleCents = (int)Math.Round(pm * 100m, MidpointRounding.AwayFromZero),
            Active = ActiveCheck.IsChecked == true
        };
        DialogResult = true;
    }
}
