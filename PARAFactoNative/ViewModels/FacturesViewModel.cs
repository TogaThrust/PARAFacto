using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using PARAFactoNative.Models;
using PARAFactoNative.Services;
using PARAFactoNative.Views;

namespace PARAFactoNative.ViewModels;

public sealed class FacturesViewModel : NotifyBase
{
    private const string RelinkSecurityCode = "7324";
    private bool _relinkUnlocked;
    private static readonly List<string> TypeKeys = new() { "TOUTES", "PATIENT", "MUTUELLE", "CREDIT_NOTE" };
    private static readonly List<string> StatusKeys = new() { "TOUTES", "IMPAYEE", "PARTIELLE", "PAYEE", "ACQUITTEE", "PERTE", "MODIFIEE" };
    private static readonly List<string> PeriodKindKeys = new()
    {
        "Toutes les périodes",
        "De 'date' à 'date'",
        "Année",
        "Année / Semaine",
        "Année / Mois",
        "Année / Trimestre",
        "Dernière année complète",
        "Année courante",
        "Trimestre courant"
    };

    /// <summary>Instances stables pour les ComboBox (évite une liste neuve à chaque binding → sélection vide).</summary>
    private List<string> _typesList = new();
    private List<string> _statusesList = new();

    public List<string> Types => _typesList;
    public List<string> Statuses => _statusesList;

    private void RebuildTypeAndStatusLists()
    {
        _typesList = TypeKeys.Select(UiTextTranslator.Translate).ToList();
        _statusesList = StatusKeys.Select(UiTextTranslator.Translate).ToList();
    }

    private readonly InvoiceRepo _repo = new();
    private readonly PatientRepo _patientRepo = new();

    private string _selectedType = "TOUTES";
    public string SelectedType
    {
        get => UiTextTranslator.Translate(_selectedType);
        set
        {
            var canonical = NormalizeType(value);
            if (Set(ref _selectedType, canonical))
            {
                Raise(nameof(SelectedType));
                Refresh();
            }
        }
    }

    private string _selectedStatus = "TOUTES";
    public string SelectedStatus
    {
        get => UiTextTranslator.Translate(_selectedStatus);
        set
        {
            var canonical = NormalizeStatus(value);
            if (Set(ref _selectedStatus, canonical))
            {
                Raise(nameof(SelectedStatus));
                Refresh();
            }
        }
    }

    private string _search = "";
    public string Search { get => _search; set { if (Set(ref _search, value)) Refresh(); } }

    public List<InvoiceRow> Items { get; private set; } = new();

    // Période pour filtre et stats (sous les Détails)
    public List<string> PeriodKinds => PeriodKindKeys.Select(UiTextTranslator.Translate).ToList();

    private string _periodKind = "Toutes les périodes";
    public string PeriodKind
    {
        get => UiTextTranslator.Translate(_periodKind);
        set
        {
            var canonical = NormalizePeriodKind(value);
            if (Set(ref _periodKind, canonical))
            {
                Raise(nameof(PeriodKind));
                RaisePeriodFields();
                Refresh();
            }
        }
    }

    private DateTime? _periodDateFrom;
    public DateTime? PeriodDateFrom { get => _periodDateFrom; set { if (Set(ref _periodDateFrom, value)) Refresh(); } }

    private DateTime? _periodDateTo;
    public DateTime? PeriodDateTo { get => _periodDateTo; set { if (Set(ref _periodDateTo, value)) Refresh(); } }

    private string _periodYear = DateTime.Today.Year.ToString(CultureInfo.InvariantCulture);
    public string PeriodYear { get => _periodYear; set { if (Set(ref _periodYear, value ?? "")) Refresh(); } }

    private int _periodMonth = DateTime.Today.Month;
    public int PeriodMonth { get => _periodMonth; set { if (Set(ref _periodMonth, value)) Refresh(); } }

