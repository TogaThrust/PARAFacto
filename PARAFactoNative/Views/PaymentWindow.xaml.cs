using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using PARAFactoNative.Models;

namespace PARAFactoNative.Views;

public partial class PaymentWindow : Window
{
    private sealed class VM
    {
        public string InvoiceLabel { get; init; } = "";
        public string AmountEuroText { get; set; } = "";
        public List<string> Methods { get; } = new() { "Espèces", "Virement", "Carte", "Autre" };
        public string SelectedMethod { get; set; } = "Virement";
        public string Reference { get; set; } = "";
    }

    private readonly VM _vm;

    public decimal AmountEuro { get; private set; }
    public string Method { get; private set; } = "Virement";
    public string Reference { get; private set; } = "";

    public PaymentWindow(Invoice invoice, decimal defaultEuro)
    {
        InitializeComponent();
        _vm = new VM
        {
            InvoiceLabel = $"{invoice.InvoiceNo} — {invoice.Recipient}",
            AmountEuroText = defaultEuro.ToString("0.00", CultureInfo.InvariantCulture),
            SelectedMethod = "Virement",
            Reference = ""
        };
        DataContext = _vm;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!decimal.TryParse(_vm.AmountEuroText.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var v))
        {
            MessageBox.Show("Montant invalide.", "PARAFacto", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (v <= 0)
        {
            MessageBox.Show("Le montant doit être > 0.", "PARAFacto", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        AmountEuro = v;
        Method = (_vm.SelectedMethod ?? "Virement").Trim();
        Reference = (_vm.Reference ?? "").Trim();
        DialogResult = true;
    }
}
