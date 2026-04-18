using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using PARAFactoNative.Models;
using PARAFactoNative.Services;

namespace PARAFactoNative.Views;

public partial class PaymentsEditWindow : Window
{
    public sealed class PaymentEditRow : INotifyPropertyChanged
    {
        public const string DefaultPaymentMethod = "Virement";

        /// <summary>Libellés exacts du ComboBox — la sélection WPF exige une correspondance avec une entrée de la liste.</summary>
        public static readonly string[] KnownPaymentMethods = { "Espèces", "Virement", "Carte", "Autre" };

        /// <summary>Aligne la valeur issue de la base / import sur un libellé connu (casse ignorée).</summary>
        public static string NormalizePaymentMethod(string? raw)
        {
            var s = (raw ?? "").Trim();
            if (s.Length == 0) return DefaultPaymentMethod;
            foreach (var k in KnownPaymentMethods)
            {
                if (string.Equals(s, k, StringComparison.OrdinalIgnoreCase))
                    return k;
            }
            return s;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Notify([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public long Id { get; set; }

        private string _paidDateIso = "";
        public string PaidDateIso
        {
            get => _paidDateIso;
            set { if (_paidDateIso == value) return; _paidDateIso = value; Notify(); }
        }

        private string _amountEuroText = "";
        public string AmountEuroText
        {
            get => _amountEuroText;
            set { if (_amountEuroText == value) return; _amountEuroText = value; Notify(); }
        }

        private string _method = DefaultPaymentMethod;
        public string Method
        {
            get => _method;
            set { if (_method == value) return; _method = value; Notify(); }
        }

        private string _reference = "";
        public string Reference
        {
            get => _reference;
            set { if (_reference == value) return; _reference = value; Notify(); }
        }
    }

    private sealed class VM
    {
        public string InvoiceLabel { get; init; } = "";
        /// <summary>Suggestion « solde restant » pour nouvelles lignes (centimes). Null = pas de plafond (ex. trop-perçu).</summary>
        public int? SoftCapTotalCents { get; init; }
        /// <summary>1ère ligne « Ajouter ligne » : trop-perçu (€), ex. -22,95. Null = non utilisé.</summary>
        public decimal? SuggestedAdditionalLineEuro { get; init; }
        public ObservableCollection<PaymentEditRow> Rows { get; } = new();
    }

    private readonly VM _vm;
    private bool _suggestedAdditionalLineConsumed;

    public ObservableCollection<PaymentEditRow> Rows => _vm.Rows;

    /// <param name="maxPaymentTotalCents">Suggestion du solde restant pour « Ajouter ligne » (centimes). Null = saisie libre (trop-perçu, remboursement NC, etc.).</param>
    /// <param name="suggestedAdditionalLineEuro">Si trop-perçu (solde négatif), proposé pour la 1ère ligne ajoutée (ex. -22,95 €).</param>
    public PaymentsEditWindow(Invoice invoice, decimal defaultEuro, PaymentEditRow[] existingRows, int? maxPaymentTotalCents = null, decimal? suggestedAdditionalLineEuro = null)
    {
        InitializeComponent();

        var softCap = maxPaymentTotalCents.HasValue ? Math.Max(0, maxPaymentTotalCents.Value) : (int?)null;

        _vm = new VM
        {
            InvoiceLabel = $"{invoice.InvoiceNo} — {invoice.Recipient}",
            SoftCapTotalCents = softCap,
            SuggestedAdditionalLineEuro = suggestedAdditionalLineEuro
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
                Reference = ""
            });
        }

        DataContext = _vm;
    }

    private void PaymentMethodButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not PaymentEditRow row)
            return;

        var methods = new List<string>();
        var current = (row.Method ?? "").Trim();
        if (current.Length > 0 &&
            !Array.Exists(PaymentEditRow.KnownPaymentMethods,
                k => string.Equals(k, current, StringComparison.OrdinalIgnoreCase)))
        {
            methods.Add(current);
        }
        methods.AddRange(PaymentEditRow.KnownPaymentMethods);

        var menu = new ContextMenu();
        foreach (var label in methods)
        {
            var pick = label;
            var item = new MenuItem { Header = pick };
            item.Click += (_, _) =>
            {
                row.Method = pick;
                menu.IsOpen = false;
            };
            menu.Items.Add(item);
        }

        menu.PlacementTarget = btn;
        menu.Placement = PlacementMode.Bottom;
        menu.StaysOpen = false;
        menu.Focus();
        menu.IsOpen = true;
    }

    private void AddRow_Click(object sender, RoutedEventArgs e)
    {
        decimal remEuro;
        if (_vm.SoftCapTotalCents is { } cap)
        {
            var remCents = Math.Max(0, cap - SumPositiveRowsCents(_vm.Rows));
            remEuro = remCents / 100m;
        }
        else if (!_suggestedAdditionalLineConsumed && _vm.SuggestedAdditionalLineEuro is { } sug)
        {
            remEuro = sug;
            _suggestedAdditionalLineConsumed = true;
        }
        else
            remEuro = 0m;

        _vm.Rows.Add(new PaymentEditRow
        {
            Id = 0,
            PaidDateIso = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            AmountEuroText = remEuro.ToString("0.00", CultureInfo.InvariantCulture),
            Reference = ""
        });
        Grid.SelectedIndex = _vm.Rows.Count - 1;
        Grid.ScrollIntoView(Grid.SelectedItem);
    }

    /// <summary>Somme des montants strictement positifs (centimes), pour plafond « solde restant ».</summary>
    private static int SumPositiveRowsCents(IEnumerable<PaymentEditRow> rows)
    {
        var sum = 0;
        foreach (var r in rows)
        {
            var t = (r.AmountEuroText ?? "").Trim().Replace(',', '.');
            if (!decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out var euro) || euro <= 0)
                continue;
            sum += (int)Math.Round(euro * 100m, MidpointRounding.AwayFromZero);
        }

        return sum;
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
            if (!decimal.TryParse(amtText, NumberStyles.Number, CultureInfo.InvariantCulture, out var amt) || amt == 0)
            {
                MessageBox.Show("Montant invalide (doit être différent de 0).", "PARAFacto", MessageBoxButton.OK, MessageBoxImage.Warning);
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