    private static int ParsePeriodInt(string? s, int defaultValue)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        return int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : defaultValue;
    }

    private int _periodWeek = 1;
    public int PeriodWeek { get => _periodWeek; set { if (Set(ref _periodWeek, value)) Refresh(); } }

    private int _periodQuarter = 1;
    public int PeriodQuarter { get => _periodQuarter; set { if (Set(ref _periodQuarter, value)) Refresh(); } }

    private void RaisePeriodFields()
    {
        Raise(nameof(PeriodDateFrom)); Raise(nameof(PeriodDateTo));
        Raise(nameof(PeriodYear)); Raise(nameof(PeriodMonth)); Raise(nameof(PeriodWeek)); Raise(nameof(PeriodQuarter));
        Raise(nameof(PeriodLabel));
        Raise(nameof(ShowPeriodDateRange)); Raise(nameof(ShowPeriodYearOnly)); Raise(nameof(ShowPeriodYearWeek));
        Raise(nameof(ShowPeriodYearMonth)); Raise(nameof(ShowPeriodYearQuarter));
    }

    public string PeriodLabel => GetPeriodRangeLabel();

    public bool ShowPeriodDateRange => _periodKind == "De 'date' à 'date'";
    public bool ShowPeriodYearOnly => _periodKind == "Année";
    public bool ShowPeriodYearWeek => _periodKind == "Année / Semaine";
    public bool ShowPeriodYearMonth => _periodKind == "Année / Mois";
    public bool ShowPeriodYearQuarter => _periodKind == "Année / Trimestre";

    public List<int> MonthNumbers { get; } = Enumerable.Range(1, 12).ToList();
    public List<int> WeekNumbers { get; } = Enumerable.Range(1, 52).ToList();
    public List<int> QuarterNumbers { get; } = Enumerable.Range(1, 4).ToList();

    /// <summary>Années disponibles pour la liste déroulante (année courante - 10 à année courante + 1).</summary>
    public List<string> AvailableYears { get; } = Enumerable.Range(DateTime.Today.Year - 10, 12)
        .Select(y => y.ToString(CultureInfo.InvariantCulture)).ToList();

    public InvoiceStats StatsFiltered { get; private set; } = new();
    public InvoiceStats StatsTotal { get; private set; } = new();

    public bool IsCreditNoteType => string.Equals(_selectedType, "CREDIT_NOTE", StringComparison.OrdinalIgnoreCase);
    public string PayeLabel => IsCreditNoteType ? "PAYEES" : "PAYE";

    private List<InvoiceRow> _allPeriodRows = new();

    private InvoiceRow? _selected;
    public InvoiceRow? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
            {
                Raise(nameof(SelectedDetails));
                Raise(nameof(SelectedCommentText));
                Raise(nameof(HasSelectedComment));
                RaiseCanExecutes();
            }
        }
    }

    public string SelectedCommentText => (Selected?.UserComment ?? "").Trim();
    public bool HasSelectedComment => !string.IsNullOrWhiteSpace(SelectedCommentText);

    public string SelectedDetails
    {
        get
        {
            if (Selected is null) return "";
            if (string.Equals(Selected.Kind?.Trim(), "credit_note", StringComparison.OrdinalIgnoreCase) && Selected.RefInvoiceId > 0)
            {
                var refInv = _repo.GetById(Selected.RefInvoiceId);
                var refNo = refInv?.InvoiceNo ?? Selected.RefInvoiceId.ToString();
                var montantNc = (Math.Abs(Selected.TotalCents) / 100.0).ToString("0.00") + " €";
                return "Note de crédit\n"
                    + "Pièce de référence n° " + refNo + "\n"
                    + "Montant : " + montantNc + "\n\n"
                    + "(Les colonnes montants de la liste sont vides pour éviter le double comptage avec la facture liée.)";
            }
            if (string.Equals(Selected.Status?.Trim(), "modified", StringComparison.OrdinalIgnoreCase)
                && string.Equals(Selected.Kind?.Trim(), "mutuelle", StringComparison.OrdinalIgnoreCase)
                && Selected.RefInvoiceId > 0)
            {
                var refInv = _repo.GetById(Selected.RefInvoiceId);
                var rev = _repo.GetLastMutualRevisionDetails(Selected.RefInvoiceId);
                var initialCents = refInv?.TotalCents ?? rev?.OriginalTotalCents ?? 0;
                var initialEuro = (initialCents / 100.0).ToString("0.00") + " €";
                var revisionDate = FormatMutualRevisionDisplayDate(rev?.ModifiedAt ?? _repo.GetLastRevisionChangedAt(Selected.RefInvoiceId));
                var motif = !string.IsNullOrWhiteSpace(Selected.Reason) ? Selected.Reason!.Trim() : (rev?.Reason ?? "").Trim();
                var docRef = (rev?.ReferenceDoc ?? "").Trim();
                if (docRef.Length == 0)
                {
                    var rd = (Selected.RefDoc ?? "").Trim();
                    if (rd.Length > 0 && !rd.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                        docRef = rd;
                }

                return "FACTURE MUTUELLE MODIFIÉE\n"
                    + "Nouveau montant total AO : " + Selected.TotalEuro + "\n"
                    + "Montant initial (facture remplacée) : " + initialEuro + "\n"
                    + "Date de la modification : " + revisionDate + "\n"
                    + "Motif invoqué par la mutuelle : " + motif + "\n"
                    + "Document de référence (mutuelle) : " + docRef + "\n\n"
                    + "Total " + Selected.TotalEuro + " | Payé " + Selected.PaidEuro + " | Perte " + Selected.LossEuro + " | Solde " + Selected.BalanceEuro;
            }
            if (string.Equals(Selected.Status?.Trim(), "acquittee", StringComparison.OrdinalIgnoreCase))
                return $"N° {Selected.InvoiceNo}\n{Selected.Recipient}\nPaiement en cash\nTotal {Selected.TotalEuro} | Payé {Selected.PaidEuro} | Solde {Selected.BalanceEuro}";

            var sb = new StringBuilder();
            sb.Append($"N° {Selected.InvoiceNo}\n{Selected.Recipient}\n");
            if (Selected.NcCents > 0)
                sb.Append($"Total {Selected.TotalEuro} | Payé {Selected.PaidEuro} | NC {Selected.NcEuro} | Perte {Selected.LossEuro} | Solde {Selected.BalanceEuro}\n\n");
            else
                sb.Append($"Total {Selected.TotalEuro} | Payé {Selected.PaidEuro} | Perte {Selected.LossEuro} | Solde {Selected.BalanceEuro}\n\n");

            var payments = _repo.GetPaymentHistory(Selected.InvoiceId);
            if (payments.Count > 0)
            {
                var totalCents = Selected.TotalCents;
                var cumulPaid = 0;
                var isFullPayment = Selected.PaidCents >= totalCents;

                if (isFullPayment && payments.Count == 1)
                {
                    var p = payments[0];
                    var dateFmt = FormatPaymentDate(p.PaidDateIso);
                    if (string.IsNullOrEmpty(dateFmt)) dateFmt = "date non renseignée";
                    var methodLabel = string.IsNullOrWhiteSpace(p.Method) ? "type non renseigné" : p.Method.Trim();
                    sb.Append($"Paiement total — {dateFmt} — {methodLabel}\n");
                }
                else
                {
                    for (var i = 0; i < payments.Count; i++)
                    {
                        var p = payments[i];
                        cumulPaid += p.AmountCents;
                        var balanceRestant = Math.Max(0, totalCents - cumulPaid);
                        var ord = (i + 1) == 1 ? "1ER" : $"{i + 1}EME";
                        var dateFmt = FormatPaymentDate(p.PaidDateIso);
                        if (string.IsNullOrEmpty(dateFmt)) dateFmt = "date non renseignée";
                        var methodLabel = string.IsNullOrWhiteSpace(p.Method) ? "type non renseigné" : p.Method.Trim();
                        var amountEuro = (p.AmountCents / 100.0).ToString("0.00", System.Globalization.CultureInfo.GetCultureInfo("fr-BE")) + " €";
                        var balanceEuro = (balanceRestant / 100.0).ToString("0.00", System.Globalization.CultureInfo.GetCultureInfo("fr-BE")) + " €";
                        sb.Append($"{ord} PAIEMENT PARTIEL — {dateFmt} — {amountEuro} — {methodLabel} — Solde restant : {balanceEuro}\n");
                    }
                }
                if (Selected.LossMagnitudeCents > 0)
                    sb.Append($"\nPerte déclarée : {Selected.LossEuro}");
                if (Selected.NcCents > 0)
                    sb.Append($"\nNote(s) de crédit : {Selected.NcEuro}");
            }
            else if (Selected.PaidCents > 0)
            {
                if (!string.IsNullOrEmpty(Selected.PaymentDateDisplay))
                {
                    sb.Append($"Date de paiement : {Selected.PaymentDateDisplay}\n");
                    sb.Append("Type de paiement et détail non enregistrés en base. Pour les afficher : enregistrer via « Paiement » ou fichier des paiements (colonnes DatePaiement, MontantPaye, Méthode) puis réimporter.");
                }
                else
                {
                    sb.Append("Aucun enregistrement de paiement en base (date et type non affichables).\n");
                    sb.Append("Pour enregistrer : cliquer sur « Paiement » ou ajouter une ligne dans le fichier des paiements puis réimporter.");
                }
            }
            else if (Selected.LossMagnitudeCents > 0 && Selected.PaidCents == 0)
            {
                sb.Append("Paiement partiel (Total − Perte) non enregistré dans le fichier des paiements — date et type non connus.\n");
                sb.Append("Pour les renseigner : cliquer sur « Paiement » ou ajouter une ligne dans le fichier des paiements puis réimporter.");
            }

            return sb.ToString().TrimEnd();
        }
    }

    private static string FormatPaymentDate(string? dateIso)
    {
        if (string.IsNullOrWhiteSpace(dateIso)) return "";
        var s = dateIso.Trim();
        if (s.Length < 10) return "";
        var datePart = s.Substring(0, 10);
        if (!DateTime.TryParse(datePart, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return "";
        return d.ToString("dd/MM/yyyy", CultureInfo.CurrentCulture);
    }

    private static string FormatMutualRevisionDisplayDate(string? changedAt)
    {
        if (string.IsNullOrWhiteSpace(changedAt)) return "";
        var s = changedAt.Trim();
        if (s.Length >= 10)
        {
            var head = s.Substring(0, 10);
            if (DateTime.TryParse(head, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                return d.ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture);
        }
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d2))
            return d2.ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture);
        return s;
    }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand OpenPdfCommand { get; }
    public RelayCommand RelinkPatientsCommand { get; }
    public RelayCommand AddPaymentCommand { get; }
    public RelayCommand AddLossCommand { get; }
    public RelayCommand ClearLossCommand { get; }
    public RelayCommand CreateCreditNoteCommand { get; }
    public RelayCommand CreateMutualRevisionCommand { get; }
    public RelayCommand DeleteModifiedMutualInvoiceCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand DeleteMonthInvoicesCommand { get; }

    public FacturesViewModel()
    {
        RebuildTypeAndStatusLists();

        RefreshCommand = new RelayCommand(Refresh);
        OpenPdfCommand = new RelayCommand(OpenSelectedPdf, () => Selected != null);
        RelinkPatientsCommand = new RelayCommand(ExecuteRelinkPatientsWithSecurity);

        AddPaymentCommand = new RelayCommand(AddPayment, CanPay);
        AddLossCommand = new RelayCommand(AddLoss, CanDeclareLoss);
        ClearLossCommand = new RelayCommand(ClearLoss, CanClearLoss);
        CreateCreditNoteCommand = new RelayCommand(CreateCreditNote, CanCreateCreditNote);
        CreateMutualRevisionCommand = new RelayCommand(CreateMutualRevision, CanMutualRevision);
        DeleteModifiedMutualInvoiceCommand = new RelayCommand(DeleteModifiedMutualInvoice, CanDeleteModifiedMutualInvoice);
        OpenFolderCommand = new RelayCommand(OpenWorkspaceFolder);
        DeleteMonthInvoicesCommand = new RelayCommand(DeleteMonthInvoices, CanDeleteMonthInvoices);
        UiLanguageService.LanguageChanged += _ =>
        {
            RebuildTypeAndStatusLists();
            Raise(nameof(Types));
            Raise(nameof(Statuses));
            Raise(nameof(PeriodKinds));
            Raise(nameof(SelectedType));
            Raise(nameof(SelectedStatus));
            Raise(nameof(PeriodKind));
            Raise(nameof(SelectedPeriodLabel));
            Raise(nameof(PeriodLabel));
        };

        Refresh();
    }

    /// <summary>À l’affichage de l’onglet Factures : filtres Type et Statut sur « TOUTES » et grille à jour.</summary>
    public void OnFacturesTabActivated()
    {
        _selectedType = "TOUTES";
        _selectedStatus = "TOUTES";
        Raise(nameof(SelectedType));
        Raise(nameof(SelectedStatus));
        Refresh();
    }

    private static string NormalizeType(string? value)
    {
        var v = (value ?? "").Trim();
        foreach (var key in TypeKeys)
        {
            if (string.Equals(v, key, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(v, UiTextTranslator.Translate(key), StringComparison.OrdinalIgnoreCase))
                return key;
        }
        return "TOUTES";
    }

    private static string NormalizeStatus(string? value)
    {
        var v = (value ?? "").Trim();
        foreach (var key in StatusKeys)
        {
            if (string.Equals(v, key, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(v, UiTextTranslator.Translate(key), StringComparison.OrdinalIgnoreCase))
                return key;
        }
        return "TOUTES";
    }

    private static string NormalizePeriodKind(string? value)
    {
        var v = (value ?? "").Trim();
        foreach (var key in PeriodKindKeys)
        {
            if (string.Equals(v, key, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(v, UiTextTranslator.Translate(key), StringComparison.OrdinalIgnoreCase))
                return key;
        }
        return "Toutes les périodes";
    }

    public void Reload() => Refresh();

    private void RaiseCanExecutes()
    {
        OpenPdfCommand.RaiseCanExecuteChanged();
        AddPaymentCommand.RaiseCanExecuteChanged();
        AddLossCommand.RaiseCanExecuteChanged();
        ClearLossCommand.RaiseCanExecuteChanged();
        CreateCreditNoteCommand.RaiseCanExecuteChanged();
        CreateMutualRevisionCommand.RaiseCanExecuteChanged();
        DeleteModifiedMutualInvoiceCommand.RaiseCanExecuteChanged();
        DeleteMonthInvoicesCommand.RaiseCanExecuteChanged();
        Raise(nameof(SelectedPeriodLabel));
        Raise(nameof(SelectedCommentText));
        Raise(nameof(HasSelectedComment));
    }


    public string SelectedPeriodLabel
    {
        get
        {
            var p = ResolveDeletePeriod();
            return string.IsNullOrWhiteSpace(p) ? "" : $"Période sélectionnée : {p}";
        }
    }

    private void Refresh()
    {
        _repo.RepairStrandedSupersededMutualInvoices();
        _repo.RepairStaleLossInvoiceStatus();

        var (dateFrom, dateTo) = GetPeriodRange();
        var invoiceNoPrefixes = GetInvoiceNoMonthPrefixes();

        var kind = SelectedType switch
        {
            "TOUTES" => "ANY",
            "PATIENT" => "patient",
            "MUTUELLE" => "mutuelle",
            "CREDIT_NOTE" => "credit_note",
            _ => "ANY"
        };

        var status = SelectedStatus switch
        {
            "TOUTES" => "ANY",
            "IMPAYEE" => "unpaid",
            "PARTIELLE" => "partial",
            "PAYEE" => "paid",
            "ACQUITTEE" => "acquittee",
            "PERTE" => "loss",
            "MODIFIEE" => "modified",
            _ => "ANY"
        };

        // Période basée sur numéro de facture (mois/trimestre/année) => pas de filtre date_iso
        var from = invoiceNoPrefixes != null ? null : dateFrom;
        var to = invoiceNoPrefixes != null ? null : dateTo;
        var rows = _repo.Search(kind, from, to, status, Search, invoiceNoPrefixes)
            .Select(i => new InvoiceRow
            {
                InvoiceId = i.Id,
                InvoiceNo = i.InvoiceNo ?? "",
                IssuedDate = i.DateIso ?? "",
                PaymentDateDisplay = string.Equals(i.Kind, "credit_note", StringComparison.OrdinalIgnoreCase)
                    ? ""
                    : FormatPaymentDate(i.LastPaymentDateIso),
                Kind = i.Kind ?? "",
                PatientId = i.PatientId ?? 0,
                Recipient = string.IsNullOrWhiteSpace(i.Recipient)
                    ? (((i.PatientId ?? 0) > 0 ? "PATIENT" : "PATIENT (non lié)"))
                    : i.Recipient,
                Status = (i.Status ?? "").Trim(),
                TotalCents = i.TotalCents,
                PaidCents = i.PaidCents,
                LossCents = _repo.GetLossTotalCents(i.Id),
                NcCents = string.Equals(i.Kind, "credit_note", StringComparison.OrdinalIgnoreCase)
                    ? Math.Abs(i.TotalCents)
                    : _repo.GetCreditNoteTotalCents(i.Id),
                RefInvoiceId = i.RefInvoiceId ?? 0,
                Reason = i.Reason ?? "",
                RefDoc = i.RefDoc ?? "",
                UserComment = i.UserComment ?? ""
            })
            .ToList();

        Items = rows;

        // TOTAL ENREGISTRÉ : toujours sur l'ensemble des enregistrements (sans filtre de période)
        var allKind = "ANY";
        var allStatus = "ANY";
        var allRows = _repo.Search(allKind, null, null, allStatus, "")
            .Select(i => new InvoiceRow
            {
                InvoiceId = i.Id,
                InvoiceNo = i.InvoiceNo ?? "",
                IssuedDate = i.DateIso ?? "",
                PaymentDateDisplay = string.Equals(i.Kind, "credit_note", StringComparison.OrdinalIgnoreCase)
                    ? ""
                    : FormatPaymentDate(i.LastPaymentDateIso),
                Kind = i.Kind ?? "",
                PatientId = i.PatientId ?? 0,
                Recipient = i.Recipient ?? "",
                Status = (i.Status ?? "").Trim(),
                TotalCents = i.TotalCents,
                PaidCents = i.PaidCents,
                LossCents = _repo.GetLossTotalCents(i.Id),
                NcCents = string.Equals(i.Kind, "credit_note", StringComparison.OrdinalIgnoreCase)
                    ? Math.Abs(i.TotalCents)
                    : _repo.GetCreditNoteTotalCents(i.Id),
                RefInvoiceId = i.RefInvoiceId ?? 0,
                Reason = i.Reason ?? "",
                RefDoc = i.RefDoc ?? "",
                UserComment = i.UserComment ?? ""
            })
            .ToList();
        _allPeriodRows = allRows;
        StatsFiltered = ComputeStats(rows);
        StatsTotal = ComputeStats(_allPeriodRows);

        Raise(nameof(Items));
        Raise(nameof(SelectedDetails));
        Raise(nameof(StatsFiltered));
        Raise(nameof(StatsTotal));
        Raise(nameof(PeriodLabel));
        Raise(nameof(PayeLabel));
        Raise(nameof(SelectedCommentText));
        Raise(nameof(HasSelectedComment));
        RaiseCanExecutes();
    }

    /// <summary>Préfixes MM-YYYY pour filtre par numéro de facture (ex. 04-2025-.., NC-04-2025-..). Null = on utilise date_iso.</summary>
    private List<string>? GetInvoiceNoMonthPrefixes()
    {
        var today = DateTime.Today;
        switch (_periodKind)
        {
            case "Année":
                var y1 = ParsePeriodInt(PeriodYear, today.Year);
                return Enumerable.Range(1, 12).Select(m => $"{m:00}-{y1}").ToList();
            case "Année / Mois":
                var ym = ParsePeriodInt(PeriodYear, today.Year);
                var month = Math.Clamp(PeriodMonth, 1, 12);
                return new List<string> { $"{month:00}-{ym}" };
            case "Année / Trimestre":
                var yq = ParsePeriodInt(PeriodYear, today.Year);
                var q = Math.Clamp(PeriodQuarter, 1, 4);
                var monthsQ = q switch { 1 => new[] { 1, 2, 3 }, 2 => new[] { 4, 5, 6 }, 3 => new[] { 7, 8, 9 }, _ => new[] { 10, 11, 12 } };
                return monthsQ.Select(m => $"{m:00}-{yq}").ToList();
            case "Dernière année complète":
                var lastYear = today.Year - 1;
                return Enumerable.Range(1, 12).Select(m => $"{m:00}-{lastYear}").ToList();
            case "Trimestre courant":
                var qCur = (today.Month - 1) / 3 + 1;
                var monthsCur = qCur switch { 1 => new[] { 1, 2, 3 }, 2 => new[] { 4, 5, 6 }, 3 => new[] { 7, 8, 9 }, _ => new[] { 10, 11, 12 } };
                return monthsCur.Select(m => $"{m:00}-{today.Year}").ToList();
            default:
                return null;
        }
    }

    private (DateTime? from, DateTime? to) GetPeriodRange()
    {
        var today = DateTime.Today;
        switch (_periodKind)
        {
            case "Toutes les périodes":
                return (null, null);
            case "De 'date' à 'date'":
                return (PeriodDateFrom, PeriodDateTo);
            case "Année":
                var y1 = ParsePeriodInt(PeriodYear, today.Year);
                return (new DateTime(y1, 1, 1), new DateTime(y1, 12, 31));
            case "Année / Semaine":
                var yw = ParsePeriodInt(PeriodYear, today.Year);
                var w = Math.Clamp(PeriodWeek, 1, 53);
                var startWeek = System.Globalization.ISOWeek.ToDateTime(yw, w, DayOfWeek.Monday);
                return (startWeek, startWeek.AddDays(6));
            case "Année / Mois":
                var ym = ParsePeriodInt(PeriodYear, today.Year);
                var m = Math.Clamp(PeriodMonth, 1, 12);
                var startMonth = new DateTime(ym, m, 1);
                return (startMonth, startMonth.AddMonths(1).AddDays(-1));
            case "Année / Trimestre":
                var yq = ParsePeriodInt(PeriodYear, today.Year);
                var q = Math.Clamp(PeriodQuarter, 1, 4);
                var (qStart, qEnd) = q switch
                {
                    1 => (new DateTime(yq, 1, 1), new DateTime(yq, 3, 30)),
                    2 => (new DateTime(yq, 4, 1), new DateTime(yq, 6, 30)),
                    3 => (new DateTime(yq, 7, 1), new DateTime(yq, 9, 30)),
                    _ => (new DateTime(yq, 10, 1), new DateTime(yq, 12, 31))
                };
                return (qStart, qEnd);
            case "Dernière année complète":
                var lastYear = today.Year - 1;
                return (new DateTime(lastYear, 1, 1), new DateTime(lastYear, 12, 31));
            case "Année courante":
                return (new DateTime(today.Year, 1, 1), today);
            case "Trimestre courant":
                var qCur = (today.Month - 1) / 3 + 1;
                var (qcStart, qcEnd) = qCur switch
                {
                    1 => (new DateTime(today.Year, 1, 1), new DateTime(today.Year, 3, 30)),
                    2 => (new DateTime(today.Year, 4, 1), new DateTime(today.Year, 6, 30)),
                    3 => (new DateTime(today.Year, 7, 1), new DateTime(today.Year, 9, 30)),
                    _ => (new DateTime(today.Year, 10, 1), new DateTime(today.Year, 12, 31))
                };
                return (qcStart, qcEnd);
            default:
                return (null, null);
        }
    }

    private string GetPeriodRangeLabel()
    {
        var (from, to) = GetPeriodRange();
        if (!from.HasValue || !to.HasValue) return UiTextTranslator.Translate("Toutes les périodes");
        return $"{from.Value:dd/MM/yyyy} → {to.Value:dd/MM/yyyy}";
    }

    private static InvoiceStats ComputeStats(List<InvoiceRow> rows)
    {
        var paye = 0;
        var partiel = 0;
        var impaye = 0;
        var enAttente = 0;
        var pertes = 0;
        var nc = 0;
        foreach (var r in rows)
        {
            // Lignes NC : montants vides en grille ; tout est sur la facture liée.
            if (string.Equals(r.Kind, "credit_note", StringComparison.OrdinalIgnoreCase))
                continue;

            var stLower = (r.Status ?? "").Trim().ToLowerInvariant();
            // Factures remplacées : cellules vides mais ancien solde encore en base → ne pas les additionner aux totaux.
            if (stLower == "superseded")
                continue;

            // PAYÉ : somme de la colonne « PAYE » (tout encaissement enregistré sur factures actives).
            paye += r.PaidDisplayCents;

            // PARTIEL : encaissements « partiels » (facture partielle + perte avec paiement partiel) — déjà inclus dans paye.
            if (stLower == "partial" || (stLower == "loss" && r.PaidDisplayCents > 0 && r.PaidDisplayCents < r.TotalCents))
                partiel += r.PaidDisplayCents;

            var bal = r.BalanceCents;
            // Impayés : somme des soldes + pertes déclarées (ex. solde 2228,86 € + perte 11,94 €).
            impaye += bal + r.LossMagnitudeCents;
            // Reste dû affiché (soldes uniquement), sans additionner la colonne perte.
            enAttente += bal;

            pertes += r.LossMagnitudeCents;
            nc += r.NcCents;
        }

        // Net après notes de crédit (encaissements − montants NC sur factures, hors ligne NC affichée à part).
        var totalRecu = paye - nc;

        return new InvoiceStats
        {
            PayeCents = paye,
            PartielCents = partiel,
            TotalRecuCents = totalRecu,
            ImpayeCents = impaye,
            EnAttenteCents = enAttente,
            PertesCents = pertes,
            NcCents = nc
        };
    }

    private bool CanPay()
    {
        var s = Selected;
        if (s is null) return false;
        // Permettre l'édition des paiements / remboursements (factures patients, mutuelles, notes de crédit).
        // On bloque uniquement les lignes remplacées (superseded).
        if (string.Equals((s.Status ?? "").Trim(), "superseded", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private bool CanDeclareLoss()
    {
        var s = Selected;
        if (s is null) return false;
        var st = (s.Status ?? "").Trim().ToLowerInvariant();
        return st is "unpaid" or "partial" or "impayee" or "partielle";
    }

    private bool CanClearLoss()
    {
        var s = Selected;
        if (s is null) return false;
        if (string.Equals((s.Kind ?? "").Trim(), "credit_note", StringComparison.OrdinalIgnoreCase)) return false;
        return s.LossCents != 0;
    }

    private void ClearLoss()
    {
        var row = Selected;
        if (row is null) return;

        if (!ChoiceDialog.AskYesNo(
                "Annuler la perte",
                $"Supprimer la PERTE associée à cette facture ?\n\n{row.InvoiceNo}\n\nCela remettra la facture en IMPAYÉE/PARTIELLE selon les paiements enregistrés.",
                "Supprimer la PERTE",
                "Annuler"))
            return;

        _repo.ClearLoss(row.InvoiceId);
        Refresh();
    }

    private bool CanMutualRevision()
    {
        var s = Selected;
        if (s is null) return false;
        // Uniquement factures mutuelles d'origine (pas les factures patients, pas les factures déjà modifiées)
        if (!string.Equals(s.Kind, "mutuelle", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals((s.Status ?? "").Trim(), "modified", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private bool CanDeleteModifiedMutualInvoice()
    {
        var s = Selected;
        if (s is null) return false;
        return RowLooksLikeDeletableMutualModified(s);
    }

    private static bool RowLooksLikeDeletableMutualModified(InvoiceRow s)
    {
        if (!string.Equals(s.Kind, "mutuelle", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals((s.Status ?? "").Trim(), "modified", StringComparison.OrdinalIgnoreCase)) return true;
        var n = NormalizeInvoiceNoDashes((s.InvoiceNo ?? "").Trim());
        return n.EndsWith("-MOD", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeInvoiceNoDashes(string invoiceNo)
    {
        if (invoiceNo.Length == 0) return "";
        return invoiceNo
            .Replace('\u2011', '-')
            .Replace('\u2012', '-')
            .Replace('\u2013', '-')
            .Replace('\u2014', '-')
            .Replace('\u2010', '-')
            .Replace('\u2212', '-');
    }

    private void AddPayment()
    {
        var row = Selected;
        if (row is null) return;

        var inv = _repo.GetById(row.InvoiceId);
        if (inv is null) return;

        var defaultCents = row.BalanceCents < 0 ? row.BalanceCents : Math.Max(0, row.BalanceCents);
        var suggestNegEuro = row.BalanceCents < 0 ? row.BalanceCents / 100m : (decimal?)null;
        var existing = _repo.GetPaymentHistory(inv.Id)
            .Select(p => new Views.PaymentsEditWindow.PaymentEditRow
            {
                Id = p.Id,
                PaidDateIso = (p.PaidDateIso ?? "").Trim(),
                AmountEuroText = (p.AmountCents / 100m).ToString("0.00", CultureInfo.InvariantCulture),
                Method = Views.PaymentsEditWindow.PaymentEditRow.NormalizePaymentMethod(p.Method),
                Reference = (p.Reference ?? "").Trim()
            })
            .ToArray();

        var dlg = new Views.PaymentsEditWindow(inv, defaultCents / 100m, existing, maxPaymentTotalCents: null, suggestNegEuro)
        {
            Owner = Application.Current?.MainWindow
        };

        if (dlg.ShowDialog() != true) return;

        var payments = dlg.Rows
            .Select(r =>
            {
                var amt = 0m;
                decimal.TryParse((r.AmountEuroText ?? "").Trim().Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out amt);
                var cents = (int)Math.Round(amt * 100m, MidpointRounding.AwayFromZero);
                return ((r.PaidDateIso ?? "").Trim(), cents, (r.Method ?? "").Trim(), (r.Reference ?? "").Trim());
            })
            .Where(x => x.cents != 0)
            .ToList();

        _repo.ReplacePayments(inv.Id, payments);

        Refresh();
        OfferCreditNoteAfterTropPercu(inv.Id);
    }

    private void OfferCreditNoteAfterTropPercu(long patientInvoiceId)
    {
        var updated = Items.FirstOrDefault(i => i.InvoiceId == patientInvoiceId);
        if (updated is null) return;
        if (!string.Equals(updated.Kind, "patient", StringComparison.OrdinalIgnoreCase)) return;
        if (updated.BalanceCents >= 0) return;

        var tropEuro = Math.Abs(updated.BalanceCents) / 100m;
        var ask = ChoiceDialog.AskThree(
            "Trop-perçu",
            $"Un trop-perçu de {tropEuro:0.00} € est enregistré sur la facture {updated.InvoiceNo}.\n\n" +
            "Souhaitez-vous créer une note de crédit correspondante et générer le PDF ?",
            "Créer, PDF et imprimer",
            "Créer et PDF (sans imprimer)",
            "Pas maintenant");

        if (ask == ActionChoiceResult.Cancel)
            return;

        var tropCents = (int)Math.Round(tropEuro * 100m, MidpointRounding.AwayFromZero);
        if (tropCents <= 0) return;

        var ncId = _repo.CreateCreditNoteFromInvoice(patientInvoiceId, tropCents, DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        if (ncId <= 0)
        {
            MessageBox.Show("La note de crédit n'a pas pu être créée.", "PARAFacto", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!_repo.TryGenerateCreditNotePdf(ncId))
        {
            MessageBox.Show(
                "La note de crédit est enregistrée, mais le PDF n'a pas pu être généré (vérifiez les dossiers d'export).",
                "PARAFacto",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Refresh();
            return;
        }

        var credit = _repo.GetById(ncId);
        var root = WorkspacePaths.TryFindWorkspaceRoot();
        string? absPath = null;
        if (!string.IsNullOrWhiteSpace(root) && credit != null && !string.IsNullOrWhiteSpace(credit.RefDoc))
            absPath = WorkspacePaths.ResolvePath(root, credit.RefDoc);

        Refresh();

        if (ask == ActionChoiceResult.Primary && !string.IsNullOrEmpty(absPath) && File.Exists(absPath))
            TryPrintPdfCreditNote(absPath);
        else if (ask == ActionChoiceResult.Primary)
            MessageBox.Show(
                "La note de crédit et le PDF sont enregistrés, mais le fichier PDF n'a pas été retrouvé pour lancer l'impression.",
                "PARAFacto",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
    }

    private static void TryPrintPdfCreditNote(string pdfPath)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = pdfPath,
                Verb = "print",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"L'impression n'a pas pu être lancée.\n\n{ex.Message}",
                "PARAFacto",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void AddLoss()
    {
        var row = Selected;
        if (row is null) return;
        var inv = _repo.GetById(row.InvoiceId);
        if (inv is null) return;

        var remaining = Math.Max(0, row.BalanceCents);
        if (remaining <= 0)
        {
            MessageBox.Show("Cette facture n'a plus de solde restant.", "PARAFacto", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!ChoiceDialog.AskYesNo(
                "Déclarer une perte",
                $"Marquer comme PERTE le solde restant dû ({remaining / 100m:0.00} €) ?",
                "Déclarer la PERTE",
                "Annuler"))
            return;

        _repo.DeclareLoss(inv.Id, DateTime.Today.ToString("yyyy-MM-dd"), remaining, "Perte déclarée");
        Refresh();
    }

    private bool CanCreateCreditNote()
    {
        var s = Selected;
        return s is not null && !s.IsCreditNoteRow;
    }

    private void CreateCreditNote()
    {
        var row = Selected;
        if (row is null || row.IsCreditNoteRow)
            return;

        var list = _repo.ListCreditNoteCandidates();
        if (list.Count == 0)
        {
            MessageBox.Show("Aucune facture disponible pour une note de crédit.", "PARAFacto", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selectedId = row.InvoiceId;
        var bal = row.BalanceCents;
        var defaultEuro = bal < 0 ? Math.Abs(bal) / 100m : Math.Max(0, bal / 100m);
        var dlg = new Views.CreditNoteWindow(list, selectedId, defaultEuro)
        {
            Owner = Application.Current?.MainWindow
        };
        if (dlg.ShowDialog() != true) return;

        var invoiceId = dlg.SelectedInvoiceId;
        var amountCents = (int)Math.Round(dlg.AmountEuro * 100m, MidpointRounding.AwayFromZero);
        if (invoiceId <= 0 || amountCents <= 0)
        {
            MessageBox.Show("Données invalides.", "PARAFacto", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var ncId = _repo.CreateCreditNoteFromInvoice(invoiceId, amountCents, DateTime.Today.ToString("yyyy-MM-dd"));
        if (ncId <= 0)
        {
            MessageBox.Show("La note de crédit n'a pas pu être créée.", "PARAFacto", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!_repo.TryGenerateCreditNotePdf(ncId))
        {
            MessageBox.Show(
                "La note de crédit est enregistrée, mais le PDF n'a pas pu être généré.",
                "PARAFacto",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        Refresh();
    }

    private void CreateMutualRevision()
    {
        var row = Selected;
        if (row is null) return;
        var inv = _repo.GetById(row.InvoiceId);
        if (inv is null) return;
        if (!string.Equals(inv.Kind, "mutuelle", StringComparison.OrdinalIgnoreCase)) return;

        var dlg = new Views.MutualRevisionWindow(inv)
        {
            Owner = Application.Current?.MainWindow
        };
        if (dlg.ShowDialog() != true) return;

        var newCents = (int)Math.Round(dlg.NewTotalEuro * 100m, MidpointRounding.AwayFromZero);
        if (newCents <= 0)
        {
            MessageBox.Show("Montant invalide.", "PARAFacto", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var (pdfPath, pdfErr) = _repo.CreateMutualRevision(inv.Id, newCents, dlg.Reason, dlg.ReferenceDoc);
        Refresh();

        if (!string.IsNullOrEmpty(pdfErr))
        {
            MessageBox.Show(
                "La modification mutuelle est enregistrée, mais le PDF n'a pas pu être généré.\n\n" + pdfErr,
                "PARAFacto",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        if (!string.IsNullOrEmpty(pdfPath) && File.Exists(pdfPath))
        {
            var ask = ChoiceDialog.AskThree(
                "Modification mutuelle",
                "Le PDF de l'état modifié a été généré. Que souhaitez-vous faire ?",
                "Imprimer",
                "Ouvrir le fichier",
                "Fermer");
            if (ask == ActionChoiceResult.Primary)
                TryPrintPdfCreditNote(pdfPath);
            else if (ask == ActionChoiceResult.Secondary)
            {
                try
                {
                    Process.Start(new ProcessStartInfo { FileName = pdfPath, UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "PARAFacto", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }
    }

    private void DeleteModifiedMutualInvoice()
    {
        var row = Selected;
        if (row is null) return;
        if (!RowLooksLikeDeletableMutualModified(row)) return;

        if (!ChoiceDialog.AskYesNo(
                "Supprimer la facture modifiée",
                $"Supprimer définitivement la facture mutuelle modifiée ?\n\n{row.InvoiceNo}\n{row.Recipient}\n{row.TotalEuro}\n\n" +
                "La facture d'origine (remplacée) sera réactivée si aucune autre copie modifiée ne reste liée à celle-ci.",
                "Supprimer",
                "Annuler"))
            return;

        if (!_repo.TryDeleteModifiedMutualInvoice(row.InvoiceId))
        {
            MessageBox.Show(
                "La suppression n'a pas pu être effectuée (facture introuvable ou non modifiable).",
                "PARAFacto",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        Refresh();
    }


    private bool CanDeleteMonthInvoices()
        => !string.IsNullOrWhiteSpace(ResolveDeletePeriod()) && _repo.HasAnyInvoicesForPeriod(ResolveDeletePeriod()!);

    private string? ResolveDeletePeriod()
    {
        if (_periodKind == "Année / Mois")
        {
            var yy = ParsePeriodInt(PeriodYear, DateTime.Today.Year);
            return $"{yy:0000}-{Math.Clamp(PeriodMonth, 1, 12):00}";
        }
        // Même logique que la suppression en base : mois de facturation = préfixe MM-YYYY du n° (pas date_iso / émission).
        if (Selected is not null)
            return TryGetPeriodYyyyMmFromInvoiceNo(Selected.InvoiceNo);
        return null;
    }

    /// <summary>Extrait <c>YYYY-MM</c> pour <see cref="InvoiceRepo.DeleteInvoicesForPeriod"/> depuis un n° du type <c>04-2026-12</c>, <c>NC-04-2026-01</c>, <c>ETAT-04-2026-…</c>.</summary>
    private static string? TryGetPeriodYyyyMmFromInvoiceNo(string? invoiceNo)
    {
        var s = (invoiceNo ?? "").Trim();
        if (s.Length == 0) return null;
        if (s.StartsWith("NC-", StringComparison.OrdinalIgnoreCase))
            s = s[3..].Trim();
        if (s.StartsWith("ETAT-", StringComparison.OrdinalIgnoreCase))
            s = s[5..].Trim();
        var m = Regex.Match(s, @"^(\d{2})-(\d{4})(?:$|-)");
        if (!m.Success) return null;
        if (!int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var month)
            || month is < 1 or > 12)
            return null;
        if (!int.TryParse(m.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year)
            || year < 1970 || year > 2100)
            return null;
        return $"{year:0000}-{month:00}";
    }

    private void DeleteMonthInvoices()
    {
        var period = ResolveDeletePeriod();
        if (string.IsNullOrWhiteSpace(period))
        {
            MessageBox.Show(
                "Choisissez « Année / Mois » avec un mois, ou sélectionnez une ligne dont le numéro commence par MM-YYYY (ex. 04-2026-…).\n\n" +
                "La suppression utilise le mois de facturation du numéro, pas la date d’émission.",
                "PARAFacto",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (!_repo.HasAnyInvoicesForPeriod(period))
        {
            MessageBox.Show($"Aucune facture à supprimer pour {period}.", "PARAFacto", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!ChoiceDialog.AskYesNo(
                "Suppression des factures du mois",
                $"Supprimer toutes les factures du mois {period} ?\n\nCela réouvrira la modification des journaliers de ce mois.",
                "Supprimer",
                "Annuler"))
            return;

        var deleted = _repo.DeleteInvoicesForPeriod(period);
        MessageBox.Show($"{deleted} facture(s) supprimée(s) pour {period}.", "PARAFacto", MessageBoxButton.OK, MessageBoxImage.Information);
        Refresh();
    }

    private void OpenWorkspaceFolder()
    {
        var root = WorkspacePaths.TryFindWorkspaceRoot();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            MessageBox.Show("Workspace introuvable.", "PARAFacto", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Process.Start(new ProcessStartInfo(root) { UseShellExecute = true });
    }

    private void OpenSelectedPdf()
    {
        var row = Selected;
        if (row is null) return;

        var root = WorkspacePaths.TryFindWorkspaceRoot();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            MessageBox.Show("Workspace introuvable.", "PARAFacto", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 1) ref_doc existant
        if (!string.IsNullOrWhiteSpace(row.RefDoc))
        {
            var p = WorkspacePaths.ResolvePath(root, row.RefDoc);
            if (File.Exists(p))
            {
                Process.Start(new ProcessStartInfo(p) { UseShellExecute = true });
                return;
            }
        }

        // 2) recherche par invoice_no dans l'arborescence attendue
        var found = TryFindInvoicePdf(root, row);
        if (string.IsNullOrWhiteSpace(found) || !File.Exists(found))
        {
            MessageBox.Show($"PDF introuvable pour {row.InvoiceNo}.", "PARAFacto", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var rel = WorkspacePaths.MakeRelativeToRoot(root, found);
        _repo.UpdateRefDoc(row.InvoiceNo, rel);
        row.RefDoc = rel;
        Raise(nameof(Items));

        Process.Start(new ProcessStartInfo(found) { UseShellExecute = true });
    }

    public void HandleInvoiceRowDoubleClick(InvoiceRow row)
    {
        if (row is null) return;
        Selected = row;

        // Le double-clic est réservé au rappel de paiement des impayées et partielles.
        // L'ouverture PDF reste uniquement via le bouton icône "📄" de la ligne.
        if (!string.Equals(row.StatusLower, "unpaid", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(row.StatusLower, "partial", StringComparison.OrdinalIgnoreCase)
            && !row.IsLossWithPartialPayment)
            return;

        if (row.PatientId <= 0)
        {
            MessageBox.Show(
                "Cette facture impayée n'est pas liée à un patient. Impossible de préparer un rappel (e-mail / WhatsApp).",
                "Factures — rappel",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var patient = _patientRepo.GetById(row.PatientId);
        if (patient is null)
        {
            MessageBox.Show(
                "Fiche patient introuvable. Impossible de préparer un rappel.",
                "Factures — rappel",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var email = (patient.Email ?? "").Trim();
        var phoneRaw = (patient.Phone ?? "").Trim();
        var phoneWa = InternationalPhoneFormatter.TryFormatForWhatsApp(phoneRaw);
        var patientLabel = string.IsNullOrWhiteSpace(patient.Display) ? (row.Recipient ?? "patient") : patient.Display;

        var action = ChoiceDialog.AskThree(
            "Rappel de paiement",
            $"Facture impayée : {row.InvoiceNo}\nPatient : {patientLabel}\n\n" +
            $"E-mail : {(string.IsNullOrWhiteSpace(email) ? "(non renseigné)" : email)}\n" +
            $"Téléphone : {(string.IsNullOrWhiteSpace(phoneRaw) ? "(non renseigné)" : InternationalPhoneFormatter.FormatForDisplay(phoneRaw))}\n\n" +
            "Choisissez le canal de rappel :",
            "Envoyer un e-mail",
            "Envoyer WhatsApp",
            "Annuler");

        if (action == ActionChoiceResult.Cancel)
            return;

        var body = BuildReminderMessage(row, ResolveSenderSignatureName());

        if (action == ActionChoiceResult.Primary)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                MessageBox.Show("Aucune adresse e-mail dans la fiche patient.", "Factures — rappel", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var subject = $"Rappel de paiement — facture N° {row.InvoiceNo}";
            var gmailUrl =
                $"https://mail.google.com/mail/?view=cm&fs=1&to={Uri.EscapeDataString(email)}" +
                $"&su={Uri.EscapeDataString(subject)}&body={Uri.EscapeDataString(body)}";
            var mailto = $"mailto:{Uri.EscapeDataString(email)}?subject={Uri.EscapeDataString(subject)}&body={Uri.EscapeDataString(body)}";
            var emailOpened = false;
            try
            {
                // 1) Priorité Gmail web.
                Process.Start(new ProcessStartInfo(gmailUrl) { UseShellExecute = true });
                emailOpened = true;
            }
            catch (Exception ex)
            {
                // 2) Fallback Outlook (si installé).
                if (TryOpenOutlookCompose(email, subject, body, out _))
                    emailOpened = true;
                else
                {
                    try
                    {
                        // 3) Fallback client mail par défaut du PC (Thunderbird/autre).
                        Process.Start(new ProcessStartInfo(mailto) { UseShellExecute = true });
                        emailOpened = true;
                    }
                    catch
                    {
                        MessageBox.Show($"Impossible d'ouvrir Gmail, Outlook, ni un autre client e-mail.\n\n{ex.Message}", "Factures — rappel", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
            }

            if (emailOpened)
                AppendReminderSentToInvoiceComment(row, "e-mail");
            return;
        }

        if (string.IsNullOrWhiteSpace(phoneWa))
        {
            MessageBox.Show("Aucun numéro de téléphone exploitable pour WhatsApp dans la fiche patient.", "Factures — rappel", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var waUrl = $"https://wa.me/{phoneWa}?text={Uri.EscapeDataString(body)}";
        try
        {
            Process.Start(new ProcessStartInfo(waUrl) { UseShellExecute = true });
            AppendReminderSentToInvoiceComment(row, "WhatsApp");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Impossible d'ouvrir WhatsApp.\n\n{ex.Message}", "Factures — rappel", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Ajoute une trace d’envoi de rappel dans <c>user_comment</c> (colonne Commentaires + panneau détail).</summary>
    private void AppendReminderSentToInvoiceComment(InvoiceRow row, string channelLabel)
    {
        if (row.InvoiceId <= 0) return;
        var todayFr = DateTime.Today.ToString("dd/MM/yyyy", CultureInfo.GetCultureInfo("fr-BE"));
        var line = $"Rappel envoyé par {channelLabel} le {todayFr}.";
        var existing = (row.UserComment ?? "").TrimEnd();
        var combined = string.IsNullOrEmpty(existing) ? line : existing + Environment.NewLine + line;
        row.UserComment = combined;
        SaveInvoiceComment(row.InvoiceId, combined);
        Raise(nameof(SelectedCommentText));
        Raise(nameof(HasSelectedComment));
        try
        {
            if (Items is not null)
                CollectionViewSource.GetDefaultView(Items)?.Refresh();
        }
        catch
        {
            /* hors contexte WPF */
        }
    }

    private string ResolveSenderSignatureName()
        => ProfessionalProfileStore.Load().ReminderSenderDisplayName;

    private static string BuildReminderMessage(InvoiceRow row, string senderDisplayName)
    {
        var dateLabel = DateTime.TryParseExact(
            (row.IssuedDate ?? "").Trim(),
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var issued)
            ? issued.ToString("dd/MM/yyyy", CultureInfo.GetCultureInfo("fr-BE"))
            : (row.IssuedDate ?? "").Trim();

        return
            "Madame, Monsieur,\n\n" +
            $"Nous vous envoyons ce message à titre de rappel de paiement pour la facture N° {row.InvoiceNo}" +
            (string.IsNullOrWhiteSpace(dateLabel) ? "" : $" du {dateLabel}") + ".\n" +
            $"Le montant restant dû est de {row.BalanceEuro}.\n\n" +
            "Nous vous remercions d'effectuer le paiement dès que possible.\n\n" +
            "Bien à vous,\n" +
            senderDisplayName;
    }

    private static bool TryOpenOutlookCompose(string to, string subject, string body, out string error)
    {
        error = "";
        Type? outlookType = null;
        object? outlook = null;
        object? mail = null;
        try
        {
            outlookType = Type.GetTypeFromProgID("Outlook.Application");
            if (outlookType is null)
            {
                error = OutlookEmailService.OutlookComProgIdMissingHint;
                return false;
            }

            outlook = Activator.CreateInstance(outlookType);
            if (outlook is null)
            {
                error = "Impossible de démarrer Outlook.";
                return false;
            }

            // 0 = olMailItem
            mail = outlookType.InvokeMember("CreateItem",
                System.Reflection.BindingFlags.InvokeMethod,
                null,
                outlook,
                new object[] { 0 });
            if (mail is null)
            {
                error = "Impossible de créer un e-mail Outlook.";
                return false;
            }

            mail.GetType().InvokeMember("To", System.Reflection.BindingFlags.SetProperty, null, mail, new object[] { to });
            mail.GetType().InvokeMember("Subject", System.Reflection.BindingFlags.SetProperty, null, mail, new object[] { subject });
            mail.GetType().InvokeMember("Body", System.Reflection.BindingFlags.SetProperty, null, mail, new object[] { body });
            // Display() pour laisser l'utilisateur relire/modifier avant envoi.
            mail.GetType().InvokeMember("Display", System.Reflection.BindingFlags.InvokeMethod, null, mail, Array.Empty<object>());
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            try
            {
                if (mail != null && Marshal.IsComObject(mail)) Marshal.FinalReleaseComObject(mail);
            }
            catch { }
            try
            {
                if (outlook != null && Marshal.IsComObject(outlook)) Marshal.FinalReleaseComObject(outlook);
            }
            catch { }
        }
    }

    private static string? TryFindInvoicePdf(string root, InvoiceRow row)
    {
        var monthFolder = MonthFolderFromInvoiceNo(row.InvoiceNo);
        if (string.IsNullOrWhiteSpace(monthFolder))
            monthFolder = MonthFolderFromIso(row.IssuedDate);

        if (string.Equals(row.Kind, "mutuelle", StringComparison.OrdinalIgnoreCase))
            return TryFindMutualInvoicePdf(root, row, monthFolder);

        var baseDirs = new List<string>();
        baseDirs.Add(Path.Combine(root, "FACTURES MENSUELLES PATIENTS", monthFolder));
        if (string.Equals(row.Kind, "credit_note", StringComparison.OrdinalIgnoreCase))
            baseDirs.Add(Path.Combine(root, "FACTURES MENSUELLES MUTUELLES", monthFolder));

        var dirs = new List<string>();
        foreach (var b in baseDirs)
        {
            dirs.Add(b);
            dirs.Add(Path.Combine(b, "CASH"));
            dirs.Add(Path.Combine(b, "FACTURES ACQUITTEES"));
            dirs.Add(Path.Combine(b, "NC"));
        }

        var ncStem = string.Equals(row.Kind, "credit_note", StringComparison.OrdinalIgnoreCase)
            ? InvoicePdfService.FormatCreditNoteNoForPdfHeader(row.InvoiceNo)
            : row.InvoiceNo;
        if (string.IsNullOrWhiteSpace(ncStem))
            ncStem = row.InvoiceNo;

        var patterns = new[]
        {
            $"Facture_{row.InvoiceNo}_*.pdf",
            $"FACTURE_{row.InvoiceNo}_*.pdf",
            $"NC_{ncStem}_*.pdf",
            $"NC_{row.InvoiceNo}_*.pdf",
            $"NoteDeCredit_{row.InvoiceNo}_*.pdf"
        };

        foreach (var dir in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(dir)) continue;

            foreach (var pat in patterns)
            {
                var hits = Directory.GetFiles(dir, pat, SearchOption.TopDirectoryOnly);
                if (hits.Length > 0) return hits[0];
            }
        }

        // fallback: scan large
        var deepRoots = new[]
        {
            Path.Combine(root, "FACTURES MENSUELLES PATIENTS"),
            Path.Combine(root, "FACTURES MENSUELLES MUTUELLES"),
        };

        foreach (var dr in deepRoots)
        {
            if (!Directory.Exists(dr)) continue;
            var hit = Directory.EnumerateFiles(dr, "*.pdf", SearchOption.AllDirectories)
                .FirstOrDefault(f =>
                {
                    var fn = Path.GetFileName(f);
                    return fn.Contains(row.InvoiceNo, StringComparison.OrdinalIgnoreCase)
                           || (!string.IsNullOrWhiteSpace(ncStem) && fn.Contains(ncStem, StringComparison.OrdinalIgnoreCase));
                });
            if (!string.IsNullOrWhiteSpace(hit)) return hit;
        }

        return null;
    }

    private static string? TryFindMutualInvoicePdf(string root, InvoiceRow row, string monthFolder)
    {
        var mutuelle = (row.Recipient ?? "").Trim();
        var isMod = row.InvoiceNo.Contains("-MOD", StringComparison.OrdinalIgnoreCase);

        // Même dossier pour original et modifié : FACTURES MENSUELLES MUTUELLES\MM-YYYY\<Mutuelle>
        var baseMut = Path.Combine(root, "FACTURES MENSUELLES MUTUELLES", monthFolder);
        if (!Directory.Exists(baseMut)) return null;

        // IMPORTANT:
        // - La génération PDF utilise WorkspacePaths.MakeSafeFolderName(mutualName)
        //   (remplace seulement les caractères interdits pour les noms de dossiers, sans upper ni substitution systématique des espaces)
        // - Ici, on essaye plusieurs variantes pour retrouver le dossier.
        foreach (var dir in GetMutualFolderCandidates(mutuelle).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var mutuelleDir = Path.Combine(baseMut, dir);
            if (!Directory.Exists(mutuelleDir)) continue;

            if (isMod)
            {
                // 1) Essayer un PDF spécifique -MOD
                var modPdf = Directory.GetFiles(mutuelleDir, "*-MOD.pdf", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(modPdf)) return modPdf;

                // 2) Sinon, retomber sur l'état récapitulatif normal (même dossier)
                var recapFallback = Directory.GetFiles(mutuelleDir, "EtatRecap_*.pdf", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault(f => !Path.GetFileName(f).Contains("-MOD"));
                if (!string.IsNullOrWhiteSpace(recapFallback)) return recapFallback;
            }
            else
            {
                var recap = Directory.GetFiles(mutuelleDir, "EtatRecap_*.pdf", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault(f => !Path.GetFileName(f).Contains("-MOD"));
                if (!string.IsNullOrWhiteSpace(recap)) return recap;
            }
        }

        return null;
    }

    private static IEnumerable<string> GetMutualFolderCandidates(string mutualName)
    {
        mutualName ??= "";
        var trimmed = mutualName.Trim();

        // 1) brut (trim)
        yield return trimmed;

        // 2) espaces → underscore (heuristique, certains export/anciens dossiers)
        yield return trimmed.Replace(' ', '_');

        // 3) même logique que WorkspacePaths.MakeSafeFolderName: remplace les caractères interdits
        yield return MakeSafeFolderName(trimmed);
        yield return MakeSafeFolderName(trimmed.Replace(' ', '_'));
    }

    private static string MakeSafeFolderName(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "SANS_NOM";
        foreach (var ch in Path.GetInvalidFileNameChars())
            s = s.Replace(ch, '_');
        return s.Trim();
    }

    
    private static string MonthFolderFromInvoiceNo(string invoiceNo)
    {
        // Expected invoice_no formats:
        // - Patients: "MM-YYYY-XX" (e.g., "01-2026-14")
        // - Mutuelles: "ETAT-MM-YYYY-MUTUAL" (e.g., "ETAT-03-2026-SOLIDARIS")
        invoiceNo = (invoiceNo ?? "").Trim();

        // Notes de crédit : "NC-MM-YYYY-NN" → dossier du mois MM-YYYY
        if (invoiceNo.Length >= 12 && invoiceNo.StartsWith("NC-", StringComparison.OrdinalIgnoreCase))
        {
            var inner = invoiceNo.Substring(3);
            if (inner.Length >= 7 && char.IsDigit(inner[0]) && char.IsDigit(inner[1]) && inner[2] == '-' &&
                char.IsDigit(inner[3]) && char.IsDigit(inner[4]) && char.IsDigit(inner[5]) && char.IsDigit(inner[6]))
                return inner[..7];
        }

        // Mutuelles: extract "MM-YYYY" from "ETAT-MM-YYYY-..."
        if (invoiceNo.Length >= 12 && invoiceNo.StartsWith("ETAT-", StringComparison.OrdinalIgnoreCase))
        {
            // ETAT- + 7 chars => 12 minimum ("ETAT-" length 5 + "MM-YYYY" length 7)
            var mmYYYY = invoiceNo.Substring(5, 7);
            if (mmYYYY.Length == 7 &&
                char.IsDigit(mmYYYY[0]) && char.IsDigit(mmYYYY[1]) && mmYYYY[2] == '-' &&
                char.IsDigit(mmYYYY[3]) && char.IsDigit(mmYYYY[4]) && char.IsDigit(mmYYYY[5]) && char.IsDigit(mmYYYY[6]))
            {
                return mmYYYY;
            }
        }

        // Patients: "MM-YYYY-..."
        if (invoiceNo.Length >= 7 && char.IsDigit(invoiceNo[0]) && char.IsDigit(invoiceNo[1]) && invoiceNo[2] == '-' &&
            char.IsDigit(invoiceNo[3]) && char.IsDigit(invoiceNo[4]) && char.IsDigit(invoiceNo[5]) && char.IsDigit(invoiceNo[6]))
        {
            return invoiceNo.Substring(0, 7); // "MM-YYYY"
        }

        return "";
    }

private static string MonthFolderFromIso(string iso)
    {
        if (DateTime.TryParseExact((iso ?? "").Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d.ToString("MM-yyyy", CultureInfo.InvariantCulture).Replace("-", "-"); // keep "MM-yyyy"
        return DateTime.Today.ToString("MM-yyyy", CultureInfo.InvariantCulture);
    }

    private void RelinkPatientsFromWorkspace()
    {
        var root = WorkspacePaths.TryFindWorkspaceRoot();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            MessageBox.Show("Workspace introuvable.\nChoisis d'abord le workspace.", "PARAFacto", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Index PDFs once: parse invoice_no directly from filename "Facture_<MM-YYYY-XX>_..."
        var pdfIndex = BuildPdfIndex(root);

        int updatedRefDoc = 0, updatedRecipient = 0, notFound = 0;
        var notFoundNos = new List<string>();

        var candidates = _repo.GetPatientInvoicesNeedingLink();
        foreach (var inv in candidates)
        {
            var invoiceNo = (inv.InvoiceNo ?? "").Trim();
            if (invoiceNo.Length == 0)
            {
                notFound++;
                notFoundNos.Add("(invoice_no vide)");
                continue;
            }

            if (!pdfIndex.TryGetValue(invoiceNo, out var pdfPath) || string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
            {
                notFound++;
                notFoundNos.Add(invoiceNo);
                continue;
            }

            // ref_doc (relative)
            var rel = WorkspacePaths.MakeRelativeToRoot(root, pdfPath);
            if (string.IsNullOrWhiteSpace(inv.RefDoc) || !string.Equals(inv.RefDoc, rel, StringComparison.OrdinalIgnoreCase))
            {
                _repo.UpdateRefDoc(invoiceNo, rel);
                updatedRefDoc++;
            }

            // recipient: from filename AFTER invoiceNo underscore, before .pdf
            var recipient = ExtractRecipientFromPdfFileName(pdfPath, invoiceNo);
            if (!string.IsNullOrWhiteSpace(recipient))
            {
                _repo.UpdateRecipient(invoiceNo, recipient);
                updatedRecipient++;
            }
        }

        Refresh();

        // Persist a tiny debug list for support/diagnostic
        try
        {
            if (notFoundNos.Count > 0)
            {
                var outPath = Path.Combine(root, "relink_not_found_invoices.txt");
                File.WriteAllLines(outPath, notFoundNos.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x));
            }
        }
        catch
        {
            // ignore
        }

        var distinctNotFound = notFoundNos
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x)
            .ToList();

        var preview = distinctNotFound.Count == 0
            ? ""
            : "\n\nNon trouvés (aperçu) :\n" + string.Join("\n", distinctNotFound.Take(25)) +
              (distinctNotFound.Count > 25 ? $"\n... (+{distinctNotFound.Count - 25} autres)" : "");

        MessageBox.Show(
            $"Index terminé.\n\n" +
            $"ref_doc mis à jour : {updatedRefDoc}\n" +
            $"destinataire mis à jour : {updatedRecipient}\n" +
            $"Non trouvés : {notFound}" +
            preview +
            (distinctNotFound.Count > 0 ? "\n\n(La liste complète a été enregistrée dans relink_not_found_invoices.txt à la racine du workspace.)" : ""),
            "PARAFacto",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void ExecuteRelinkPatientsWithSecurity()
    {
        if (!_relinkUnlocked)
        {
            var ask = new SecurityCodeWindow
            {
                Owner = Application.Current?.MainWindow
            };

            if (ask.ShowDialog() != true)
                return;

            if (!string.Equals((ask.EnteredCode ?? "").Trim(), RelinkSecurityCode, StringComparison.Ordinal))
            {
                MessageBox.Show("Code de sécurité invalide.", "PARAFacto", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _relinkUnlocked = true;
            MessageBox.Show("Indexation PDF déverrouillée pour cette session.", "PARAFacto", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        RelinkPatientsFromWorkspace();
    }

    private static string ExtractRecipientFromPdfFileName(string pdfPath, string invoiceNo)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(pdfPath) ?? "";
        // expected: Facture_<invoiceNo>_<recipient>
        var prefix = $"Facture_{invoiceNo}_";
        var idx = name.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return "";
        var rec = name.Substring(idx + prefix.Length);
        rec = rec.Replace('_', ' ').Trim();
        return rec;
    }

private static Dictionary<string, string> BuildPdfIndex(string workspaceRoot)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // We only need patient invoices PDFs for relink
        var patientRoot = Path.Combine(workspaceRoot, "FACTURES MENSUELLES PATIENTS");
        if (!Directory.Exists(patientRoot)) return map;

        // Match: Facture_03-2025-01_....pdf  => invoiceNo = 03-2025-01
        var rx = new System.Text.RegularExpressions.Regex(
            @"^Facture_(\d{2}-\d{4}-\d+)_.*\.pdf$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        foreach (var file in Directory.EnumerateFiles(patientRoot, "*.pdf", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            var m = rx.Match(name);
            if (!m.Success) continue;

            var invoiceNo = m.Groups[1].Value.Trim();
            if (invoiceNo.Length == 0) continue;

            // First wins; keep stable mapping
            if (!map.ContainsKey(invoiceNo))
                map[invoiceNo] = file;
        }

        return map;
    }
private static string ExtractHintFromPdfFileName(string pdfPath, string invoiceNo)
    {
        var name = Path.GetFileNameWithoutExtension(pdfPath) ?? "";
        // remove leading "Facture_" and invoice number
        name = name.Replace("FACTURE_", "Facture_", StringComparison.OrdinalIgnoreCase);

        // common patterns: Facture_<no>_<desc>
        var prefix = $"Facture_{invoiceNo}_";
        var idx = name.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0) name = name.Substring(idx + prefix.Length);

        // also allow "Facture_<no>" without desc
        if (name.Equals($"Facture_{invoiceNo}", StringComparison.OrdinalIgnoreCase)) return "";

        name = name.Replace('_', ' ').Trim();

        // cleanup common french phrases
        var lower = name.ToLowerInvariant();
        foreach (var s in new[]
                 {
                     "aux parents de", "aux parents d", "aux parents", "parents de", "parent de",
                     "au patient", "a la patiente", "à la patiente", "a l'attention de", "à l'attention de",
                     "monsieur", "madame", "mme", "mr", "m."
                 })
        {
            lower = lower.Replace(s, " ");
        }

        // keep original length-ish but using lowered cleaned
        var cleaned = NormalizeName(lower);
        return cleaned.Trim();
    }

    private static List<PatientNorm> MatchPatients(string hintNorm, List<PatientNorm> patients)
    {
        var res = new List<PatientNorm>();

        foreach (var p in patients)
        {
            if (!string.IsNullOrWhiteSpace(p.ReferendNorm) && hintNorm.Contains(p.ReferendNorm))
            {
                res.Add(p);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(p.FirstLastNorm) && hintNorm.Contains(p.FirstLastNorm))
            {
                res.Add(p);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(p.LastFirstNorm) && hintNorm.Contains(p.LastFirstNorm))
            {
                res.Add(p);
                continue;
            }
        }

        // de-dup by Id
        return res.GroupBy(x => x.Id).Select(g => g.First()).ToList();
    }

    private static string NormalizeName(string s)
    {
        s = (s ?? "").Trim().ToLowerInvariant();
        if (s.Length == 0) return "";

        // remove accents
        var formD = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in formD)
        {
            var uc = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (uc != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }

        var t = sb.ToString();

        // keep letters/digits/spaces, remove punctuation
        var sb2 = new StringBuilder();
        foreach (var ch in t)
        {
            if (char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch))
                sb2.Append(ch);
            else
                sb2.Append(' ');
        }

        // collapse spaces
        return string.Join(" ", sb2.ToString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
    }

    private sealed record PatientNorm(long Id, string Code3, string First, string Last, string Referend)
    {
        public string ReferendNorm { get; } = NormalizeName(Referend);
        public string FirstLastNorm { get; } = NormalizeName($"{First} {Last}");
        public string LastFirstNorm { get; } = NormalizeName($"{Last} {First}");
    }

    public void SaveInvoiceComment(long invoiceId, string? comment)
    {
        if (invoiceId <= 0) return;
        _repo.UpdateUserComment(invoiceId, comment);
    }
}

public sealed class InvoiceRow
{
    public long InvoiceId { get; set; }
    public string InvoiceNo { get; set; } = "";
    public string InvoiceNoDisplay
    {
        get
        {
            var no = (InvoiceNo ?? "").Trim();
            if (string.Equals(Kind, "credit_note", StringComparison.OrdinalIgnoreCase))
            {
                // Liste factures : garder le préfixe NC- (le PDF peut afficher sans préfixe via FormatCreditNoteNoForPdfHeader).
                if (no.Length == 0) return "";
                return no.StartsWith("NC-", StringComparison.OrdinalIgnoreCase) ? no : "NC-" + no;
            }
            if (!no.StartsWith("ETAT-", StringComparison.OrdinalIgnoreCase))
                return no;

            // Format attendu : ETAT-MM-YYYY-<MUTUELLE>[-MOD]
            // Affichage compact demandé : ETAT-MM-YYYY (et ETAT-MM-YYYY-MOD si modifiée)
            if (no.Length >= 12)
            {
                var baseNo = no.Substring(0, 12); // "ETAT-03-2026"
                if (no.EndsWith("-MOD", StringComparison.OrdinalIgnoreCase))
                    return baseNo + "-MOD";
                return baseNo;
            }
            return no;
        }
    }
    public string IssuedDate { get; set; } = "";
    /// <summary>Date du dernier paiement (affichage liste factures). Vide si aucun paiement enregistré.</summary>
    public string PaymentDateDisplay { get; set; } = "";
    public string Kind { get; set; } = "";
    public long PatientId { get; set; }
    public string Recipient { get; set; } = "";
    public string Status { get; set; } = "";

    /// <summary>Statut brut en base (minuscules).</summary>
    public string StatusLower => (Status ?? "").Trim().ToLowerInvariant();

    /// <summary>Perte déclarée mais encaissement partiel : affiché comme partiel, pas perte totale.</summary>
    public bool IsLossWithPartialPayment =>
        StatusLower == "loss"
        && !IsCreditNoteRow
        && LossMagnitudeCents > 0
        && PaidDisplayCents > 0
        && PaidDisplayCents < TotalCents;

    /// <summary>Pour la couleur de ligne : perte+partiel → même fond que « partial ».</summary>
    public string StatusLowerForRowStyle => IsLossWithPartialPayment ? "partial" : StatusLower;

    public string StatusFr
    {
        get
        {
            if (IsLossWithPartialPayment)
                return "PARTIELLE";
            return StatusLower switch
            {
                "paid" => "PAYEE",
                "acquittee" => "ACQUITTEE",
                "unpaid" => "IMPAYEE",
                "partial" => "PARTIELLE",
                "loss" => "PERTE",
                "modified" => "MODIFIEE",
                "superseded" => "REMPLACEE",
                _ => (Status ?? "").ToUpperInvariant()
            };
        }
    }
    public int TotalCents { get; set; }
    public int PaidCents { get; set; }
    public int LossCents { get; set; }

    /// <summary>Montant des pertes en positif (en base, <c>losses.amount_cents</c> est négatif).</summary>
    public int LossMagnitudeCents => Math.Abs(LossCents);

    public int NcCents { get; set; }
    public long RefInvoiceId { get; set; }
    public string Reason { get; set; } = "";
    public string RefDoc { get; set; } = "";
    public string UserComment { get; set; } = "";

    private bool IsReplaced => string.Equals((Status ?? "").Trim(), "superseded", StringComparison.OrdinalIgnoreCase)
                            || string.Equals((Status ?? "").Trim(), "REMPLACEE", StringComparison.OrdinalIgnoreCase);

    /// <summary>Montant réellement encaissé (somme des paiements enregistrés).</summary>
    public int PaidDisplayCents => PaidCents;

    public bool IsCreditNoteRow => string.Equals(Kind, "credit_note", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Facture patient / mutuelle : <c>raw = Total − payé − |perte|</c>.
    /// Si <c>raw &gt; 0</c> (reste dû), les NC diminuent ce reste : <c>raw − NC</c>.
    /// Si <c>raw &lt; 0</c> (trop-perçu), la NC enregistrée réduit ce trop : <c>min(0, raw + NC)</c>.
    /// Si <c>raw == 0</c>, la facture est soldée au net des paiements ; on n’applique pas la NC au solde
    /// (sinon double comptage quand un ajustement négatif a déjà ramené le net payé au total).
    /// Note de crédit : montant NC − remboursements enregistrés − |perte|.
    /// </summary>
    public int BalanceCents
    {
        get
        {
            if (IsCreditNoteRow)
                return Math.Abs(TotalCents) - PaidDisplayCents - LossMagnitudeCents;

            var raw = TotalCents - PaidDisplayCents - LossMagnitudeCents;
            if (raw < 0)
                return Math.Min(0, raw + NcCents);
            if (raw == 0)
                return 0;
            return raw - NcCents;
        }
    }

    /// <summary>Fond d’alerte sur la colonne Solde (trop-perçu ou trop remboursé sur NC).</summary>
    public bool BalanceCellHighlight => !IsCreditNoteRow && BalanceCents < 0;

    /// <summary>Fond sur la colonne Perte lorsqu’un montant de perte est présent (y compris avec ligne affichée en PARTIELLE).</summary>
    public bool LossCellHighlight => !HideMoneyColumnsForGrid && LossMagnitudeCents > 0;

    /// <summary>Ligne NC : montants vides dans la grille (évitent le double comptage visuel avec la facture liée).</summary>
    private bool HideMoneyColumnsForGrid => IsReplaced || IsCreditNoteRow;

    /// <summary>Grille : pas de « 0,00 € » pour les montants nuls (lisibilité).</summary>
    private static string EuroCell(int cents)
    {
        if (cents == 0) return "";
        return (cents / 100.0).ToString("0.00") + " €";
    }

    public string TotalEuro => HideMoneyColumnsForGrid ? "" : EuroCell(TotalCents);
    public string PaidEuro => HideMoneyColumnsForGrid ? "" : EuroCell(PaidDisplayCents);
    public string NcEuro => HideMoneyColumnsForGrid ? "" : EuroCell(NcCents);
    public string LossEuro => HideMoneyColumnsForGrid ? "" : EuroCell(LossMagnitudeCents);
    public string BalanceEuro => HideMoneyColumnsForGrid ? "" : EuroCell(BalanceCents);
}

public sealed class InvoiceStats
{
    public int PayeCents { get; set; }
    public int PartielCents { get; set; }
    public int TotalRecuCents { get; set; }
    public int ImpayeCents { get; set; }
    public int EnAttenteCents { get; set; }
    public int PertesCents { get; set; }
    public int NcCents { get; set; }

    public string PayeEuro => (PayeCents / 100.0).ToString("0.00", CultureInfo.InvariantCulture) + " €";
    public string PartielEuro => (PartielCents / 100.0).ToString("0.00", CultureInfo.InvariantCulture) + " €";
    public string TotalRecuEuro => (TotalRecuCents / 100.0).ToString("0.00", CultureInfo.InvariantCulture) + " €";
    public string ImpayeEuro => (ImpayeCents / 100.0).ToString("0.00", CultureInfo.InvariantCulture) + " €";
    public string EnAttenteEuro => (EnAttenteCents / 100.0).ToString("0.00", CultureInfo.InvariantCulture) + " €";
    public string PertesEuro => (PertesCents / 100.0).ToString("0.00", CultureInfo.InvariantCulture) + " €";
    public string NcEuro => (NcCents / 100.0).ToString("0.00", CultureInfo.InvariantCulture) + " €";
}
