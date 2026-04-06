using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using PARAFactoNative.Services;

namespace PARAFactoNative.Views;

public partial class CreditNoteWindow : Window
{
    public sealed class Candidate
    {
        public long InvoiceId { get; init; }
        public string Label { get; init; } = "";
    }

    private sealed class VM
    {
        public List<Candidate> Candidates { get; }
        public Candidate? SelectedCandidate { get; set; }
        public string AmountEuroText { get; set; } = "";

        public VM(List<Candidate> candidates, long preselectedInvoiceId, decimal defaultAmountEuro)
        {
            Candidates = candidates;
            SelectedCandidate = preselectedInvoiceId > 0
                ? candidates.FirstOrDefault(c => c.InvoiceId == preselectedInvoiceId) ?? candidates.FirstOrDefault()
                : candidates.FirstOrDefault();
            AmountEuroText = defaultAmountEuro > 0
                ? defaultAmountEuro.ToString("0.00", CultureInfo.InvariantCulture)
                : "";
        }
    }

    private readonly VM _vm;

    public long SelectedInvoiceId { get; private set; }
    public decimal AmountEuro { get; private set; }

    public CreditNoteWindow(List<InvoiceRepo.CreditNoteCandidate> candidates, long preselectedInvoiceId = 0, decimal defaultAmountEuro = 0)
    {
        InitializeComponent();
        var list = candidates.Select(c => new Candidate { InvoiceId = c.InvoiceId, Label = c.Label }).ToList();
        _vm = new VM(list, preselectedInvoiceId, defaultAmountEuro);
        DataContext = _vm;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var c = _vm.SelectedCandidate;
        if (c is null || c.InvoiceId <= 0)
        {
            MessageBox.Show("Sélectionne une facture.", "PARAFacto", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!decimal.TryParse((_vm.AmountEuroText ?? "").Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var v))
        {
            MessageBox.Show("Montant invalide.", "PARAFacto", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (v <= 0)
        {
            MessageBox.Show("Le montant doit être > 0.", "PARAFacto", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SelectedInvoiceId = c.InvoiceId;
        AmountEuro = v;
        DialogResult = true;
    }
}
