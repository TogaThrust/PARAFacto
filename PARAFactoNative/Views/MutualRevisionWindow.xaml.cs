using System;
using System.Globalization;
using System.Windows;
using PARAFactoNative.Models;

namespace PARAFactoNative.Views;

public partial class MutualRevisionWindow : Window
{
    private sealed class VM
    {
        public string InvoiceLabel { get; init; } = "";
        public string NewTotalEuroText { get; set; } = "";
        public string Reason { get; set; } = "";
        public string ReferenceDoc { get; set; } = "";
    }

    private readonly VM _vm;

    public decimal NewTotalEuro { get; private set; }
    public string Reason { get; private set; } = "";
    public string ReferenceDoc { get; private set; } = "";

    public MutualRevisionWindow(Invoice invoice)
    {
        InitializeComponent();
        _vm = new VM
        {
            InvoiceLabel = $"{invoice.InvoiceNo} — {invoice.Recipient}",
            NewTotalEuroText = (invoice.TotalCents / 100m).ToString("0.00", CultureInfo.InvariantCulture)
        };
        DataContext = _vm;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!decimal.TryParse((_vm.NewTotalEuroText ?? "").Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var v) || v <= 0)
        {
            MessageBox.Show("Montant invalide.", "PARAFacto", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(_vm.Reason))
        {
            MessageBox.Show("La raison est obligatoire.", "PARAFacto", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(_vm.ReferenceDoc))
        {
            MessageBox.Show("La référence du document est obligatoire.", "PARAFacto", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        NewTotalEuro = v;
        Reason = _vm.Reason.Trim();
        ReferenceDoc = _vm.ReferenceDoc.Trim();
        DialogResult = true;
    }
}
