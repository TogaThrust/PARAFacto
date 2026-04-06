using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using PARAFactoNative.Models;
using PARAFactoNative.Services;

namespace PARAFactoNative.ViewModels;

public sealed class SeancesViewModel : NotifyBase
{
    private readonly SeanceRepo _repo = new();
    private readonly SeanceService _service = new();

    public ObservableCollection<SeanceRow> Items { get; } = new();

    public string[] ModeOptions { get; } = new[] { "Jour", "Mois", "Année" };

    /// <summary>Années pour la liste déroulante (année courante - 10 à année courante + 1).</summary>
    public string[] AvailableYears { get; } = Enumerable.Range(DateTime.Today.Year - 10, 12)
        .Select(y => y.ToString(CultureInfo.InvariantCulture)).ToArray();
    /// <summary>Mois 1 à 12 pour la liste déroulante.</summary>
    public string[] AvailableMonths { get; } = Enumerable.Range(1, 12).Select(m => m.ToString(CultureInfo.InvariantCulture)).ToArray();

    private string _mode = "Jour";
    public string Mode
    {
        get => _mode;
        set
        {
            if (string.Equals(_mode, value, StringComparison.Ordinal)) return;
            _mode = value ?? "Jour";
            OnPropertyChanged();
            ApplyModeDefaults();
            Refresh();
        }
    }

