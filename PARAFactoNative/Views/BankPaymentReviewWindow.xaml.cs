using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PARAFactoNative.Services;

namespace PARAFactoNative.Views;

public partial class BankPaymentReviewWindow : Window
{
    private readonly BankPaymentImportService.ReviewRequest _request;
    private readonly List<CandidateRow> _allRows;

    public BankPaymentImportService.ReviewDecision Decision { get; private set; } = new()
    {
        Action = BankPaymentImportService.ManualReviewAction.Skip
    };

    public BankPaymentReviewWindow(BankPaymentImportService.ReviewRequest request)
    {
        InitializeComponent();
        _request = request;
        _allRows = request.Candidates.Select(CandidateRow.From).ToList();

        ReasonText.Text = request.Reason;
        PaymentText.Text = $"Ligne {request.Transaction.LineNumber} - Date {request.Transaction.DateIso} - Montant {request.Transaction.AmountEuro}";
        CommunicationText.Text = string.IsNullOrWhiteSpace(request.Transaction.RawText)
            ? "Communication : (vide)"
            : "Communication : " + request.Transaction.RawText;
        SearchBox.Text = BuildInitialSearchText(request);
        ApplyFilter();
    }

    private static string BuildInitialSearchText(BankPaymentImportService.ReviewRequest request)
    {
        var amount = (request.Transaction.AmountCents / 100m).ToString("0.00", CultureInfo.GetCultureInfo("fr-BE"));
        var text = (request.Transaction.RawText ?? "").Trim();
        return text.Length == 0 ? amount : text;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void CandidatesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => ApplySelected();

    private void Apply_Click(object sender, RoutedEventArgs e) => ApplySelected();

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        Decision = new BankPaymentImportService.ReviewDecision
        {
            Action = BankPaymentImportService.ManualReviewAction.Skip
        };
        DialogResult = true;
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        Decision = new BankPaymentImportService.ReviewDecision
        {
            Action = BankPaymentImportService.ManualReviewAction.Stop
        };
        DialogResult = true;
    }

    private void ApplySelected()
    {
        if (CandidatesGrid.SelectedItem is not CandidateRow row)
        {
            MessageBox.Show("Sélectionnez une facture à associer.", "PARAFacto", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Decision = new BankPaymentImportService.ReviewDecision
        {
            Action = BankPaymentImportService.ManualReviewAction.ApplyToInvoice,
            InvoiceId = row.InvoiceId
        };
        DialogResult = true;
    }

    private void ApplyFilter()
    {
        var query = Normalize(SearchBox.Text);
        var amountCents = TryParseAmountCents(SearchBox.Text);

        IEnumerable<CandidateRow> rows = _allRows;
        if (query.Length > 0 || amountCents.HasValue)
        {
            rows = rows.Where(r =>
                (query.Length > 0 && Normalize(r.InvoiceNo + " " + r.PaymentReference + " " + r.Recipient).Contains(query, StringComparison.OrdinalIgnoreCase))
                || (amountCents.HasValue && (r.BalanceCents == amountCents.Value || r.TotalCents == amountCents.Value)));
        }

        var filtered = rows
            .OrderBy(r => amountCents.HasValue ? Math.Abs(r.BalanceCents - amountCents.Value) : 0)
            .ThenBy(r => r.InvoiceNo)
            .ToList();

        CandidatesGrid.ItemsSource = filtered.Count > 0 ? filtered : _allRows;
        if (CandidatesGrid.Items.Count > 0)
            CandidatesGrid.SelectedIndex = 0;
    }

    private static int? TryParseAmountCents(string? value)
    {
        var s = (value ?? "").Trim();
        if (s.Length == 0)
            return null;
        var m = Regex.Match(s, @"-?\d+(?:[,.]\d{1,2})?");
        if (!m.Success)
            return null;
        var txt = m.Value.Replace(',', '.');
        return decimal.TryParse(txt, NumberStyles.Number, CultureInfo.InvariantCulture, out var euro)
            ? (int)Math.Round(euro * 100m, MidpointRounding.AwayFromZero)
            : null;
    }

    private static string Normalize(string? value)
    {
        var s = (value ?? "").Trim().ToUpperInvariant();
        var formD = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(formD.Length);
        foreach (var ch in formD)
        {
            var uc = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (uc != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }

        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    private sealed class CandidateRow
    {
        public long InvoiceId { get; init; }
        public string InvoiceNo { get; init; } = "";
        public string PaymentReference { get; init; } = "";
        public string Recipient { get; init; } = "";
        public int TotalCents { get; init; }
        public int BalanceCents { get; init; }
        public string TotalEuro => Euro(TotalCents);
        public string BalanceEuro => Euro(BalanceCents);

        public static CandidateRow From(InvoiceRepo.BankPaymentCandidate c) => new()
        {
            InvoiceId = c.InvoiceId,
            InvoiceNo = c.InvoiceNo,
            PaymentReference = c.PaymentReference,
            Recipient = c.Recipient,
            TotalCents = c.TotalCents,
            BalanceCents = c.BalanceCents
        };

        private static string Euro(int cents) => (cents / 100m).ToString("0.00", CultureInfo.GetCultureInfo("fr-BE")) + " €";
    }
}
