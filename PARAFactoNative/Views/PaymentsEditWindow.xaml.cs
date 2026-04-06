using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using PARAFactoNative.Models;
using PARAFactoNative.Services;

namespace PARAFactoNative.Views;

public partial class PaymentsEditWindow : Window
{
    public sealed class PaymentEditRow
    {
        public long Id { get; set; }
        public string PaidDateIso { get; set; } = "";
        public string AmountEuroText { get; set; } = "";
        public string Method { get; set; } = "Espèces";
        public string Reference { get; set; } = "";
    }

    private sealed class VM
    {
        public string InvoiceLabel { get; init; } = "";
        public ObservableCollection<PaymentEditRow> Rows { get; } = new();
        public string[] Methods { get; } = { "Espèces", "Virement", "Carte", "Autre" };
    }

    private readonly VM _vm;

    public ObservableCollection<PaymentEditRow> Rows => _vm.Rows;
    public string[] Methods => _vm.Methods;

    public PaymentsEditWindow(Invoice invoice, decimal defaultEuro, PaymentEditRow[] existingRows)
    {
        InitializeComponent();

        _vm = new VM
        {
            InvoiceLabel = $"{invoice.InvoiceNo} — {invoice.Recipient}"
        };

        if (existingRows.Length > 0)
        {
            foreach (var r in existingRows)
                _vm.Rows.Add(r);
        }
        else
        {
            _vm.Rows.Add(new PaymentEditRow
            {
                Id = 0,
                PaidDateIso = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                AmountEuroText = defaultEuro.ToString("0.00", CultureInfo.InvariantCulture),
                Method = "Espèces",
                Reference = ""
            });
        }

        DataContext = _vm;
    }

    private void AddRow_Click(object sender, RoutedEventArgs e)
    {
        _vm.Rows.Add(new PaymentEditRow
        {
            Id = 0,
            PaidDateIso = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            AmountEuroText = "0.00",
            Method = "Espèces",
            Reference = ""
        });
        Grid.SelectedIndex = _vm.Rows.Count - 1;
        Grid.ScrollIntoView(Grid.SelectedItem);
    }

    private void RemoveRow_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is not PaymentEditRow row) return;
        _vm.Rows.Remove(row);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // Validation
        foreach (var r in _vm.Rows)
        {
            var date = (r.PaidDateIso ?? "").Trim();
            if (date.Length < 10 || !DateTime.TryParseExact(date.Substring(0, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            {
                MessageBox.Show("Date invalide. Utilisez le format YYYY-MM-DD.", "PARAFacto", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var amtText = (r.AmountEuroText ?? "").Trim().Replace(',', '.');
            if (!decimal.TryParse(amtText, NumberStyles.Number, CultureInfo.InvariantCulture, out var amt) || amt <= 0)
            {
                MessageBox.Show("Montant invalide (doit être > 0).", "PARAFacto", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var method = (r.Method ?? "").Trim();
            if (method.Length == 0) r.Method = "Autre";
        }

        if (_vm.Rows.Count == 0)
        {
            var ask = ChoiceDialog.AskYesNo(
                "PARAFacto",
                "Aucun paiement ne sera enregistré pour cette facture.\n\nConfirmer la suppression de tous les paiements ?\n\n" +
                "Cette action supprimera l'historique de paiement de cette facture.",
                "Supprimer tous les paiements",
                "Annuler",
                this);
            if (!ask) return;
        }

        // Trier par date pour un historique cohérent
        var ordered = _vm.Rows.OrderBy(r => (r.PaidDateIso ?? "")).ToList();
        _vm.Rows.Clear();
        foreach (var r in ordered) _vm.Rows.Add(r);

        DialogResult = true;
    }
}