    private DateTime? _day = DateTime.Today;
    public DateTime? Day
    {
        get => _day;
        set
        {
            _day = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSelectedMonthLocked));
            RaiseSelectionCommands();
            Refresh();
        }
    }

    private string _year = "";
    public string Year
    {
        get => _year;
        set
        {
            _year = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSelectedMonthLocked));
            RaiseSelectionCommands();
            Refresh();
        }
    }

    private string _month = "";
    public string Month
    {
        get => _month;
        set
        {
            _month = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSelectedMonthLocked));
            RaiseSelectionCommands();
            Refresh();
        }
    }

    private string _search = "";
    public string Search
    {
        get => _search;
        set
        {
            _search = value;
            OnPropertyChanged();
            Refresh();
        }
    }

    private string _totalPatient = "0,00 €";
    public string TotalPatient
    {
        get => _totalPatient;
        set { _totalPatient = value; OnPropertyChanged(); }
    }

    private string _totalMutuelle = "0,00 €";
    public string TotalMutuelle
    {
        get => _totalMutuelle;
        set { _totalMutuelle = value; OnPropertyChanged(); }
    }

    private string _totalGeneral = "0,00 €";
    public string TotalGeneral
    {
        get => _totalGeneral;
        set { _totalGeneral = value; OnPropertyChanged(); }
    }

    private bool _isDayEnabled = true;
    public bool IsDayEnabled
    {
        get => _isDayEnabled;
        private set { _isDayEnabled = value; OnPropertyChanged(); }
    }

    private bool _isYearEnabled = false;
    public bool IsYearEnabled
    {
        get => _isYearEnabled;
        private set { _isYearEnabled = value; OnPropertyChanged(); }
    }

    private bool _isMonthEnabled = false;
    public bool IsMonthEnabled
    {
        get => _isMonthEnabled;
        private set { _isMonthEnabled = value; OnPropertyChanged(); }
    }

    private SeanceRow? _selected;
    public SeanceRow? Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedLockMessage));
            RaiseSelectionCommands();
        }
    }

    public bool IsSelectedMonthLocked => ResolveCurrentPeriod() is string p && new InvoiceRepo().HasAnyInvoicesForPeriod(p);

    public string SelectedLockMessage
    {
        get
        {
            var period = ResolveCurrentPeriod();
            if (string.IsNullOrWhiteSpace(period)) return "";
            return new InvoiceRepo().HasAnyInvoicesForPeriod(period)
                ? $"Le mois {period} est verrouillé car des factures existent déjà."
                : $"Le mois {period} est modifiable.";
        }
    }

    public ICommand RefreshCommand { get; }
    public RelayCommand DeleteSelectedCommand { get; }

    public SeancesViewModel()
    {
        RefreshCommand = new RelayCommand(Refresh);
        DeleteSelectedCommand = new RelayCommand(DeleteSelected, CanEditSelected);

        Mode = "Jour";
    }

    public void SetDay(DateTime date)
    {
        Mode = "Jour";
        Day = date.Date;
        Refresh();
    }

    private void RaiseSelectionCommands()
    {
        DeleteSelectedCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(SelectedLockMessage));
    }

    private bool CanEditSelected()
    {
        if (Selected is null) return false;
        var period = PeriodFromSelected();
        if (string.IsNullOrWhiteSpace(period)) return false;
        return !new InvoiceRepo().HasAnyInvoicesForPeriod(period);
    }

    private string? ResolveCurrentPeriod()
    {
        if (string.Equals(Mode, "Mois", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(Year, out var y)) return null;
            if (!int.TryParse(Month, out var m)) return null;
            return $"{y:0000}-{Math.Clamp(m,1,12):00}";
        }

        if (string.Equals(Mode, "Jour", StringComparison.OrdinalIgnoreCase))
            return (Day ?? DateTime.Today).ToString("yyyy-MM");

        return null;
    }

    private string? PeriodFromSelected()
    {
        if (Selected?.DateIso is string iso && iso.Length >= 7)
            return iso[..7];
        return ResolveCurrentPeriod();
    }

    private void ApplyModeDefaults()
    {
        var today = DateTime.Today;

        if (string.Equals(Mode, "Mois", StringComparison.OrdinalIgnoreCase))
        {
            IsDayEnabled = false;
            IsYearEnabled = true;
            IsMonthEnabled = true;
            Year = today.Year.ToString(CultureInfo.InvariantCulture);
            Month = today.Month.ToString(CultureInfo.InvariantCulture);
            return;
        }

        if (string.Equals(Mode, "Année", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Mode, "Annee", StringComparison.OrdinalIgnoreCase))
        {
            IsDayEnabled = false;
            IsYearEnabled = true;
            IsMonthEnabled = false;
            Day = null;
            Month = "";
            Year = today.Year.ToString(CultureInfo.InvariantCulture);
            return;
        }

        IsDayEnabled = true;
        IsYearEnabled = false;
        IsMonthEnabled = false;
        Year = "";
        Month = "";

        try
        {
            if (_repo.HasSeancesForDay(today))
            {
                Day = today;
            }
            else
            {
                var lastIso = _repo.GetLastSeanceDateIso();
                if (!string.IsNullOrWhiteSpace(lastIso) &&
                    DateTime.TryParseExact(lastIso, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var last))
                    Day = last;
                else
                    Day = today;
            }
        }
        catch
        {
            Day = today;
        }
    }

    public void Refresh()
    {
        var list = Load();
        Items.Clear();
        foreach (var x in list) Items.Add(x);

        var sumP = list.Sum(x => x.PartPatientCents);
        var sumM = list.Sum(x => x.PartMutuelleCents);
        TotalPatient = Money(sumP);
        TotalMutuelle = Money(sumM);
        TotalGeneral = Money(sumP + sumM);

        if (Selected is not null)
            Selected = Items.FirstOrDefault(x => x.SeanceId == Selected.SeanceId) ?? Items.FirstOrDefault();
        else
            Selected = Items.FirstOrDefault();

        OnPropertyChanged(nameof(IsSelectedMonthLocked));
    }

    private SeanceRow[] Load()
    {
        var q = (Search ?? "").Trim();

        if (string.Equals(Mode, "Mois", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(Year, out var y)) y = DateTime.Today.Year;
            if (!int.TryParse(Month, out var m)) m = DateTime.Today.Month;
            var list = _repo.GetByMonth(y, Math.Clamp(m, 1, 12));
            return FilterText(list, q);
        }

        if (string.Equals(Mode, "Année", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Mode, "Annee", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(Year, out var y)) y = DateTime.Today.Year;
            var list = _repo.GetByYear(y);
            return FilterText(list, q);
        }

        var d = Day ?? DateTime.Today;
        if (q.Length == 0) return _repo.GetByDay(d).ToArray();
        return _repo.Search(q, d).ToArray();
    }

    private static SeanceRow[] FilterText(System.Collections.Generic.List<SeanceRow> list, string q)
    {
        if (q.Length == 0) return list.ToArray();
        q = q.ToLowerInvariant();
        return list.Where(x =>
            (x.PatientDisplay ?? "").ToLowerInvariant().Contains(q) ||
            (x.TarifLabel ?? "").ToLowerInvariant().Contains(q) ||
            (x.Commentaire ?? "").ToLowerInvariant().Contains(q)).ToArray();
    }

    private void DeleteSelected()
    {
        if (Selected is null) return;
        if (!CanEditSelected())
        {
            MessageBox.Show("Impossible de supprimer cette séance : les factures du mois existent déjà.", "PARAFacto", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!ChoiceDialog.AskYesNo(
                "PARAFacto",
                "Supprimer la séance sélectionnée de la base ?",
                "Supprimer",
                "Annuler"))
            return;

        _service.DeleteSeance(Selected.SeanceId);
        Refresh();
    }

    private static string Money(int cents)
        => (cents / 100m).ToString("0.00", CultureInfo.GetCultureInfo("fr-BE")) + " €";
}
