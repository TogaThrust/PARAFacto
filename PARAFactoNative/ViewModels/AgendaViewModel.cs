using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using Dapper;
using PARAFactoNative.Models;
using PARAFactoNative.Services;
using PARAFactoNative.Views;

namespace PARAFactoNative.ViewModels;

public sealed class AgendaPatientRow
{
    public long Id { get; init; }
    public string Display { get; init; } = "";
    public string Statut { get; init; } = "";
}

public sealed class AgendaTarifPick
{
    public long Id { get; init; }
    public string Label { get; init; } = "";
}

public sealed class AgendaDurationChoice : NotifyBase
{
    private string _label = "";
    public string Label
    {
        get => _label;
        set => Set(ref _label, value);
    }

    /// <summary>30 ou 60 pour les préréglages ; null = « Autre » (saisie heures/minutes).</summary>
    public int? PresetMinutes { get; init; }
}

public sealed class AgendaLineVm : NotifyBase
{
    public long AppointmentId { get; init; }
    public string LineText { get; init; } = "";
    /// <summary>Plage libre ≥ durée sélectionnée (vert).</summary>
    public bool IsAvailableGap { get; init; }
    /// <summary>Bloc d’indisponibilité (rouge).</summary>
    public bool IsUnavailability { get; init; }
    /// <summary>Pause lunch récurrente (jaune).</summary>
    public bool IsLunchBreak { get; init; }
    /// <summary>Ordre d’affichage dans la journée (minute depuis minuit).</summary>
    public int SortMinutes { get; init; }
    /// <summary>Jour calendaire déjà passé : pas d’édition ni de sélection de RDV.</summary>
    public bool IsHistoricalReadOnlyDay { get; init; }
    /// <summary>Date de la case calendrier (double-clic sur le lunch).</summary>
    public DateTime? CalendarOwnerDate { get; init; }
    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }
}

public sealed class AgendaMonthCellVm
{
    public DateTime CellDate { get; init; }
    public bool IsCurrentMonth { get; init; }
    public bool IsToday { get; init; }
    public bool IsBelgianHoliday { get; init; }
    public string HolidayName { get; init; } = "";
    public string? HolidayTooltip => IsBelgianHoliday && !string.IsNullOrWhiteSpace(HolidayName) ? HolidayName : null;
    public bool IsPastDay { get; init; }
    public bool CanSelectCalendarDay => !IsPastDay;
    public List<AgendaLineVm> Lines { get; init; } = new();
}

public sealed class AgendaWeekColumnVm
{
    public string Header { get; init; } = "";
    public DateTime Day { get; init; }
    public bool IsToday { get; init; }
    public bool IsPastDay { get; init; }
    public bool CanSelectCalendarDay => !IsPastDay;
    public bool IsBelgianHoliday { get; init; }
    public string HolidayName { get; init; } = "";
    public string? HolidayTooltip => IsBelgianHoliday && !string.IsNullOrWhiteSpace(HolidayName) ? HolidayName : null;
    public List<AgendaLineVm> Lines { get; init; } = new();
}

public sealed class AgendaViewModel : NotifyBase
{
    private const string AutreDurationLabelDefault = "Autre (heures et minutes…)";

    private const string MsgRdvDansLePasse =
        "Hélas, on ne sait pas retourner dans le passé... C'est notre condition humaine, désolé. Merci de choisir un créneau de séance situé dans le futur :-)";

    private const string MsgRdvPasseNonModifiable =
        "Les rendez-vous passés ne peuvent pas être modifiés depuis l’agenda.";

    private static CultureInfo UiCulture => CultureInfo.CurrentCulture;

    private readonly AppointmentRepo _repo = new();
    private readonly UnavailabilityRepo _unavailRepo = new();
    private readonly LunchDayOverrideRepo _lunchOverrideRepo = new();
    private readonly WorkdayDayOverrideRepo _workdayOverrideRepo = new();
    private readonly PatientRepo _patientRepo = new();
    private readonly TarifRepo _tarifs = new();
    private readonly SeanceService _seanceSvc = new();
    private readonly AppSettingsStore _appSettings = new();

    private int _workdayStartMin = 9 * 60;
    private int _workdayClosingMin = 21 * 60;
    private string _workdaySettingStartDisplay = "09:00";
    private string _workdaySettingClosingDisplay = "21:00";

    private bool _lunchBreakEnabled;
    private int _lunchStartMin = 12 * 60;
    private int _lunchEndMin = 13 * 60;
    private string _lunchStartDisplay = "12:00";
    private string _lunchEndDisplay = "13:00";
    private bool _suppressLunchToggle;

    /// <summary>Exceptions lunch par date (plage visible + chargement paresseux hors plage).</summary>
    private Dictionary<string, LunchDayOverrideRow> _lunchOverridesByDay = new(StringComparer.Ordinal);

    /// <summary>Exceptions début/fin de journée par date (plage visible + chargement paresseux hors plage).</summary>
    private Dictionary<string, WorkdayDayOverrideRow> _workdayOverridesByDay = new(StringComparer.Ordinal);

    /// <summary>Émis après suppression d’un RDV (pour rafraîchir la console si même jour).</summary>
    public event Action<DateTime>? AgendaAppointmentDeleted;

    public List<string> ViewModes => new() { UiTextTranslator.Translate("Mois"), UiTextTranslator.Translate("Semaine"), UiTextTranslator.Translate("Jour") };

    private string _viewMode = "Mois";
    public string ViewMode
    {
        get => _viewMode;
        set
        {
            var canonical = NormalizeViewMode(value);
            if (!Set(ref _viewMode, canonical)) return;
            Raise(nameof(IsMonthView));
            Raise(nameof(IsWeekView));
            Raise(nameof(IsDayView));
            Raise(nameof(IsDayViewPastDay));
            RefreshCalendar();
            UpdateHeader();
        }
    }

    public bool IsMonthView => _viewMode == "Mois";
    public bool IsWeekView => _viewMode == "Semaine";
    public bool IsDayView => _viewMode == "Jour";

    /// <summary>Vue jour sur une date déjà passée : affichage grisé, lecture seule.</summary>
    public bool IsDayViewPastDay => IsDayView && AnchorDate.Date < DateTime.Today;

    private DateTime _anchorDate = DateTime.Today;
    public DateTime AnchorDate
    {
        get => _anchorDate;
        set
        {
            if (!Set(ref _anchorDate, value.Date)) return;
            UpdateHeader();
            Raise(nameof(IsDayViewPastDay));
            RefreshCalendar();
        }
    }

    private string _headerTitle = "";
    public string HeaderTitle
    {
        get => _headerTitle;
        private set => Set(ref _headerTitle, value);
    }

    public ObservableCollection<string> TimeSlots { get; } = new();

    /// <summary>Choix quart d’heure pour les réglages début / fin de journée.</summary>
    public List<string> AgendaQuarterHourChoices { get; } = Enumerable.Range(0, 96)
        .Select(i => AppointmentScheduling.FormatMinutesAsHhMm(i * 15))
        .ToList();

    public string WorkdaySettingStart
    {
        get => _workdaySettingStartDisplay;
        set
        {
            var v = (value ?? "").Trim();
            if (string.Equals(v, _workdaySettingStartDisplay, StringComparison.Ordinal)) return;
            if (!AppointmentScheduling.TryParseTimeToMinutes(v, out var newStart)) return;
            if (!AppointmentScheduling.TryParseTimeToMinutes(_workdaySettingClosingDisplay, out var close))
                return;
            if (newStart >= close - 15)
            {
                MessageBox.Show(
                    "L’heure de début de journée doit être au moins 15 minutes avant la fin de journée.",
                    "Agenda",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                Raise(nameof(WorkdaySettingStart));
                return;
            }

            _workdaySettingStartDisplay = AppointmentScheduling.FormatMinutesAsHhMm(newStart);
            _workdayStartMin = newStart;
            _appSettings.SaveAgendaWorkday(_workdaySettingStartDisplay, _workdaySettingClosingDisplay);
            OnWorkdaySettingsApplied();
        }
    }

    public string WorkdaySettingClosing
    {
        get => _workdaySettingClosingDisplay;
        set
        {
            var v = (value ?? "").Trim();
            if (string.Equals(v, _workdaySettingClosingDisplay, StringComparison.Ordinal)) return;
            if (!AppointmentScheduling.TryParseTimeToMinutes(v, out var newClose)) return;
            if (!AppointmentScheduling.TryParseTimeToMinutes(_workdaySettingStartDisplay, out var start))
                return;
            if (newClose <= start + 15)
            {
                MessageBox.Show(
                    "L’heure de fin de journée doit être au moins 15 minutes après le début de journée.",
                    "Agenda",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                Raise(nameof(WorkdaySettingClosing));
                return;
            }

            _workdaySettingClosingDisplay = AppointmentScheduling.FormatMinutesAsHhMm(newClose);
            _workdayClosingMin = newClose;
            _appSettings.SaveAgendaWorkday(_workdaySettingStartDisplay, _workdaySettingClosingDisplay);
            OnWorkdaySettingsApplied();
        }
    }

    public bool LunchBreakEnabled
    {
        get => _lunchBreakEnabled;
        set
        {
            if (_suppressLunchToggle)
            {
                Set(ref _lunchBreakEnabled, value);
                return;
            }

            if (value == _lunchBreakEnabled) return;
            if (value)
            {
                var w = new LunchBreakWindow(_lunchStartDisplay, _lunchEndDisplay)
                {
                    Owner = Application.Current?.MainWindow
                };
                if (w.ShowDialog() != true)
                {
                    _suppressLunchToggle = true;
                    Raise(nameof(LunchBreakEnabled));
                    _suppressLunchToggle = false;
                    return;
                }

                _lunchStartMin = w.StartTotalMinutes;
                _lunchEndMin = w.EndTotalMinutes;

                // Le rendu agenda clippe la pause lunch à la plage de journée.
                // Si la pause est totalement hors plage, elle serait enregistrée mais invisible.
                var overlapsWorkday = _lunchEndMin > _workdayStartMin && _lunchStartMin < _workdayClosingMin;
                if (!overlapsWorkday)
                {
                    MessageBox.Show(
                        $"La pause lunch ({AppointmentScheduling.FormatMinutesAsHhMm(_lunchStartMin)} – {AppointmentScheduling.FormatMinutesAsHhMm(_lunchEndMin)}) " +
                        $"est en dehors de la plage de journée ({AppointmentScheduling.FormatMinutesAsHhMm(_workdayStartMin)} – {AppointmentScheduling.FormatMinutesAsHhMm(_workdayClosingMin)}).\n\n" +
                        "Choisissez une heure qui recoupe la journée pour qu’elle soit visible dans le calendrier.",
                        "Agenda",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    _suppressLunchToggle = true;
                    Raise(nameof(LunchBreakEnabled));
                    _suppressLunchToggle = false;
                    return;
                }

                _lunchStartDisplay = AppointmentScheduling.FormatMinutesAsHhMm(_lunchStartMin);
                _lunchEndDisplay = AppointmentScheduling.FormatMinutesAsHhMm(_lunchEndMin);
                _appSettings.SaveAgendaLunch(true, _lunchStartDisplay, _lunchEndDisplay);
                Set(ref _lunchBreakEnabled, true);
                OnLunchSettingsChanged();
            }
            else
            {
                _appSettings.SaveAgendaLunch(false, _lunchStartDisplay, _lunchEndDisplay);
                Set(ref _lunchBreakEnabled, false);
                OnLunchSettingsChanged();
            }
        }
    }

    public List<AgendaDurationChoice> DurationPresets { get; } = new()
    {
        new AgendaDurationChoice { Label = UiTextTranslator.Translate("30 minutes"), PresetMinutes = 30 },
        new AgendaDurationChoice { Label = UiTextTranslator.Translate("1 heure"), PresetMinutes = 60 },
        new AgendaDurationChoice { Label = UiTextTranslator.Translate(AutreDurationLabelDefault), PresetMinutes = null }
    };

    private bool _suppressDurationChoice;
    private bool _suppressTimeSuggest;
    private AgendaDurationChoice? _selectedDurationChoice;

    public AgendaDurationChoice SelectedDurationChoice
    {
        get => _selectedDurationChoice ?? DurationPresets[0];
        set
        {
            if (value is null) return;

            if (_suppressDurationChoice)
            {
                _selectedDurationChoice = value;
                Raise(nameof(SelectedDurationChoice));
                return;
            }

            if (value.PresetMinutes is int pm)
            {
                if (ReferenceEquals(_selectedDurationChoice, value) && DurationMinutes == pm)
                    return;
                DurationMinutes = pm;
                ResetAutrePresetLabel();
                _selectedDurationChoice = value;
                Raise(nameof(SelectedDurationChoice));
                return;
            }

            var prev = _selectedDurationChoice ?? DurationPresets[0];
            var w = new DurationCustomWindow(DurationMinutes) { Owner = Application.Current?.MainWindow };
            if (w.ShowDialog() == true && w.TotalMinutes > 0)
            {
                var mins = w.TotalMinutes;
                DurationMinutes = mins;
                ApplyAutrePresetLabelFromMinutes(mins);
                _selectedDurationChoice = value;
                Raise(nameof(SelectedDurationChoice));
            }
            else
            {
                _suppressDurationChoice = true;
                _selectedDurationChoice = prev;
                _suppressDurationChoice = false;
                Raise(nameof(SelectedDurationChoice));
            }
        }
    }

    public ObservableCollection<AgendaPatientRow> Patients { get; } = new();
    public ObservableCollection<AgendaTarifPick> TarifChoices { get; } = new();

    private AgendaPatientRow? _selectedPatient;
    public AgendaPatientRow? SelectedPatient
    {
        get => _selectedPatient;
        set
        {
            if (!Set(ref _selectedPatient, value)) return;
            ApplyTarifFromStatut();
            SaveCommand.RaiseCanExecuteChanged();
            EncodeRecurringCommand.RaiseCanExecuteChanged();
        }
    }

    private AgendaTarifPick? _selectedTarif;
    public AgendaTarifPick? SelectedTarif
    {
        get => _selectedTarif;
        set
        {
            if (!Set(ref _selectedTarif, value)) return;
            SaveCommand.RaiseCanExecuteChanged();
            EncodeRecurringCommand.RaiseCanExecuteChanged();
        }
    }

    private DateTime _appointmentDate = DateTime.Today;
    public DateTime AppointmentDate
    {
        get => _appointmentDate;
        set
        {
            var d = value.Date;
            if (d < DateTime.Today)
                d = DateTime.Today;
            if (!Set(ref _appointmentDate, d)) return;
            RebuildAppointmentTimeSlots();
            if (!_suppressTimeSuggest && EditingId == 0)
                SuggestNextAvailableStartIfNew();
            RefreshLunchResetButtonVisibility();
            EncodeRecurringCommand.RaiseCanExecuteChanged();
        }
    }

    private string _selectedTime = "09:00";
    public string SelectedTime
    {
        get => _selectedTime;
        set
        {
            if (!Set(ref _selectedTime, value ?? "09:00")) return;
            EncodeRecurringCommand.RaiseCanExecuteChanged();
        }
    }

    private int _durationMinutes = 30;
    public int DurationMinutes
    {
        get => _durationMinutes;
        set
        {
            if (!Set(ref _durationMinutes, value)) return;
            if (!_suppressTimeSuggest)
            {
                RebuildAppointmentTimeSlots();
                if (EditingId == 0)
                    SuggestNextAvailableStartIfNew();
                RefreshCalendar();
            }
        }
    }

    private long _editingId;
    public long EditingId
    {
        get => _editingId;
        private set
        {
            if (!Set(ref _editingId, value)) return;
            Raise(nameof(IsEditingExisting));
            DeleteCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsEditingExisting => EditingId > 0;

    public ObservableCollection<AgendaMonthCellVm> MonthCells { get; } = new();
    public ObservableCollection<AgendaWeekColumnVm> WeekColumns { get; } = new();
    public ObservableCollection<AgendaLineVm> DayLines { get; } = new();

    private long _selectedAppointmentId;
    private bool _isDayViewHoliday;
    private string _dayHolidayBanner = "";

    public bool IsDayViewHoliday
    {
        get => _isDayViewHoliday;
        private set => Set(ref _isDayViewHoliday, value);
    }

    public string DayHolidayBanner
    {
        get => _dayHolidayBanner;
        private set => Set(ref _dayHolidayBanner, value);
    }

    private bool _dayViewIsToday;
    public bool DayViewIsToday
    {
        get => _dayViewIsToday;
        private set => Set(ref _dayViewIsToday, value);
    }

    private bool _showResetLunchForSelectedDay;
    /// <summary>True si la date du formulaire a une exception lunch (omit ou moved) : proposer le retour au réglage récurrent.</summary>
    public bool ShowResetLunchForSelectedDay
    {
        get => _showResetLunchForSelectedDay;
        private set
        {
            if (!Set(ref _showResetLunchForSelectedDay, value)) return;
            ResetLunchToRecurringCommand.RaiseCanExecuteChanged();
        }
    }

    public RelayCommand PrevCommand { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand TodayCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand NewCommand { get; }
    public RelayCommand<long?> SelectAppointmentCommand { get; }
    public RelayCommand IndisponibiliteCommand { get; }
    public RelayCommand<object?> SelectCalendarDayCommand { get; }
    public RelayCommand ResetLunchToRecurringCommand { get; }
    public RelayCommand EncodeRecurringCommand { get; }
    public RelayCommand CopyDayRecurringCommand { get; }
    public RelayCommand DeleteDayCommand { get; }

    public AgendaViewModel()
    {
        PrevCommand = new RelayCommand(() => Navigate(-1));
        NextCommand = new RelayCommand(() => Navigate(1));
        TodayCommand = new RelayCommand(() =>
        {
            AnchorDate = DateTime.Today;
            AppointmentDate = DateTime.Today;
        });
        SaveCommand = new RelayCommand(Save, CanSave);
        DeleteCommand = new RelayCommand(Delete, CanDelete);
        NewCommand = new RelayCommand(ClearForm);
        SelectAppointmentCommand = new RelayCommand<long?>(id =>
        {
            if (id is not > 0) return;
            var row = _repo.GetById(id.Value);
            if (row is not null
                && DateTime.TryParseExact(row.DateIso, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var rd)
                && rd.Date < DateTime.Today)
            {
                MessageBox.Show(MsgRdvPasseNonModifiable, "Agenda", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _selectedAppointmentId = id.Value;
            ApplyLineSelection();
            LoadForEdit(id.Value);
        });
        IndisponibiliteCommand = new RelayCommand(OpenUnavailabilityWindow);
        SelectCalendarDayCommand = new RelayCommand<object?>(SelectCalendarDay);
        ResetLunchToRecurringCommand = new RelayCommand(ResetLunchToRecurring, () => ShowResetLunchForSelectedDay);
        EncodeRecurringCommand = new RelayCommand(EncodeRecurring, CanEncodeRecurring);
        CopyDayRecurringCommand = new RelayCommand(CopyDayRecurringUntilDate, CanCopyOrDeleteSelectedDay);
        DeleteDayCommand = new RelayCommand(DeleteSelectedDay, CanCopyOrDeleteSelectedDay);

        _suppressTimeSuggest = true;
        _suppressDurationChoice = true;
        RefreshDurationPresetLabels();
        _selectedDurationChoice = DurationPresets[0];
        _suppressDurationChoice = false;
        Raise(nameof(SelectedDurationChoice));

        LoadWorkdaySettingsFromStore();
        LoadLunchFromStore();
        RebuildAppointmentTimeSlots();
        Raise(nameof(WorkdaySettingStart));
        Raise(nameof(WorkdaySettingClosing));
        Raise(nameof(LunchBreakEnabled));
        UiLanguageService.LanguageChanged += _ =>
        {
            Raise(nameof(ViewModes));
            RefreshDurationPresetLabels();
            UpdateHeader();
            RefreshCalendar();
        };

        ReloadRefs();
        UpdateHeader();
        _suppressTimeSuggest = false;
        SuggestNextAvailableStartIfNew();
    }

    private static string NormalizeViewMode(string? value)
    {
        var v = (value ?? "").Trim();
        if (string.Equals(v, "Mois", StringComparison.OrdinalIgnoreCase)
            || string.Equals(v, "Month", StringComparison.OrdinalIgnoreCase)
            || string.Equals(v, "Maand", StringComparison.OrdinalIgnoreCase))
            return "Mois";
        if (string.Equals(v, "Semaine", StringComparison.OrdinalIgnoreCase)
            || string.Equals(v, "Week", StringComparison.OrdinalIgnoreCase))
            return "Semaine";
        return "Jour";
    }

    private bool CanSave() => SelectedPatient is not null && SelectedTarif is not null;

    private bool CanEncodeRecurring()
    {
        if (!CanSave()) return false;
        if (!AppointmentScheduling.TryParseTimeToMinutes(SelectedTime, out _)) return false;
        if (AppointmentDate.Date < DateTime.Today) return false;
        return true;
    }

    private void SelectCalendarDay(object? p)
    {
        if (p is not DateTime dt) return;
        if (dt.Date < DateTime.Today) return;
        AppointmentDate = dt.Date;
    }

    private bool CanDelete()
    {
        if (EditingId <= 0) return false;
        var row = _repo.GetById(EditingId);
        if (row is null) return false;
        if (!DateTime.TryParseExact(row.DateIso, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return true;
        return d.Date >= DateTime.Today;
    }

    private bool CanCopyOrDeleteSelectedDay() => AppointmentDate.Date >= DateTime.Today;

    private void RefreshLunchResetButtonVisibility()
    {
        var iso = AppointmentDate.ToString("yyyy-MM-dd");
        ShowResetLunchForSelectedDay = ResolveLunchOverrideRow(iso) is not null;
        CopyDayRecurringCommand.RaiseCanExecuteChanged();
        DeleteDayCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Lunch récurrent (paramètres globaux), sans exception jour — pour contrôle avant « retour au récurrent ».</summary>
    private bool TryGetRecurrentLunchBlockForDay(DateTime day, out int startMin, out int endMin)
    {
        startMin = 0;
        endMin = 0;
        if (day.DayOfWeek == DayOfWeek.Sunday) return false;
        if (BelgianHolidayHelper.TryGetName(day.Date, out _)) return false;
        if (!_lunchBreakEnabled || _lunchEndMin <= _lunchStartMin) return false;
        startMin = _lunchStartMin;
        endMin = _lunchEndMin;
        return true;
    }

    private void ResetLunchToRecurring()
    {
        var iso = AppointmentDate.ToString("yyyy-MM-dd");
        if (ResolveLunchOverrideRow(iso) is null) return;

        var day = AppointmentDate.Date;
        var owner = Application.Current?.MainWindow;
        var editingIdNullable = EditingId > 0 ? EditingId : (long?)null;
        var formStartMin = 0;
        var formDur = 30;
        if (EditingId > 0 && AppointmentScheduling.TryParseTimeToMinutes(SelectedTime, out var fs))
        {
            formStartMin = fs;
            formDur = DurationMinutes > 0 ? DurationMinutes : 30;
        }

        if (TryGetRecurrentLunchBlockForDay(day, out var recLs, out var recLe))
        {
            while (true)
            {
                var sameDay = _repo.ListForDay(day);
                var unavailDay = _unavailRepo.ListForDay(day);
                var conflicts = AppointmentScheduling.ListAppointmentsOverlappingLunch(
                    sameDay,
                    recLs,
                    recLe,
                    editingIdNullable,
                    formStartMin,
                    formDur);

                if (conflicts.Count == 0)
                    break;

                var first = conflicts[0];
                var detail = $"Rendez-vous concerné : {first.PatientDisplay} à {NormalizeTime(first.StartTime)}.";
                var choiceWin = new LunchVsAppointmentConflictWindow(
                    detail,
                    "Après retour au lunch récurrent, la plage habituelle empiète sur un rendez-vous existant. Modifier les heures de lunch (réglage global) ou déplacer le rendez-vous ?",
                    "Modifier le lunch",
                    "Déplacer le rendez-vous",
                    "Agenda — lunch récurrent") { Owner = owner };

                if (choiceWin.ShowDialog() != true)
                    return;

                if (choiceWin.Choice == LunchVsAppointmentConflictWindow.ConflictChoice.ModifyLunch)
                {
                    var lb = new LunchBreakWindow(_lunchStartDisplay, _lunchEndDisplay) { Owner = owner };
                    if (lb.ShowDialog() != true)
                        continue;

                    _lunchStartMin = lb.StartTotalMinutes;
                    _lunchEndMin = lb.EndTotalMinutes;
                    _lunchStartDisplay = AppointmentScheduling.FormatMinutesAsHhMm(_lunchStartMin);
                    _lunchEndDisplay = AppointmentScheduling.FormatMinutesAsHhMm(_lunchEndMin);
                    _appSettings.SaveAgendaLunch(_lunchBreakEnabled, _lunchStartDisplay, _lunchEndDisplay);
                    OnLunchSettingsChanged();

                    if (!TryGetRecurrentLunchBlockForDay(day, out recLs, out recLe))
                        break;
                    continue;
                }

                if (choiceWin.Choice != LunchVsAppointmentConflictWindow.ConflictChoice.MoveAppointment)
                    return;

                var moveDur = first.DurationMinutes > 0 ? first.DurationMinutes : 30;
                int? earliest = null;
                if (day.Date == DateTime.Today)
                {
                    var n = DateTime.Now;
                    earliest = n.Hour * 60 + n.Minute;
                }

                var (wdResetS, wdResetE) = GetEffectiveWorkdayMinutesForDay(day.Date);
                var slots = AppointmentScheduling.ListAvailableStartTimes(
                    sameDay,
                    unavailDay,
                    moveDur,
                    excludeAppointmentId: first.Id,
                    editingAppointmentId: editingIdNullable,
                    editingStartMin: formStartMin,
                    editingDurationMin: formDur,
                    pendingNewRdvNotInDb: EditingId == 0,
                    pendingStartMin: formStartMin,
                    pendingDurationMin: formDur,
                    wdResetS,
                    wdResetE,
                    5,
                    earliest,
                    recLs,
                    recLe);

                if (slots.Count == 0)
                {
                    var msg = AppointmentScheduling.BuildMessageWhenNoSameDayRelocateSlot(
                        UiCulture,
                        day,
                        moveDur,
                        wdResetS,
                        wdResetE,
                        d => _repo.ListForDay(d),
                        d => _unavailRepo.ListForDay(d),
                        d => TryGetEffectiveLunchForDay(d, out var ls, out var le) ? (true, ls, le) : (false, 0, 0));
                    MessageBox.Show(msg, "Agenda — lunch", MessageBoxButton.OK, MessageBoxImage.Information);
                    continue;
                }

                var phone = _patientRepo.GetTelephoneByPatientId(first.PatientId);
                var dateFr = day.ToString("dddd d MMMM yyyy", UiCulture);
                var relocateWin = new AppointmentRelocateSlotWindow(
                    $"Patient : {first.PatientDisplay}",
                    $"Date : {dateFr} — actuellement à {NormalizeTime(first.StartTime)} ({moveDur} min).",
                    slots,
                    phone) { Owner = owner };

                if (relocateWin.ShowDialog() != true)
                    return;
                var newHhMm = relocateWin.SelectedSlotHhMm;
                if (string.IsNullOrWhiteSpace(newHhMm)) return;

                try
                {
                    _repo.Update(first.Id, first.PatientId, first.TarifId, iso, newHhMm.Trim(), moveDur, first.RecurrenceSeriesId);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "Agenda — déplacement", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                AppointmentResponsibleNotify.ShowNotifyResponsibleDialog(_patientRepo, first.PatientId);
                RefreshCalendar();
            }
        }

        try
        {
            _lunchOverrideRepo.DeleteForDateIso(iso);
            _lunchOverridesByDay.Remove(iso);
            RefreshCalendar();
            RefreshLunchResetButtonVisibility();
            if (EditingId == 0)
                SuggestNextAvailableStartIfNew();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Agenda — lunch", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public void ReloadRefs()
    {
        try
        {
            LoadPatients();
            LoadTarifChoices();
            SaveCommand.RaiseCanExecuteChanged();
            EncodeRecurringCommand.RaiseCanExecuteChanged();
            RefreshCalendar();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Agenda — chargement", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Rafraîchit uniquement le calendrier (ex. RDV supprimé depuis la console).</summary>
    public void RefreshAppointmentsCalendar()
    {
        try
        {
            RefreshCalendar();
        }
        catch
        {
            /* ignore */
        }
    }

    private void LoadPatients()
    {
        Patients.Clear();
        using var cn = Db.Open();
        var rows = cn.Query<(long id, string nom, string prenom, string statut)>(@"
SELECT id, COALESCE(nom,''), COALESCE(prenom,''), COALESCE(statut,'')
FROM patients
ORDER BY nom, prenom;
");
        foreach (var (id, nom, prenom, statut) in rows)
        {
            var display = $"{nom} {prenom}".Trim();
            if (string.IsNullOrWhiteSpace(display)) display = $"#{id}";
            Patients.Add(new AgendaPatientRow { Id = id, Display = display, Statut = statut.Trim() });
        }

        SelectedPatient ??= Patients.FirstOrDefault();
    }

    private void LoadTarifChoices()
    {
        TarifChoices.Clear();
        foreach (var p in BuildStatutTarifPicks(_tarifs.GetAll().ToList()))
            TarifChoices.Add(p);

        SelectedTarif ??= TarifChoices.FirstOrDefault();
    }

    /// <summary>Ordre : NON BIM/BIM/PLEIN en priorisant les variantes CABINET 30.</summary>
    private static IEnumerable<AgendaTarifPick> BuildStatutTarifPicks(List<Tarif> tarifs)
    {
        AgendaTarifPick? nonBim = null, bim = null, plein = null;

        static bool IsCabinet30(string label)
        {
            var u = (label ?? "").ToUpperInvariant();
            return u.Contains("CABINET") && u.Contains("30");
        }

        // 1) priorité stricte aux libellés CABINET 30
        foreach (var t in tarifs)
        {
            var u = (t.Label ?? "").ToUpperInvariant();
            if (!IsCabinet30(t.Label ?? "")) continue;
            if (u.Contains("NON") && u.Contains("BIM"))
                nonBim ??= new AgendaTarifPick { Id = t.Id, Label = t.Label ?? "" };
            else if (u.Contains("PLEIN"))
                plein ??= new AgendaTarifPick { Id = t.Id, Label = t.Label ?? "" };
            else if (u.Contains("BIM") && !u.Contains("NON"))
                bim ??= new AgendaTarifPick { Id = t.Id, Label = t.Label ?? "" };
        }

        // 2) fallback historique
        foreach (var t in tarifs)
        {
            var u = (t.Label ?? "").ToUpperInvariant();
            if (u.Contains("NON") && u.Contains("BIM"))
                nonBim ??= new AgendaTarifPick { Id = t.Id, Label = t.Label ?? "" };
            if (u.Contains("PLEIN"))
                plein ??= new AgendaTarifPick { Id = t.Id, Label = t.Label ?? "" };
        }
        foreach (var t in tarifs)
        {
            var u = (t.Label ?? "").ToUpperInvariant();
            if (u.Contains("BIM") && !u.Contains("NON"))
                bim ??= new AgendaTarifPick { Id = t.Id, Label = t.Label ?? "" };
        }
        if (nonBim != null) yield return nonBim;
        if (bim != null) yield return bim;
        if (plein != null) yield return plein;
        foreach (var t in tarifs)
        {
            if (nonBim != null && t.Id == nonBim.Id) continue;
            if (bim != null && t.Id == bim.Id) continue;
            if (plein != null && t.Id == plein.Id) continue;
            yield return new AgendaTarifPick { Id = t.Id, Label = t.Label ?? "" };
        }
    }

    private void ApplyTarifFromStatut()
    {
        if (SelectedPatient is null || TarifChoices.Count == 0) return;
        var raw = (SelectedPatient.Statut ?? "").Trim();
        var s = raw.ToUpperInvariant();
        AgendaTarifPick? pick;

        // Nouveau comportement: si la fiche patient contient un tarif exact, l'utiliser en priorité.
        pick = TarifChoices.FirstOrDefault(t => string.Equals(t.Label.Trim(), raw, StringComparison.OrdinalIgnoreCase));
        if (pick is not null)
        {
            SelectedTarif = pick;
            return;
        }

        static bool IsCabinet30Label(string label)
        {
            var u = (label ?? "").ToUpperInvariant();
            return u.Contains("CABINET") && u.Contains("30");
        }

        // Priorité stricte demandée : BIM/NON BIM/PLEIN -> "... CABINET 30".
        if (s.Contains("NON") && s.Contains("BIM"))
        {
            pick = TarifChoices.FirstOrDefault(t => string.Equals(t.Label.Trim(), "NON BIM CABINET 30", StringComparison.OrdinalIgnoreCase))
                   ?? TarifChoices.FirstOrDefault(t =>
                   {
                       var u = t.Label.ToUpperInvariant();
                       return u.Contains("NON") && u.Contains("BIM") && IsCabinet30Label(t.Label);
                   })
                   ?? TarifChoices.FirstOrDefault(t => t.Label.ToUpperInvariant().Contains("NON") && t.Label.ToUpperInvariant().Contains("BIM"));
        }
        else if (s.Contains("PLEIN"))
        {
            pick = TarifChoices.FirstOrDefault(t => string.Equals(t.Label.Trim(), "PLEIN CABINET 30", StringComparison.OrdinalIgnoreCase))
                   ?? TarifChoices.FirstOrDefault(t =>
                   {
                       var u = t.Label.ToUpperInvariant();
                       return u.Contains("PLEIN") && IsCabinet30Label(t.Label);
                   })
                   ?? TarifChoices.FirstOrDefault(t => t.Label.ToUpperInvariant().Contains("PLEIN"));
        }
        else if (s == "BIM" || (s.Contains("BIM") && !s.Contains("NON")))
        {
            pick = TarifChoices.FirstOrDefault(t => string.Equals(t.Label.Trim(), "BIM CABINET 30", StringComparison.OrdinalIgnoreCase))
                   ?? TarifChoices.FirstOrDefault(t =>
                   {
                       var u = t.Label.ToUpperInvariant();
                       return u.Contains("BIM") && !u.Contains("NON") && IsCabinet30Label(t.Label);
                   })
                   ?? TarifChoices.FirstOrDefault(t =>
                   {
                       var u = t.Label.ToUpperInvariant();
                       return u.Contains("BIM") && !u.Contains("NON");
                   });
        }
        else
        {
            pick = null;
        }

        SelectedTarif = pick ?? TarifChoices.FirstOrDefault();
    }

    private void UpdateHeader()
    {
        HeaderTitle = ViewMode switch
        {
            "Mois" => AnchorDate.ToString("MMMM yyyy", UiCulture),
            "Semaine" =>
                $"{StartOfWeekMonday(AnchorDate):dd/MM/yyyy} – {StartOfWeekMonday(AnchorDate).AddDays(6):dd/MM/yyyy}",
            _ => AnchorDate.ToString("dddd d MMMM yyyy", UiCulture)
        };
    }

    private void Navigate(int delta)
    {
        AnchorDate = ViewMode switch
        {
            "Mois" => AnchorDate.AddMonths(delta),
            "Semaine" => AnchorDate.AddDays(7 * delta),
            _ => AnchorDate.AddDays(delta)
        };
    }

    private void RefreshCalendar()
    {
        MonthCells.Clear();
        WeekColumns.Clear();
        DayLines.Clear();

        var (from, to) = GetVisibleRange();
        var fromIso = from.ToString("yyyy-MM-dd");
        var toIso = to.ToString("yyyy-MM-dd");
        var rows = _repo.ListBetweenInclusive(fromIso, toIso);
        var byDay = rows.GroupBy(r => r.DateIso).ToDictionary(g => g.Key, g => g.OrderBy(x => x.StartTime).ToList());
        var byDayUnavail = _unavailRepo.ListBetweenInclusive(fromIso, toIso)
            .GroupBy(r => r.DateIso)
            .ToDictionary(g => g.Key, g => g.ToList());

        _lunchOverridesByDay = _lunchOverrideRepo.ListBetweenInclusive(fromIso, toIso)
            .ToDictionary(r => r.DateIso, r => r, StringComparer.Ordinal);

        _workdayOverridesByDay = _workdayOverrideRepo.ListBetweenInclusive(fromIso, toIso)
            .ToDictionary(r => r.DateIso, r => r, StringComparer.Ordinal);

        if (IsMonthView) RebuildMonthCells(byDay, byDayUnavail);
        else if (IsWeekView) RebuildWeekColumns(byDay, byDayUnavail);
        else RebuildDayLines(byDay, byDayUnavail);

        ApplyLineSelection();
        UpdateDayHolidayBanner();
        DayViewIsToday = IsDayView && AnchorDate.Date == DateTime.Today;
        RefreshLunchResetButtonVisibility();
    }

    private void ApplyLineSelection()
    {
        void walk(IEnumerable<AgendaLineVm> lines)
        {
            foreach (var line in lines)
                line.IsSelected = line.AppointmentId > 0 && line.AppointmentId == _selectedAppointmentId;
        }

        foreach (var c in MonthCells)
            walk(c.Lines);
        foreach (var w in WeekColumns)
            walk(w.Lines);
        walk(DayLines);
    }

    private void UpdateDayHolidayBanner()
    {
        if (!IsDayView || !BelgianHolidayHelper.TryGetName(AnchorDate, out var name))
        {
            IsDayViewHoliday = false;
            DayHolidayBanner = "";
            return;
        }

        IsDayViewHoliday = true;
        DayHolidayBanner = $"{UiTextTranslator.Translate("Jour férié :")} {TranslateHolidayName(name)}";
    }

    private (DateTime from, DateTime to) GetVisibleRange()
    {
        if (IsMonthView)
        {
            var first = new DateTime(AnchorDate.Year, AnchorDate.Month, 1);
            var start = StartOfWeekMonday(first);
            var end = start.AddDays(41);
            return (start, end);
        }
        if (IsWeekView)
        {
            var start = StartOfWeekMonday(AnchorDate);
            return (start, start.AddDays(6));
        }
        return (AnchorDate.Date, AnchorDate.Date);
    }

    private AgendaLineVm LineFrom(AppointmentRow r, bool historicalReadOnly, DateTime calendarDay)
    {
        var sort = 0;
        AppointmentScheduling.TryParseTimeToMinutes(r.StartTime, out sort);
        return new AgendaLineVm
        {
            AppointmentId = r.Id,
            SortMinutes = sort,
            LineText = $"{r.StartTime}  {r.PatientDisplay}",
            IsHistoricalReadOnlyDay = historicalReadOnly,
            CalendarOwnerDate = calendarDay
        };
    }

    private AgendaLineVm UnavailLineFrom(UnavailabilityRow u, int startMin, int endMin, bool historicalReadOnly, DateTime calendarDay)
    {
        var reason = string.IsNullOrWhiteSpace(u.Reason) ? "" : $" — {u.Reason.Trim()}";
        var t0 = AppointmentScheduling.FormatMinutesAsHhMm(startMin);
        var t1 = AppointmentScheduling.FormatMinutesAsHhMm(endMin);
        return new AgendaLineVm
        {
            AppointmentId = 0,
            IsUnavailability = true,
            SortMinutes = startMin,
            LineText = $"{AgendaUnavailableLabel()} {t0} – {t1}{reason}",
            IsHistoricalReadOnlyDay = historicalReadOnly,
            CalendarOwnerDate = calendarDay
        };
    }

    private AgendaLineVm LunchLineFrom(int startMin, int endMin, bool historicalReadOnly, DateTime calendarDay)
    {
        var t0 = AppointmentScheduling.FormatMinutesAsHhMm(startMin);
        var t1 = AppointmentScheduling.FormatMinutesAsHhMm(endMin);
        return new AgendaLineVm
        {
            AppointmentId = 0,
            IsLunchBreak = true,
            SortMinutes = startMin,
            LineText = $"{AgendaLunchLabel()} {t0} – {t1}",
            IsHistoricalReadOnlyDay = historicalReadOnly,
            CalendarOwnerDate = calendarDay
        };
    }

    private static (int s, int e)? ClipBusyToWorkday(int s, int e, int ws, int wc)
    {
        var a = Math.Max(s, ws);
        var b = Math.Min(e, wc);
        if (b <= a) return null;
        return (a, b);
    }

    private static void SortAgendaLinesChronologically(List<AgendaLineVm> list)
    {
        static int Tie(AgendaLineVm x)
        {
            if (x.IsUnavailability) return 0;
            if (x.IsLunchBreak) return 1;
            if (x.AppointmentId > 0 && !x.IsAvailableGap) return 2;
            return 3;
        }

        list.Sort((a, b) =>
        {
            var c = a.SortMinutes.CompareTo(b.SortMinutes);
            return c != 0 ? c : Tie(a).CompareTo(Tie(b));
        });
    }

    /// <summary>Pas de surlignage des créneaux libres le dimanche ni les jours fériés BE.</summary>
    private static bool AllowGapHighlightsForCalendarDay(DateTime day) =>
        day.DayOfWeek != DayOfWeek.Sunday && !BelgianHolidayHelper.TryGetName(day.Date, out _);

    /// <summary>RDV, indisponibilités (rouge), créneaux verts (selon durée formulaire et plages occupées).</summary>
    private List<AgendaLineVm> BuildCalendarDayLines(
        List<AppointmentRow>? appts,
        List<UnavailabilityRow>? unavails,
        bool includeGapHighlights,
        DateTime calendarDay)
    {
        var historicalReadOnly = calendarDay.Date < DateTime.Today;
        var ordered = appts is null ? new List<AppointmentRow>() : appts.OrderBy(x => x.StartTime).ToList();
        var ulist = unavails ?? new List<UnavailabilityRow>();
        var (ws, we) = GetEffectiveWorkdayMinutesForDay(calendarDay.Date);

        if (!includeGapHighlights)
        {
            var lines = new List<AgendaLineVm>();
            foreach (var u in ulist.OrderBy(x => x.StartTime))
            {
                if (!AppointmentScheduling.TryGetUnavailabilityInterval(u, out var us, out var ue)) continue;
                lines.Add(UnavailLineFrom(u, us, ue, historicalReadOnly, calendarDay.Date));
            }

            lines.AddRange(ordered.Select(a => LineFrom(a, historicalReadOnly, calendarDay.Date)));
            SortAgendaLinesChronologically(lines);
            return lines;
        }

        var minBlock = Math.Max(15, DurationMinutes > 0 ? DurationMinutes : 30);
        var busyParts = new List<(int s, int e)>();
        foreach (var a in ordered)
        {
            if (!AppointmentScheduling.TryParseTimeToMinutes(a.StartTime, out var s)) continue;
            var d = a.DurationMinutes > 0 ? a.DurationMinutes : 30;
            var clip = ClipBusyToWorkday(s, s + d, ws, we);
            if (clip != null) busyParts.Add(clip.Value);
        }

        foreach (var u in ulist)
        {
            if (!AppointmentScheduling.TryGetUnavailabilityInterval(u, out var s, out var e)) continue;
            var clip = ClipBusyToWorkday(s, e, ws, we);
            if (clip != null) busyParts.Add(clip.Value);
        }

        var hasLunchForDay = TryGetEffectiveLunchForDay(calendarDay.Date, out var lunchS, out var lunchE);
        if (hasLunchForDay)
        {
            var lunchClip = ClipBusyToWorkday(lunchS, lunchE, ws, we);
            if (lunchClip != null) busyParts.Add(lunchClip.Value);
        }

        var merged = AppointmentScheduling.MergeBusyIntervals(busyParts);
        var result = new List<AgendaLineVm>();

        void addGapIfOk(int gapStart, int gapEnd)
        {
            if (gapEnd <= gapStart) return;
            var len = gapEnd - gapStart;
            if (len < minBlock) return;
            var t0 = AppointmentScheduling.FormatMinutesAsHhMm(gapStart);
            var t1 = AppointmentScheduling.FormatMinutesAsHhMm(gapEnd);
            result.Add(new AgendaLineVm
            {
                AppointmentId = 0,
                IsAvailableGap = true,
                SortMinutes = gapStart,
                LineText = $"{AgendaAvailableSlotLabel()} {t0} – {t1} ({len} min)",
                IsHistoricalReadOnlyDay = historicalReadOnly,
                CalendarOwnerDate = calendarDay.Date
            });
        }

        var cursor = ws;
        foreach (var seg in merged)
        {
            addGapIfOk(cursor, seg.s);
            cursor = Math.Max(cursor, seg.e);
        }

        addGapIfOk(cursor, we);

        foreach (var u in ulist)
        {
            if (!AppointmentScheduling.TryGetUnavailabilityInterval(u, out var us, out var ue)) continue;
            result.Add(UnavailLineFrom(u, us, ue, historicalReadOnly, calendarDay.Date));
        }

        if (hasLunchForDay)
        {
            var lunchClip = ClipBusyToWorkday(lunchS, lunchE, ws, we);
            if (lunchClip != null)
                result.Add(LunchLineFrom(lunchClip.Value.s, lunchClip.Value.e, historicalReadOnly, calendarDay.Date));
        }

        foreach (var a in ordered)
            result.Add(LineFrom(a, historicalReadOnly, calendarDay.Date));

        SortAgendaLinesChronologically(result);
        return result;
    }

    private void OpenUnavailabilityWindow()
    {
        if (IsDayView && AnchorDate.Date < DateTime.Today)
        {
            MessageBox.Show(
                "Les jours passés ne peuvent pas être modifiés (indisponibilités).",
                "Agenda",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var w = new UnavailabilityWindow(
            RefreshCalendar,
            AppointmentDate.Date,
            day =>
            {
                if (TryGetEffectiveLunchForDay(day.Date, out var ls, out var le)) return (true, ls, le);
                return (false, 0, 0);
            },
            GetEffectiveWorkdayMinutesForDay) { Owner = Application.Current?.MainWindow };
        w.ShowDialog();
    }

    /// <summary>Double-clic sur la date d’une case (mois / semaine) : horaires pour ce jour uniquement.</summary>
    public void OnCalendarDateHeaderDoubleClick(DateTime day)
    {
        if (day.Date < DateTime.Today) return;
        var owner = Application.Current?.MainWindow;
        var (effS, effE) = GetEffectiveWorkdayMinutesForDay(day.Date);
        var win = new WorkdayDayOverrideWindow(day.Date, AgendaQuarterHourChoices, effS, effE, UiCulture) { Owner = owner };
        var iso = day.ToString("yyyy-MM-dd");
        if (win.ShowDialog() == true
            && !string.IsNullOrWhiteSpace(win.SavedStartHhMm)
            && !string.IsNullOrWhiteSpace(win.SavedEndHhMm))
        {
            if (AppointmentScheduling.TryParseTimeToMinutes(win.SavedStartHhMm, out var ns)
                && AppointmentScheduling.TryParseTimeToMinutes(win.SavedEndHhMm, out var ne)
                && ns == _workdayStartMin
                && ne == _workdayClosingMin)
            {
                _workdayOverrideRepo.DeleteForDateIso(iso);
                _workdayOverridesByDay.Remove(iso);
            }
            else
            {
                _workdayOverrideRepo.Upsert(iso, win.SavedStartHhMm, win.SavedEndHhMm);
                _workdayOverridesByDay[iso] = new WorkdayDayOverrideRow
                {
                    DateIso = iso,
                    StartTime = win.SavedStartHhMm,
                    EndTime = win.SavedEndHhMm
                };
            }

            RefreshCalendar();
            if (AppointmentDate.Date == day.Date)
            {
                RebuildAppointmentTimeSlots();
                if (EditingId == 0)
                    SuggestNextAvailableStartIfNew();
            }
        }
        else
            AppointmentDate = day.Date;
    }

    /// <summary>Double-clic sur la ligne lunch : déplacer la pause pour ce jour (annulation = date active au formulaire).</summary>
    public void OnCalendarLunchLineDoubleClick(AgendaLineVm line)
    {
        if (line.CalendarOwnerDate is not { } day0) return;
        var day = day0.Date;
        if (day < DateTime.Today || !line.IsLunchBreak) return;
        if (!TryGetEffectiveLunchForDay(day, out var ls, out var le)) return;

        var owner = Application.Current?.MainWindow;
        var iso = day.ToString("yyyy-MM-dd");
        var unavailDay = _unavailRepo.ListForDay(day);
        var seedLunchS = ls;
        var seedLunchE = le;

        outer:
        while (true)
        {
            var lunchWin = new LunchRescheduleDayWindow(day, seedLunchS, seedLunchE, UiCulture) { Owner = owner };
            if (lunchWin.ShowDialog() != true)
            {
                AppointmentDate = day;
                RefreshLunchResetButtonVisibility();
                return;
            }

            if (!AppointmentScheduling.TryParseTimeToMinutes(lunchWin.StartHhMm, out var nLunchS)
                || !AppointmentScheduling.TryParseTimeToMinutes(lunchWin.EndHhMm, out var nLunchE)
                || nLunchE <= nLunchS)
            {
                MessageBox.Show("Plage de lunch invalide.", "Agenda — lunch", MessageBoxButton.OK, MessageBoxImage.Warning);
                continue;
            }

            while (true)
            {
                var sameDay = _repo.ListForDay(day);
                var conflicts = AppointmentScheduling.ListAppointmentsOverlappingLunch(
                    sameDay,
                    nLunchS,
                    nLunchE,
                    null,
                    0,
                    0);

                if (conflicts.Count == 0)
                {
                    _lunchOverrideRepo.UpsertMoved(iso, lunchWin.StartHhMm, lunchWin.EndHhMm);
                    _lunchOverridesByDay[iso] = new LunchDayOverrideRow
                    {
                        DateIso = iso,
                        Mode = "moved",
                        StartTime = lunchWin.StartHhMm,
                        EndTime = lunchWin.EndHhMm
                    };
                    RefreshLunchResetButtonVisibility();
                    RefreshCalendar();
                    if (AppointmentDate.Date == day)
                        RebuildAppointmentTimeSlots();
                    return;
                }

                var first = conflicts[0];
                var detail = $"Rendez-vous concerné : {first.PatientDisplay} à {NormalizeTime(first.StartTime)}.";
                var choiceWin = new LunchVsAppointmentConflictWindow(detail) { Owner = owner };
                if (choiceWin.ShowDialog() != true)
                {
                    AppointmentDate = day;
                    return;
                }

                if (choiceWin.Choice == LunchVsAppointmentConflictWindow.ConflictChoice.ModifyLunch)
                {
                    seedLunchS = nLunchS;
                    seedLunchE = nLunchE;
                    goto outer;
                }

                if (choiceWin.Choice != LunchVsAppointmentConflictWindow.ConflictChoice.MoveAppointment)
                {
                    AppointmentDate = day;
                    return;
                }

                var moveDur = first.DurationMinutes > 0 ? first.DurationMinutes : 30;
                int? earliest = null;
                if (day.Date == DateTime.Today)
                {
                    var n = DateTime.Now;
                    earliest = n.Hour * 60 + n.Minute;
                }

                var (wdCalS, wdCalE) = GetEffectiveWorkdayMinutesForDay(day.Date);
                var slots = AppointmentScheduling.ListAvailableStartTimes(
                    sameDay,
                    unavailDay,
                    moveDur,
                    excludeAppointmentId: first.Id,
                    editingAppointmentId: null,
                    editingStartMin: 0,
                    editingDurationMin: 30,
                    pendingNewRdvNotInDb: false,
                    pendingStartMin: 0,
                    pendingDurationMin: 30,
                    wdCalS,
                    wdCalE,
                    5,
                    earliest,
                    nLunchS,
                    nLunchE,
                    null,
                    null);

                if (slots.Count == 0)
                {
                    var msg = AppointmentScheduling.BuildMessageWhenNoSameDayRelocateSlot(
                        UiCulture,
                        day,
                        moveDur,
                        wdCalS,
                        wdCalE,
                        d => _repo.ListForDay(d),
                        d => _unavailRepo.ListForDay(d),
                        d => TryGetEffectiveLunchForDay(d, out var a, out var b) ? (true, a, b) : (false, 0, 0));
                    MessageBox.Show(msg, "Agenda — lunch", MessageBoxButton.OK, MessageBoxImage.Information);
                    seedLunchS = nLunchS;
                    seedLunchE = nLunchE;
                    goto outer;
                }

                var phone = _patientRepo.GetTelephoneByPatientId(first.PatientId);
                var dateFr = day.ToString("dddd d MMMM yyyy", UiCulture);
                var relocateWin = new AppointmentRelocateSlotWindow(
                    $"Patient : {first.PatientDisplay}",
                    $"Date : {dateFr} — actuellement à {NormalizeTime(first.StartTime)} ({moveDur} min).",
                    slots,
                    phone) { Owner = owner };

                if (relocateWin.ShowDialog() != true)
                {
                    AppointmentDate = day;
                    return;
                }

                var newHhMm = relocateWin.SelectedSlotHhMm;
                if (string.IsNullOrWhiteSpace(newHhMm)) return;

                try
                {
                    _repo.Update(first.Id, first.PatientId, first.TarifId, iso, newHhMm.Trim(), moveDur, first.RecurrenceSeriesId);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "Agenda — déplacement", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                AppointmentResponsibleNotify.ShowNotifyResponsibleDialog(_patientRepo, first.PatientId);
                RefreshCalendar();
            }
        }
    }

    private void RebuildMonthCells(
        Dictionary<string, List<AppointmentRow>> byDay,
        Dictionary<string, List<UnavailabilityRow>> byDayUnavail)
    {
        var first = new DateTime(AnchorDate.Year, AnchorDate.Month, 1);
        var start = StartOfWeekMonday(first);
        var inMonth = AnchorDate.Month;
        var today = DateTime.Today;
        for (int i = 0; i < 42; i++)
        {
            var d = start.AddDays(i);
            var iso = d.ToString("yyyy-MM-dd");
            byDay.TryGetValue(iso, out var list);
            byDayUnavail.TryGetValue(iso, out var ulist);
            var isFerie = BelgianHolidayHelper.TryGetName(d, out var ferieName);
            MonthCells.Add(new AgendaMonthCellVm
            {
                CellDate = d,
                IsCurrentMonth = d.Month == inMonth,
                IsToday = d.Date == today,
                IsPastDay = d.Date < today,
                IsBelgianHoliday = isFerie,
                HolidayName = isFerie ? TranslateHolidayName(ferieName ?? "") : "",
                Lines = BuildCalendarDayLines(
                    list,
                    ulist,
                    AllowGapHighlightsForCalendarDay(d),
                    d.Date)
            });
        }
    }

    private void RebuildWeekColumns(
        Dictionary<string, List<AppointmentRow>> byDay,
        Dictionary<string, List<UnavailabilityRow>> byDayUnavail)
    {
        var start = StartOfWeekMonday(AnchorDate);
        var today = DateTime.Today;
        for (int i = 0; i < 7; i++)
        {
            var d = start.AddDays(i);
            var iso = d.ToString("yyyy-MM-dd");
            byDay.TryGetValue(iso, out var list);
            byDayUnavail.TryGetValue(iso, out var ulist);
            var isFerie = BelgianHolidayHelper.TryGetName(d, out var ferieName);
            WeekColumns.Add(new AgendaWeekColumnVm
            {
                Day = d,
                Header = d.ToString("ddd d/M", UiCulture),
                IsToday = d.Date == today,
                IsPastDay = d.Date < today,
                IsBelgianHoliday = isFerie,
                HolidayName = isFerie ? TranslateHolidayName(ferieName ?? "") : "",
                Lines = BuildCalendarDayLines(
                    list,
                    ulist,
                    AllowGapHighlightsForCalendarDay(d),
                    d.Date)
            });
        }
    }

    private static string TranslateHolidayName(string frName)
    {
        var key = (frName ?? "").Trim();
        if (key.Length == 0) return key;
        if (UiLanguageService.Current == UiLanguageService.Fr) return key;

        return key switch
        {
            "Nouvel An" => UiLanguageService.Current == UiLanguageService.Nl ? "Nieuwjaar" : "New Year's Day",
            "Fête du Travail" => UiLanguageService.Current == UiLanguageService.Nl ? "Dag van de Arbeid" : "Labour Day",
            "Fête nationale" => UiLanguageService.Current == UiLanguageService.Nl ? "Nationale feestdag" : "National Day",
            "Assomption" => UiLanguageService.Current == UiLanguageService.Nl ? "Onze-Lieve-Vrouw Hemelvaart" : "Assumption Day",
            "Toussaint" => UiLanguageService.Current == UiLanguageService.Nl ? "Allerheiligen" : "All Saints' Day",
            "Armistice 1918" => UiLanguageService.Current == UiLanguageService.Nl ? "Wapenstilstand 1918" : "Armistice Day",
            "Noël" => UiLanguageService.Current == UiLanguageService.Nl ? "Kerstmis" : "Christmas Day",
            "Lundi de Pâques" => UiLanguageService.Current == UiLanguageService.Nl ? "Paasmaandag" : "Easter Monday",
            "Ascension" => UiLanguageService.Current == UiLanguageService.Nl ? "Onze-Lieve-Heer Hemelvaart" : "Ascension Day",
            "Lundi de Pentecôte" => UiLanguageService.Current == UiLanguageService.Nl ? "Pinkstermaandag" : "Whit Monday",
            _ => key
        };
    }

    private static string AgendaAvailableSlotLabel()
        => UiLanguageService.Current switch
        {
            UiLanguageService.En => "Available slot",
            UiLanguageService.Nl => "Beschikbaar tijdslot",
            _ => "Créneau disponible"
        };

    private static string AgendaUnavailableLabel()
        => UiLanguageService.Current switch
        {
            UiLanguageService.En => "Unavailable",
            UiLanguageService.Nl => "Onbeschikbaar",
            _ => "Indisponible"
        };

    private static string AgendaLunchLabel()
        => UiLanguageService.Current switch
        {
            UiLanguageService.En => "Lunch",
            UiLanguageService.Nl => "Lunch",
            _ => "Lunch"
        };

    private void RebuildDayLines(
        Dictionary<string, List<AppointmentRow>> byDay,
        Dictionary<string, List<UnavailabilityRow>> byDayUnavail)
    {
        var iso = AnchorDate.ToString("yyyy-MM-dd");
        byDay.TryGetValue(iso, out var list);
        byDayUnavail.TryGetValue(iso, out var ulist);
        foreach (var line in BuildCalendarDayLines(
                     list,
                     ulist,
                     AllowGapHighlightsForCalendarDay(AnchorDate.Date),
                     AnchorDate.Date))
            DayLines.Add(line);
    }

    private void LoadForEdit(long id)
    {
        _suppressTimeSuggest = true;
        var r = _repo.GetById(id);
        if (r is null)
        {
            _selectedAppointmentId = 0;
            ApplyLineSelection();
            _suppressTimeSuggest = false;
            return;
        }

        var apptDay = DateTime.ParseExact(r.DateIso, "yyyy-MM-dd", CultureInfo.InvariantCulture).Date;
        if (apptDay < DateTime.Today)
        {
            MessageBox.Show(MsgRdvPasseNonModifiable, "Agenda", MessageBoxButton.OK, MessageBoxImage.Information);
            _selectedAppointmentId = 0;
            ApplyLineSelection();
            _suppressTimeSuggest = false;
            return;
        }

        EditingId = r.Id;
        AppointmentDate = apptDay;
        SelectedTime = NormalizeTime(r.StartTime);
        DurationMinutes = r.DurationMinutes > 0 ? r.DurationMinutes : 30;
        SelectedPatient = Patients.FirstOrDefault(p => p.Id == r.PatientId) ?? SelectedPatient;
        SelectedTarif = TarifChoices.FirstOrDefault(t => t.Id == r.TarifId) ?? SelectedTarif;
        ApplyDurationChoiceFromMinutes();
        RebuildAppointmentTimeSlots();
        SaveCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
        EncodeRecurringCommand.RaiseCanExecuteChanged();
        _suppressTimeSuggest = false;
        RefreshCalendar();
    }

    private void ApplyDurationChoiceFromMinutes()
    {
        _suppressDurationChoice = true;
        if (DurationMinutes == 30)
        {
            ResetAutrePresetLabel();
            _selectedDurationChoice = DurationPresets[0];
        }
        else if (DurationMinutes == 60)
        {
            ResetAutrePresetLabel();
            _selectedDurationChoice = DurationPresets[1];
        }
        else
        {
            ApplyAutrePresetLabelFromMinutes(DurationMinutes);
            _selectedDurationChoice = DurationPresets[2];
        }

        _suppressDurationChoice = false;
        Raise(nameof(SelectedDurationChoice));
    }

    private void ResetAutrePresetLabel()
        => DurationPresets[2].Label = UiTextTranslator.Translate(AutreDurationLabelDefault);

    private void ApplyAutrePresetLabelFromMinutes(int minutes)
    {
        if (minutes <= 0)
        {
            ResetAutrePresetLabel();
            return;
        }

        DurationPresets[2].Label = FormatCustomDurationDisplay(minutes);
    }

    private static string FormatCustomDurationDisplay(int minutes)
    {
        if (minutes < 60)
            return $"{minutes} {UiTextTranslator.Translate("minutes")}";

        if (minutes % 60 == 0)
        {
            if (UiLanguageService.Current == UiLanguageService.En)
                return minutes / 60 == 1 ? "1 hour" : $"{minutes / 60} hours";
            if (UiLanguageService.Current == UiLanguageService.Nl)
                return minutes / 60 == 1 ? "1 uur" : $"{minutes / 60} uur";
            return minutes / 60 == 1 ? "1 heure" : $"{minutes / 60} heures";
        }

        return $"{minutes / 60} h {minutes % 60} min";
    }

    private void RefreshDurationPresetLabels()
    {
        DurationPresets[0].Label = UiTextTranslator.Translate("30 minutes");
        DurationPresets[1].Label = UiTextTranslator.Translate("1 heure");
        if (_selectedDurationChoice?.PresetMinutes is null && DurationMinutes != 30 && DurationMinutes != 60)
            ApplyAutrePresetLabelFromMinutes(DurationMinutes);
        else
            ResetAutrePresetLabel();
    }

    private static string NormalizeTime(string t)
    {
        t = (t ?? "").Trim();
        if (TimeSpan.TryParse(t, CultureInfo.InvariantCulture, out var ts))
            return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}".Replace(" ", "");
        if (DateTime.TryParseExact(t, new[] { "HH:mm", "H:mm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return dt.ToString("HH:mm");
        return "09:00";
    }

    private void ClearForm()
    {
        _suppressTimeSuggest = true;
        _selectedAppointmentId = 0;
        ApplyLineSelection();
        EditingId = 0;
        var ad = AnchorDate.Date;
        AppointmentDate = ad < DateTime.Today ? DateTime.Today : ad;
        DurationMinutes = 30;
        ApplyDurationChoiceFromMinutes();
        _suppressTimeSuggest = false;
        SuggestNextAvailableStartIfNew();
        SaveCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
        EncodeRecurringCommand.RaiseCanExecuteChanged();
        RebuildAppointmentTimeSlots();
        RefreshCalendar();
    }

    private void LoadWorkdaySettingsFromStore()
    {
        var (start, close) = _appSettings.LoadAgendaWorkdayMinutes();
        _workdayStartMin = start;
        _workdayClosingMin = close;
        _workdaySettingStartDisplay = AppointmentScheduling.FormatMinutesAsHhMm(start);
        _workdaySettingClosingDisplay = AppointmentScheduling.FormatMinutesAsHhMm(close);
    }

    private void LoadLunchFromStore()
    {
        var (en, st, et, sm, em) = _appSettings.LoadAgendaLunch();
        _lunchBreakEnabled = en;
        _lunchStartDisplay = st;
        _lunchEndDisplay = et;
        _lunchStartMin = sm;
        _lunchEndMin = em;
    }

    private void OnLunchSettingsChanged()
    {
        if (EditingId == 0 && !_suppressTimeSuggest)
            SuggestNextAvailableStartIfNew();
        RefreshCalendar();
    }

    private LunchDayOverrideRow? ResolveLunchOverrideRow(string dateIso)
    {
        if (_lunchOverridesByDay.TryGetValue(dateIso, out var cached)) return cached;
        var row = _lunchOverrideRepo.GetForDateIso(dateIso);
        if (row is not null)
            _lunchOverridesByDay[dateIso] = row;
        return row;
    }

    private WorkdayDayOverrideRow? ResolveWorkdayOverrideRow(string dateIso)
    {
        if (_workdayOverridesByDay.TryGetValue(dateIso, out var cached)) return cached;
        var row = _workdayOverrideRepo.GetForDateIso(dateIso);
        if (row is not null)
            _workdayOverridesByDay[dateIso] = row;
        return row;
    }

    /// <summary>Début et fin de journée effectifs : exception pour ce jour ou réglage global.</summary>
    private (int startMin, int endMin) GetEffectiveWorkdayMinutesForDay(DateTime day)
    {
        var iso = day.ToString("yyyy-MM-dd");
        var ov = ResolveWorkdayOverrideRow(iso);
        if (ov is not null
            && AppointmentScheduling.TryParseTimeToMinutes(ov.StartTime, out var s)
            && AppointmentScheduling.TryParseTimeToMinutes(ov.EndTime, out var e)
            && e > s + 14)
            return (s, e);
        return (_workdayStartMin, _workdayClosingMin);
    }

    /// <summary>Lunch effectif pour une date : réglage récurrent, sauf dimanche / férié BE / exception jour (omit ou moved).</summary>
    private bool TryGetEffectiveLunchForDay(DateTime day, out int startMin, out int endMin)
    {
        startMin = 0;
        endMin = 0;
        if (day.DayOfWeek == DayOfWeek.Sunday) return false;
        if (BelgianHolidayHelper.TryGetName(day.Date, out _)) return false;

        var iso = day.ToString("yyyy-MM-dd");
        var ov = ResolveLunchOverrideRow(iso);
        if (ov is not null)
        {
            if (string.Equals(ov.Mode, "omit", StringComparison.OrdinalIgnoreCase))
                return false;
            if (string.Equals(ov.Mode, "moved", StringComparison.OrdinalIgnoreCase)
                && AppointmentScheduling.TryParseTimeToMinutes(ov.StartTime, out var s)
                && AppointmentScheduling.TryParseTimeToMinutes(ov.EndTime, out var e)
                && e > s)
            {
                startMin = s;
                endMin = e;
                return true;
            }

            return false;
        }

        if (!_lunchBreakEnabled || _lunchEndMin <= _lunchStartMin) return false;
        startMin = _lunchStartMin;
        endMin = _lunchEndMin;
        return true;
    }

    /// <summary>Si le RDV empiète sur le lunch : confirmations puis omit ou moved (avec résolution des RDV déjà planifiés sous le nouveau lunch). False = abandon.</summary>
    private bool ResolveLunchOverlapForSave(
        string dateIso,
        DateTime day,
        int startMin,
        int durSave,
        IReadOnlyList<UnavailabilityRow> unavailDay)
    {
        if (!TryGetEffectiveLunchForDay(day, out var ls, out var le)) return true;
        if (!AppointmentScheduling.OverlapsHalfOpenBlock(startMin, durSave, ls, le)) return true;

        var owner = Application.Current?.MainWindow;
        var editingIdNullable = EditingId > 0 ? EditingId : (long?)null;

        var r1 = ShowActionChoice(
            "Agenda — lunch",
            "Attention, ce créneau empiète sur votre temps de lunch.",
            "Confirmer le rendez-vous",
            "Annuler l'encodage");
        if (r1 != ActionChoiceResult.Primary) return false;

        var r2 = ShowActionChoice(
            "Agenda — lunch",
            "Choisissez l'action à appliquer pour ce jour.",
            "Déplacer le lunch",
            "Supprimer le lunch du jour");

        if (r2 == ActionChoiceResult.Cancel)
            return false;

        if (r2 == ActionChoiceResult.Secondary)
        {
            _lunchOverrideRepo.UpsertOmit(dateIso);
            _lunchOverridesByDay[dateIso] = new LunchDayOverrideRow { DateIso = dateIso, Mode = "omit" };
            RefreshLunchResetButtonVisibility();
            return true;
        }

        var seedLunchS = ls;
        var seedLunchE = le;

        outer:
        while (true)
        {
            var lunchWin = new LunchRescheduleDayWindow(day, seedLunchS, seedLunchE, UiCulture) { Owner = owner };
            if (lunchWin.ShowDialog() != true) return false;
            if (!AppointmentScheduling.TryParseTimeToMinutes(lunchWin.StartHhMm, out var nLunchS)
                || !AppointmentScheduling.TryParseTimeToMinutes(lunchWin.EndHhMm, out var nLunchE)
                || nLunchE <= nLunchS)
            {
                MessageBox.Show("Plage de lunch invalide.", "Agenda — lunch", MessageBoxButton.OK, MessageBoxImage.Warning);
                continue;
            }

            while (true)
            {
                var sameDay = _repo.ListForDay(day);
                var conflicts = AppointmentScheduling.ListAppointmentsOverlappingLunch(
                    sameDay,
                    nLunchS,
                    nLunchE,
                    editingIdNullable,
                    startMin,
                    durSave);

                if (conflicts.Count == 0)
                {
                    _lunchOverrideRepo.UpsertMoved(dateIso, lunchWin.StartHhMm, lunchWin.EndHhMm);
                    _lunchOverridesByDay[dateIso] = new LunchDayOverrideRow
                    {
                        DateIso = dateIso,
                        Mode = "moved",
                        StartTime = lunchWin.StartHhMm,
                        EndTime = lunchWin.EndHhMm
                    };
                    RefreshLunchResetButtonVisibility();
                    return true;
                }

                var first = conflicts[0];
                var detail = $"Rendez-vous concerné : {first.PatientDisplay} à {NormalizeTime(first.StartTime)}.";
                var choiceWin = new LunchVsAppointmentConflictWindow(detail) { Owner = owner };
                if (choiceWin.ShowDialog() != true) return false;

                if (choiceWin.Choice == LunchVsAppointmentConflictWindow.ConflictChoice.ModifyLunch)
                {
                    seedLunchS = nLunchS;
                    seedLunchE = nLunchE;
                    goto outer;
                }

                if (choiceWin.Choice != LunchVsAppointmentConflictWindow.ConflictChoice.MoveAppointment)
                    return false;

                var moveDur = first.DurationMinutes > 0 ? first.DurationMinutes : 30;
                int? earliest = null;
                if (day.Date == DateTime.Today)
                {
                    var n = DateTime.Now;
                    earliest = n.Hour * 60 + n.Minute;
                }

                var (wdLunchS, wdLunchE) = GetEffectiveWorkdayMinutesForDay(day.Date);
                var slots = AppointmentScheduling.ListAvailableStartTimes(
                    sameDay,
                    unavailDay,
                    moveDur,
                    excludeAppointmentId: first.Id,
                    editingAppointmentId: editingIdNullable,
                    editingStartMin: startMin,
                    editingDurationMin: durSave,
                    pendingNewRdvNotInDb: EditingId == 0,
                    pendingStartMin: startMin,
                    pendingDurationMin: durSave,
                    wdLunchS,
                    wdLunchE,
                    5,
                    earliest,
                    nLunchS,
                    nLunchE);

                if (slots.Count == 0)
                {
                    var msg = AppointmentScheduling.BuildMessageWhenNoSameDayRelocateSlot(
                        UiCulture,
                        day,
                        moveDur,
                        wdLunchS,
                        wdLunchE,
                        d => _repo.ListForDay(d),
                        d => _unavailRepo.ListForDay(d),
                        d => TryGetEffectiveLunchForDay(d, out var ls, out var le) ? (true, ls, le) : (false, 0, 0));
                    MessageBox.Show(msg, "Agenda — lunch", MessageBoxButton.OK, MessageBoxImage.Information);
                    seedLunchS = nLunchS;
                    seedLunchE = nLunchE;
                    goto outer;
                }

                var phone = _patientRepo.GetTelephoneByPatientId(first.PatientId);
                var dateFr = day.ToString("dddd d MMMM yyyy", UiCulture);
                var relocateWin = new AppointmentRelocateSlotWindow(
                    $"Patient : {first.PatientDisplay}",
                    $"Date : {dateFr} — actuellement à {NormalizeTime(first.StartTime)} ({moveDur} min).",
                    slots,
                    phone) { Owner = owner };

                if (relocateWin.ShowDialog() != true) return false;
                var newHhMm = relocateWin.SelectedSlotHhMm;
                if (string.IsNullOrWhiteSpace(newHhMm)) return false;

                try
                {
                    _repo.Update(first.Id, first.PatientId, first.TarifId, dateIso, newHhMm.Trim(), moveDur, first.RecurrenceSeriesId);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "Agenda — déplacement", MessageBoxButton.OK, MessageBoxImage.Error);
                    return false;
                }

                AppointmentResponsibleNotify.ShowNotifyResponsibleDialog(_patientRepo, first.PatientId);
                // Revérifier d’éventuels autres RDV sous le même lunch
            }
        }
    }

    private void OnWorkdaySettingsApplied()
    {
        RebuildAppointmentTimeSlots();
        Raise(nameof(WorkdaySettingStart));
        Raise(nameof(WorkdaySettingClosing));
        if (EditingId == 0)
        {
            if (!TimeSlots.Contains(SelectedTime))
                SuggestNextAvailableStartIfNew();
        }
        RefreshCalendar();
    }

    /// <summary>Créneaux de début (pas de 15 min) pour la durée courante ; conserve l’heure du RDV en cours d’édition si hors plage.</summary>
    private void RebuildAppointmentTimeSlots()
    {
        TimeSlots.Clear();
        var dur = Math.Max(15, DurationMinutes);
        var (dayStart, dayClose) = GetEffectiveWorkdayMinutesForDay(AppointmentDate.Date);
        for (var t = dayStart; t + dur <= dayClose; t += 15)
            TimeSlots.Add(AppointmentScheduling.FormatMinutesAsHhMm(t));

        if (EditingId > 0
            && AppointmentScheduling.TryParseTimeToMinutes(SelectedTime, out _)
            && !TimeSlots.Contains(SelectedTime))
            TimeSlots.Add(SelectedTime);

        var sorted = TimeSlots.OrderBy(s =>
            AppointmentScheduling.TryParseTimeToMinutes(s, out var m) ? m : 9999).ToList();
        TimeSlots.Clear();
        foreach (var s in sorted)
            TimeSlots.Add(s);
    }

    /// <summary>Nouveau RDV uniquement : premier créneau libre après le dernier RDV de la journée (pas 15 min, aligné sur la liste « Heure de début »).</summary>
    private void SuggestNextAvailableStartIfNew()
    {
        if (_suppressTimeSuggest || EditingId > 0) return;
        var list = _repo.ListForDay(AppointmentDate);
        var unavail = _unavailRepo.ListForDay(AppointmentDate);
        const int slotStepMin = 15;
        int? earliest = null;
        if (AppointmentDate.Date == DateTime.Today)
        {
            var n = DateTime.Now;
            earliest = n.Hour * 60 + n.Minute;
        }

        var lastApptEndMin = 0;
        foreach (var a in list)
        {
            if (!AppointmentScheduling.TryParseTimeToMinutes(a.StartTime, out var sm)) continue;
            var d = a.DurationMinutes > 0 ? a.DurationMinutes : 30;
            lastApptEndMin = Math.Max(lastApptEndMin, sm + d);
        }

        if (lastApptEndMin > 0)
        {
            var afterLast = (lastApptEndMin + slotStepMin - 1) / slotStepMin * slotStepMin;
            earliest = earliest.HasValue ? Math.Max(earliest.Value, afterLast) : afterLast;
        }

        int? lunchS = null, lunchE = null;
        if (TryGetEffectiveLunchForDay(AppointmentDate.Date, out var ls, out var le))
        {
            lunchS = ls;
            lunchE = le;
        }

        var (wds, wdc) = GetEffectiveWorkdayMinutesForDay(AppointmentDate.Date);
        var slot = AppointmentScheduling.FindFirstAvailableStart(
            list,
            unavail,
            DurationMinutes,
            null,
            wds,
            wdc,
            slotStepMin,
            earliest,
            lunchS,
            lunchE);
        if (slot != null)
            SelectedTime = slot;
    }

    private enum RecurringBlockKind
    {
        Appointment,
        Unavailability,
        Lunch
    }

    private sealed class RecurringSlotConflict
    {
        public required DateTime Day { get; init; }
        public RecurringBlockKind Kind { get; init; }
        public AppointmentRow? BlockingAppointment { get; init; }
        public UnavailabilityRow? BlockingUnavailability { get; init; }
        public int LunchStartMin { get; init; }
        public int LunchEndMin { get; init; }
    }

    /// <summary>Exclut uniquement dimanche, férié, passé et hors plage journée — indispo et lunch sont des conflits à traiter comme un RDV.</summary>
    private bool IsRecurringDayCalendarFilteredOut(DateTime day, int startMin, int durationMin)
    {
        if (day.DayOfWeek == DayOfWeek.Sunday) return true;
        if (BelgianHolidayHelper.TryGetName(day.Date, out _)) return true;
        if (day.Date < DateTime.Today) return true;
        if (day.Date == DateTime.Today)
        {
            var nowMin = DateTime.Now.Hour * 60 + DateTime.Now.Minute;
            if (startMin < nowMin) return true;
        }

        var (wdFiltS, wdFiltE) = GetEffectiveWorkdayMinutesForDay(day.Date);
        return startMin < wdFiltS || startMin + durationMin > wdFiltE;
    }

    private List<string> ListSlotsForRecurringOccurrence(DateTime day, int durationMin, long? excludeOtherAppointmentId)
    {
        int? earliest = day.Date == DateTime.Today ? DateTime.Now.Hour * 60 + DateTime.Now.Minute : null;
        var same = _repo.ListForDay(day);
        var unavail = _unavailRepo.ListForDay(day);
        int? lunchS = null, lunchE = null;
        if (TryGetEffectiveLunchForDay(day.Date, out var ls, out var le))
        {
            lunchS = ls;
            lunchE = le;
        }

        var (wdRecS, wdRecE) = GetEffectiveWorkdayMinutesForDay(day.Date);
        return AppointmentScheduling.ListAvailableStartTimes(
            same,
            unavail,
            durationMin,
            excludeOtherAppointmentId,
            null,
            0,
            0,
            false,
            0,
            0,
            wdRecS,
            wdRecE,
            5,
            earliest,
            lunchS,
            lunchE,
            null,
            null);
    }

    private List<string> ListSlotsForMovingBlocker(DateTime day, AppointmentRow blocker, int reservedStartMin, int reservedDur)
    {
        int? earliest = day.Date == DateTime.Today ? DateTime.Now.Hour * 60 + DateTime.Now.Minute : null;
        var same = _repo.ListForDay(day);
        var unavail = _unavailRepo.ListForDay(day);
        int? lunchS = null, lunchE = null;
        if (TryGetEffectiveLunchForDay(day.Date, out var ls, out var le))
        {
            lunchS = ls;
            lunchE = le;
        }

        var bDur = blocker.DurationMinutes > 0 ? blocker.DurationMinutes : 30;
        var (wdBlkS, wdBlkE) = GetEffectiveWorkdayMinutesForDay(day.Date);
        return AppointmentScheduling.ListAvailableStartTimes(
            same,
            unavail,
            bDur,
            blocker.Id,
            null,
            0,
            0,
            true,
            reservedStartMin,
            reservedDur,
            wdBlkS,
            wdBlkE,
            5,
            earliest,
            lunchS,
            lunchE,
            null,
            null);
    }

    /// <summary>Vérifie si le créneau récurrent (heure + durée) est libre ce jour (hors filtre calendaire strict).</summary>
    private bool CanInsertRecurringSlotAt(DateTime day, int startMin, int durationMin)
    {
        if (IsRecurringDayCalendarFilteredOut(day, startMin, durationMin)) return false;
        var same = _repo.ListForDay(day);
        if (AppointmentScheduling.TryGetFirstOverlappingAppointment(same, startMin, durationMin, null) is not null)
            return false;
        var unavList = _unavailRepo.ListForDay(day);
        if (AppointmentScheduling.TryGetFirstOverlappingUnavailability(unavList, startMin, durationMin) is not null)
            return false;
        return !TryGetEffectiveLunchForDay(day.Date, out var ls, out var le)
               || !AppointmentScheduling.OverlapsHalfOpenBlock(startMin, durationMin, ls, le);
    }

    private void EncodeRecurring()
    {
        var owner = Application.Current?.MainWindow;
        if (SelectedPatient is null || SelectedTarif is null) return;
        if (!AppointmentScheduling.TryParseTimeToMinutes(SelectedTime, out var startMin))
        {
            MessageBox.Show("Heure invalide (utilisez le format HH:mm).", "Agenda", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var (wdEncS, wdEncE) = GetEffectiveWorkdayMinutesForDay(AppointmentDate.Date);
        if (startMin < wdEncS || startMin + DurationMinutes > wdEncE)
        {
            var d0 = AppointmentScheduling.FormatMinutesAsHhMm(wdEncS);
            var d1 = AppointmentScheduling.FormatMinutesAsHhMm(wdEncE);
            MessageBox.Show(
                $"L’heure de début doit être entre {d0} et une heure telle que la séance se termine au plus tard à {d1}.",
                "Agenda",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var today = DateTime.Today;
        if (AppointmentDate.Date < today)
        {
            MessageBox.Show(MsgRdvDansLePasse, "Agenda", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (AppointmentDate.Date == today)
        {
            var nowMin = DateTime.Now.Hour * 60 + DateTime.Now.Minute;
            if (startMin < nowMin)
            {
                MessageBox.Show(MsgRdvDansLePasse, "Agenda", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }

        if (BelgianHolidayHelper.TryGetName(AppointmentDate.Date, out var ferieName))
        {
            var msg = $"Cette date est un jour férié en Belgique : {ferieName}.\n\n" +
                      "Souhaitez-vous tout de même utiliser cette date comme premier rendez-vous de la série ?";
            if (ShowActionChoice("Agenda — jour férié", msg, "Continuer", "Annuler") != ActionChoiceResult.Primary)
                return;
        }

        if (AppointmentDate.DayOfWeek == DayOfWeek.Sunday)
        {
            const string msgDimanche = "La date sélectionnée correspond à un dimanche.";
            if (ShowActionChoice("Agenda", msgDimanche, "Continuer", "Annuler") != ActionChoiceResult.Primary)
                return;
        }

        var durSave = DurationMinutes > 0 ? DurationMinutes : 30;
        var iso = AppointmentDate.ToString("yyyy-MM-dd");
        var unavailDay = _unavailRepo.ListForDay(AppointmentDate);
        if (AppointmentScheduling.OverlapsUnavailability(unavailDay, startMin, durSave))
        {
            MessageBox.Show(
                "Impossible d’utiliser ce créneau comme premier rendez-vous : vous êtes indisponible.",
                "Agenda",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (!ResolveLunchOverlapForSave(iso, AppointmentDate.Date, startMin, durSave, unavailDay))
            return;

        var excludeId = EditingId > 0 ? EditingId : (long?)null;
        var sameDay = _repo.ListForDay(AppointmentDate);
        if (AppointmentScheduling.HasOverlap(sameDay, startMin, durSave, excludeId))
        {
            MessageBox.Show(
                "Ce créneau chevauche un autre rendez-vous. Corrigez l’heure ou le jour avant un encodage récurrent.",
                "Agenda",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var dlg = new RecurringAppointmentDialog(AppointmentDate.Date) { Owner = owner };
        if (dlg.ShowDialog() != true || dlg.Result is null)
            return;

        var w = dlg.Result;
        var dates = RecurrenceDateGenerator.Generate(
            AppointmentDate.Date,
            w.Kind,
            w.FixedWeekday,
            w.FixedDayOfMonth,
            w.LimitByEndDate,
            w.EndDateInclusive,
            w.OccurrenceCount);

        if (dates.Count == 0)
        {
            MessageBox.Show("Aucune date ne correspond aux critères choisis.", "Agenda — récurrence", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var seriesId = Guid.NewGuid().ToString("N");
        var timeStr = AppointmentScheduling.FormatMinutesAsHhMm(startMin);
        var patientId = SelectedPatient.Id;
        var tarifId = SelectedTarif.Id;
        var created = 0;
        var skippedOther = 0;
        var conflicts = new List<RecurringSlotConflict>();

        try
        {
            if (EditingId > 0)
            {
                var exist = _repo.GetById(EditingId);
                if (exist is not null)
                    _repo.Update(EditingId, patientId, tarifId, iso, timeStr, DurationMinutes, seriesId);
            }

            foreach (var d in dates)
            {
                if (EditingId > 0 && d.Date == AppointmentDate.Date)
                    continue;

                if (IsRecurringDayCalendarFilteredOut(d, startMin, durSave))
                {
                    skippedOther++;
                    continue;
                }

                var same = _repo.ListForDay(d);
                var unavList = _unavailRepo.ListForDay(d);

                var apptBlock = AppointmentScheduling.TryGetFirstOverlappingAppointment(same, startMin, durSave, null);
                if (apptBlock is not null)
                {
                    conflicts.Add(new RecurringSlotConflict
                    {
                        Day = d.Date,
                        Kind = RecurringBlockKind.Appointment,
                        BlockingAppointment = apptBlock
                    });
                    continue;
                }

                var unb = AppointmentScheduling.TryGetFirstOverlappingUnavailability(unavList, startMin, durSave);
                if (unb is not null)
                {
                    conflicts.Add(new RecurringSlotConflict
                    {
                        Day = d.Date,
                        Kind = RecurringBlockKind.Unavailability,
                        BlockingUnavailability = unb
                    });
                    continue;
                }

                if (TryGetEffectiveLunchForDay(d.Date, out var lunchS, out var lunchE)
                    && AppointmentScheduling.OverlapsHalfOpenBlock(startMin, durSave, lunchS, lunchE))
                {
                    conflicts.Add(new RecurringSlotConflict
                    {
                        Day = d.Date,
                        Kind = RecurringBlockKind.Lunch,
                        LunchStartMin = lunchS,
                        LunchEndMin = lunchE
                    });
                    continue;
                }

                var dIsoIns = d.ToString("yyyy-MM-dd");
                _repo.Insert(patientId, tarifId, dIsoIns, timeStr, DurationMinutes, seriesId);
                created++;
            }

            if (conflicts.Count > 0)
            {
                var summary = new StringBuilder();
                summary.AppendLine($"Nombre de chevauchements (RDV, indisponibilité ou lunch) : {conflicts.Count}");
                summary.AppendLine();
                summary.AppendLine("Détail par occurrence :");
                summary.AppendLine();
                foreach (var c in conflicts)
                {
                    var ds = c.Day.ToString("dddd d MMMM yyyy", UiCulture);
                    var ourT = AppointmentScheduling.FormatMinutesAsHhMm(startMin);
                    switch (c.Kind)
                    {
                        case RecurringBlockKind.Appointment:
                        {
                            var b = c.BlockingAppointment!;
                            var bt = NormalizeTime(b.StartTime);
                            var bm = b.DurationMinutes > 0 ? b.DurationMinutes : 30;
                            summary.AppendLine(
                                $"• {ds} : votre série à {ourT} ({durSave} min) — chevauche le RDV « {b.PatientDisplay} » à {bt} ({bm} min).");
                            break;
                        }
                        case RecurringBlockKind.Unavailability:
                        {
                            var u = c.BlockingUnavailability!;
                            var line =
                                $"• {ds} : votre série à {ourT} ({durSave} min) — chevauche une indisponibilité ({NormalizeTime(u.StartTime)} – {NormalizeTime(u.EndTime)})";
                            if (!string.IsNullOrWhiteSpace(u.Reason))
                                line += $" — {u.Reason.Trim()}";
                            summary.AppendLine(line + ".");
                            break;
                        }
                        case RecurringBlockKind.Lunch:
                        {
                            var l1 = AppointmentScheduling.FormatMinutesAsHhMm(c.LunchStartMin);
                            var l2 = AppointmentScheduling.FormatMinutesAsHhMm(c.LunchEndMin);
                            summary.AppendLine(
                                $"• {ds} : votre série à {ourT} ({durSave} min) — chevauche la pause déjeuner ({l1} – {l2}).");
                            break;
                        }
                    }
                }

                MessageBox.Show(
                    summary.ToString(),
                    "Agenda — récurrence (chevauchements)",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                foreach (var c in conflicts)
                {
                    var day = c.Day;
                    var dIso = day.ToString("yyyy-MM-dd");
                    var dateTitle = day.ToString("dddd d MMMM yyyy", UiCulture);
                    var telR = _patientRepo.GetTelephoneByPatientId(patientId);
                    var phoneR = string.IsNullOrWhiteSpace(telR) ? "" : InternationalPhoneFormatter.FormatForDisplay(telR);

                    var detail = "";
                    var phoneOther = "";
                    string? otherLabel = null;
                    string? otherSub = null;
                    var blockingCaption = c.Kind switch
                    {
                        RecurringBlockKind.Appointment => "Déplacer le RDV déjà encodé à un autre horaire",
                        RecurringBlockKind.Unavailability => "Modifier / déplacer l’indisponibilité",
                        RecurringBlockKind.Lunch => "Déplacer la pause déjeuner (ce jour)",
                        _ => "Déplacer le blocage"
                    };

                    switch (c.Kind)
                    {
                        case RecurringBlockKind.Appointment:
                        {
                            var blocker = c.BlockingAppointment!;
                            detail =
                                $"Récurrence prévue : {SelectedPatient.Display} à {timeStr} ({durSave} min).\n" +
                                $"Conflit avec le RDV déjà encodé : {blocker.PatientDisplay} à {NormalizeTime(blocker.StartTime)} " +
                                $"({(blocker.DurationMinutes > 0 ? blocker.DurationMinutes : 30)} min).";
                            var telO = _patientRepo.GetTelephoneByPatientId(blocker.PatientId);
                            phoneOther = string.IsNullOrWhiteSpace(telO) ? "" : InternationalPhoneFormatter.FormatForDisplay(telO);
                            break;
                        }
                        case RecurringBlockKind.Unavailability:
                        {
                            var u = c.BlockingUnavailability!;
                            detail =
                                $"Récurrence prévue : {SelectedPatient.Display} à {timeStr} ({durSave} min).\n" +
                                $"Conflit avec une indisponibilité : {NormalizeTime(u.StartTime)} – {NormalizeTime(u.EndTime)}";
                            if (!string.IsNullOrWhiteSpace(u.Reason))
                                detail += $" ({u.Reason.Trim()})";
                            detail +=
                                ".\nCette plage est traitée comme un rendez-vous bloquant (même logique de chevauchement).";
                            otherLabel = "Blocage — indisponibilité";
                            otherSub =
                                "Pas de numéro patient : vous pouvez déplacer cette plage (bouton ci-dessous) ou déplacer la récurrence.";
                            break;
                        }
                        case RecurringBlockKind.Lunch:
                        {
                            var l1 = AppointmentScheduling.FormatMinutesAsHhMm(c.LunchStartMin);
                            var l2 = AppointmentScheduling.FormatMinutesAsHhMm(c.LunchEndMin);
                            detail =
                                $"Récurrence prévue : {SelectedPatient.Display} à {timeStr} ({durSave} min).\n" +
                                $"Conflit avec la pause déjeuner : {l1} – {l2}.\n" +
                                "Traitée comme un rendez-vous bloquant (même logique de chevauchement).";
                            otherLabel = "Blocage — pause déjeuner";
                            otherSub =
                                "Pas de numéro pour le lunch : vous pouvez le déplacer pour ce jour (bouton ci-dessous) ou déplacer la récurrence.";
                            break;
                        }
                    }

                    var cw = new RecurringOccurrenceConflictWindow(
                        dateTitle,
                        detail,
                        phoneR,
                        phoneOther,
                        blockingCaption,
                        otherLabel,
                        otherSub) { Owner = owner };
                    cw.ShowDialog();

                    long? excludeApptId = c.Kind == RecurringBlockKind.Appointment ? c.BlockingAppointment!.Id : null;

                    if (cw.Decision == RecurringConflictDecision.MoveRecurring)
                    {
                        var slotsR = ListSlotsForRecurringOccurrence(day, durSave, excludeApptId);
                        if (slotsR.Count == 0)
                        {
                            var (wdOccS, wdOccE) = GetEffectiveWorkdayMinutesForDay(day.Date);
                            var msg = AppointmentScheduling.BuildMessageWhenNoSameDayRelocateSlot(
                                UiCulture,
                                day,
                                durSave,
                                wdOccS,
                                wdOccE,
                                x => _repo.ListForDay(x),
                                x => _unavailRepo.ListForDay(x),
                                x => TryGetEffectiveLunchForDay(x, out var a, out var b) ? (true, a, b) : (false, 0, 0));
                            MessageBox.Show(msg, "Agenda — récurrence", MessageBoxButton.OK, MessageBoxImage.Information);
                        }
                        else
                        {
                            var rw = new AppointmentRelocateSlotWindow(
                                $"Patient (série) : {SelectedPatient.Display}",
                                $"Date : {dateTitle} — autre horaire pour la récurrence ({durSave} min).",
                                slotsR,
                                telR) { Owner = owner };
                            if (rw.ShowDialog() == true && !string.IsNullOrWhiteSpace(rw.SelectedSlotHhMm))
                            {
                                _repo.Insert(patientId, tarifId, dIso, rw.SelectedSlotHhMm.Trim(), DurationMinutes, seriesId);
                                created++;
                            }
                        }
                    }
                    else if (cw.Decision == RecurringConflictDecision.MoveExisting)
                    {
                        switch (c.Kind)
                        {
                            case RecurringBlockKind.Appointment:
                            {
                                var blocker = c.BlockingAppointment!;
                                var moveDurB = blocker.DurationMinutes > 0 ? blocker.DurationMinutes : 30;
                                var telOb = _patientRepo.GetTelephoneByPatientId(blocker.PatientId);
                                var slotsB = ListSlotsForMovingBlocker(day, blocker, startMin, durSave);
                                if (slotsB.Count == 0)
                                {
                                    var (wdBlkOccS, wdBlkOccE) = GetEffectiveWorkdayMinutesForDay(day.Date);
                                    var msg2 = AppointmentScheduling.BuildMessageWhenNoSameDayRelocateSlot(
                                        UiCulture,
                                        day,
                                        moveDurB,
                                        wdBlkOccS,
                                        wdBlkOccE,
                                        x => _repo.ListForDay(x),
                                        x => _unavailRepo.ListForDay(x),
                                        x => TryGetEffectiveLunchForDay(x, out var a, out var b) ? (true, a, b) : (false, 0, 0));
                                    MessageBox.Show(msg2, "Agenda — récurrence", MessageBoxButton.OK, MessageBoxImage.Information);
                                }
                                else
                                {
                                    var bw = new AppointmentRelocateSlotWindow(
                                        $"Patient : {blocker.PatientDisplay}",
                                        $"Date : {dateTitle} — actuellement à {NormalizeTime(blocker.StartTime)} ({moveDurB} min).",
                                        slotsB,
                                        telOb) { Owner = owner };
                                    if (bw.ShowDialog() == true && !string.IsNullOrWhiteSpace(bw.SelectedSlotHhMm))
                                    {
                                        _repo.Update(
                                            blocker.Id,
                                            blocker.PatientId,
                                            blocker.TarifId,
                                            dIso,
                                            bw.SelectedSlotHhMm.Trim(),
                                            moveDurB,
                                            blocker.RecurrenceSeriesId);
                                        _repo.Insert(patientId, tarifId, dIso, timeStr, DurationMinutes, seriesId);
                                        created++;
                                    }
                                }

                                break;
                            }
                            case RecurringBlockKind.Unavailability:
                            {
                                var uw = new UnavailabilityRescheduleWindow(day, c.BlockingUnavailability!, UiCulture) { Owner = owner };
                                if (uw.ShowDialog() == true)
                                {
                                    _unavailRepo.Update(c.BlockingUnavailability!.Id, uw.StartHhMm, uw.EndHhMm, uw.ReasonText);
                                    RefreshCalendar();
                                    if (CanInsertRecurringSlotAt(day, startMin, durSave))
                                    {
                                        _repo.Insert(patientId, tarifId, dIso, timeStr, DurationMinutes, seriesId);
                                        created++;
                                    }
                                    else
                                    {
                                        MessageBox.Show(
                                            "Après modification de l’indisponibilité, le créneau de la récurrence chevauche encore une contrainte (autre plage, RDV ou lunch). Ajustez à nouveau ou déplacez la récurrence.",
                                            "Agenda — récurrence",
                                            MessageBoxButton.OK,
                                            MessageBoxImage.Information);
                                    }
                                }

                                break;
                            }
                            case RecurringBlockKind.Lunch:
                            {
                                var lunchWin = new LunchRescheduleDayWindow(day, c.LunchStartMin, c.LunchEndMin, UiCulture) { Owner = owner };
                                if (lunchWin.ShowDialog() == true)
                                {
                                    _lunchOverrideRepo.UpsertMoved(dIso, lunchWin.StartHhMm, lunchWin.EndHhMm);
                                    _lunchOverridesByDay[dIso] = new LunchDayOverrideRow
                                    {
                                        DateIso = dIso,
                                        Mode = "moved",
                                        StartTime = lunchWin.StartHhMm,
                                        EndTime = lunchWin.EndHhMm
                                    };
                                    RefreshLunchResetButtonVisibility();
                                    RefreshCalendar();
                                    if (CanInsertRecurringSlotAt(day, startMin, durSave))
                                    {
                                        _repo.Insert(patientId, tarifId, dIso, timeStr, DurationMinutes, seriesId);
                                        created++;
                                    }
                                    else
                                    {
                                        MessageBox.Show(
                                            "Après déplacement du lunch, le créneau de la récurrence chevauche encore une contrainte. Ajustez le lunch ou déplacez la récurrence.",
                                            "Agenda — récurrence",
                                            MessageBoxButton.OK,
                                            MessageBoxImage.Information);
                                    }
                                }

                                break;
                            }
                        }
                    }
                }
            }

            RefreshCalendar();
            if (EditingId > 0)
                LoadForEdit(EditingId);
            else
                ClearForm();

            if (created > 0 || EditingId > 0)
                AppointmentResponsibleNotify.ShowNotifyResponsibleDialog(_patientRepo, patientId);

            var recap = new StringBuilder();
            recap.AppendLine("Série enregistrée.");
            recap.AppendLine();
            if (EditingId > 0)
                recap.AppendLine("• Rendez-vous existant rattaché à la série");
            recap.AppendLine($"• {created} créneau(x) au total enregistré(s) pour cette série (sur les dates traitées)");
            recap.AppendLine($"• {skippedOther} date(s) ignorée(s) (dimanche, férié ou hors plage de journée)");
            if (conflicts.Count > 0)
                recap.AppendLine($"• {conflicts.Count} chevauchement(s) (RDV, indisponibilité ou lunch — voir les fenêtres de résolution si besoin)");

            MessageBox.Show(recap.ToString(), "Agenda — récurrence", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Agenda — récurrence", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CopyDayRecurringUntilDate()
    {
        var sourceDay = AppointmentDate.Date;
        if (sourceDay < DateTime.Today)
            return;

        var sourceRows = _repo.ListForDay(sourceDay).OrderBy(r => r.StartTime).ToList();
        if (sourceRows.Count == 0)
        {
            MessageBox.Show("Aucun rendez-vous à copier pour cette journée.", "Agenda — copie journée", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var picker = new CopyDayUntilDateWindow(sourceDay)
        {
            Owner = Application.Current?.MainWindow
        };
        if (picker.ShowDialog() != true)
            return;

        var endDate = picker.EndDateInclusive.Date;
        if (endDate <= sourceDay)
        {
            MessageBox.Show("La date de fin doit être postérieure à la journée source.", "Agenda — copie journée", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var created = 0;
        var skipped = 0;
        var checkedDays = 0;

        try
        {
            for (var day = sourceDay.AddDays(7); day <= endDate; day = day.AddDays(7))
            {
                checkedDays++;
                if (day.Date < DateTime.Today)
                {
                    skipped += sourceRows.Count;
                    continue;
                }

                if (day.DayOfWeek == DayOfWeek.Sunday || BelgianHolidayHelper.TryGetName(day.Date, out _))
                {
                    skipped += sourceRows.Count;
                    continue;
                }

                var sameDayAppts = _repo.ListForDay(day);
                var dayUnav = _unavailRepo.ListForDay(day);
                var dayIso = day.ToString("yyyy-MM-dd");
                var dayBusy = new List<AppointmentRow>(sameDayAppts);

                foreach (var src in sourceRows)
                {
                    if (!AppointmentScheduling.TryParseTimeToMinutes(src.StartTime, out var startMin))
                    {
                        skipped++;
                        continue;
                    }

                    var dur = src.DurationMinutes > 0 ? src.DurationMinutes : 30;
                    var (wdCopyS, wdCopyE) = GetEffectiveWorkdayMinutesForDay(day.Date);
                    if (startMin < wdCopyS || startMin + dur > wdCopyE)
                    {
                        skipped++;
                        continue;
                    }

                    if (AppointmentScheduling.HasOverlap(dayBusy, startMin, dur, null)
                        || AppointmentScheduling.OverlapsUnavailability(dayUnav, startMin, dur)
                        || (TryGetEffectiveLunchForDay(day.Date, out var ls, out var le)
                            && AppointmentScheduling.OverlapsHalfOpenBlock(startMin, dur, ls, le)))
                    {
                        skipped++;
                        continue;
                    }

                    var hhmm = AppointmentScheduling.FormatMinutesAsHhMm(startMin);
                    var newId = _repo.Insert(src.PatientId, src.TarifId, dayIso, hhmm, dur, src.RecurrenceSeriesId);
                    created++;
                    dayBusy.Add(new AppointmentRow
                    {
                        Id = newId,
                        PatientId = src.PatientId,
                        TarifId = src.TarifId,
                        DateIso = dayIso,
                        StartTime = hhmm,
                        DurationMinutes = dur,
                        PatientNom = src.PatientNom,
                        PatientPrenom = src.PatientPrenom,
                        RecurrenceSeriesId = src.RecurrenceSeriesId
                    });
                }
            }

            RefreshCalendar();
            MessageBox.Show(
                $"Copie terminée.\n\n• Jours traités : {checkedDays}\n• Rendez-vous copiés : {created}\n• Entrées ignorées (conflit / indisponibilité / lunch / hors plage / férié-dimanche) : {skipped}",
                "Agenda — copie journée",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Agenda — copie journée", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DeleteSelectedDay()
    {
        var selectedDay = AppointmentDate.Date;
        if (selectedDay < DateTime.Today)
            return;

        var dayRows = _repo.ListForDay(selectedDay).ToList();
        if (dayRows.Count == 0)
        {
            MessageBox.Show("Aucun rendez-vous à supprimer sur cette journée.", "Agenda — suppression journée", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var scope = ShowActionChoice(
            "Agenda — suppression journée",
            "Choisissez la portée :\n\n- Supprimer uniquement cette journée\n- Supprimer aussi les occurrences récurrentes futures liées à cette journée",
            "Supprimer uniquement cette journée",
            "Supprimer la journée + récurrences futures");

        if (scope == ActionChoiceResult.Cancel)
            return;

        var idsToDelete = new HashSet<long>();
        foreach (var row in dayRows)
            idsToDelete.Add(row.Id);

        if (scope == ActionChoiceResult.Secondary)
        {
            var fromIso = selectedDay.ToString("yyyy-MM-dd");
            var distinctSeries = dayRows
                .Select(r => r.RecurrenceSeriesId)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            foreach (var seriesId in distinctSeries)
            {
                foreach (var id in _repo.ListIdsInRecurrenceSeriesFromDate(seriesId, fromIso))
                    idsToDelete.Add(id);
            }

            // Fallback pour les RDV copiés sans serie explicite :
            // supprimer les occurrences futures "liées" par même patient/tarif/heure/durée.
            var futureRows = _repo.ListBetweenInclusive(fromIso, "9999-12-31")
                .Where(r =>
                {
                    if (!DateTime.TryParseExact(r.DateIso, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                        return false;
                    return d.Date >= selectedDay && d.Date >= DateTime.Today;
                })
                .ToList();

            foreach (var src in dayRows)
            {
                foreach (var fr in futureRows)
                {
                    if (fr.PatientId != src.PatientId) continue;
                    if (fr.TarifId != src.TarifId) continue;
                    if (!string.Equals(NormalizeTime(fr.StartTime), NormalizeTime(src.StartTime), StringComparison.Ordinal)) continue;
                    if ((fr.DurationMinutes > 0 ? fr.DurationMinutes : 30) != (src.DurationMinutes > 0 ? src.DurationMinutes : 30)) continue;
                    idsToDelete.Add(fr.Id);
                }
            }
        }

        if (idsToDelete.Count == 0)
            return;

        if (ShowActionChoice(
                "Agenda — confirmation",
                $"Confirmer la suppression de {idsToDelete.Count} rendez-vous ?",
                "Confirmer",
                "Annuler") != ActionChoiceResult.Primary)
            return;

        try
        {
            foreach (var id in idsToDelete)
            {
                _repo.Delete(id);
                _seanceSvc.DeleteSeancesLinkedToAppointment(id);
            }

            AgendaAppointmentDeleted?.Invoke(selectedDay);
            RefreshCalendar();
            ClearForm();
            MessageBox.Show("Suppression effectuée.", "Agenda — suppression journée", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Agenda — suppression journée", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Save()
    {
        if (SelectedPatient is null || SelectedTarif is null) return;
        if (!AppointmentScheduling.TryParseTimeToMinutes(SelectedTime, out var startMin))
        {
            MessageBox.Show("Heure invalide (utilisez le format HH:mm).", "Agenda", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (EditingId == 0)
        {
            var (wdSaveS, wdSaveE) = GetEffectiveWorkdayMinutesForDay(AppointmentDate.Date);
            if (startMin < wdSaveS || startMin + DurationMinutes > wdSaveE)
            {
                var d0 = AppointmentScheduling.FormatMinutesAsHhMm(wdSaveS);
                var d1 = AppointmentScheduling.FormatMinutesAsHhMm(wdSaveE);
                MessageBox.Show(
                    $"Pour un nouveau rendez-vous, l’heure de début doit être entre {d0} et une heure telle que la séance se termine au plus tard à {d1} (selon la durée choisie).",
                    "Agenda",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }
        }

        var today = DateTime.Today;
        if (AppointmentDate.Date < today)
        {
            MessageBox.Show(MsgRdvDansLePasse, "Agenda", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (AppointmentDate.Date == today)
        {
            var now = DateTime.Now;
            var nowMin = now.Hour * 60 + now.Minute;
            if (startMin < nowMin)
            {
                MessageBox.Show(MsgRdvDansLePasse, "Agenda", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }

        if (BelgianHolidayHelper.TryGetName(AppointmentDate.Date, out var ferieName))
        {
            var msg = $"Cette date est un jour férié en Belgique : {ferieName}.\n\n" +
                      "Souhaitez-vous tout de même enregistrer ce rendez-vous ?";
            if (ShowActionChoice("Agenda — jour férié", msg, "Enregistrer", "Annuler") != ActionChoiceResult.Primary)
                return;
        }

        if (AppointmentDate.DayOfWeek == DayOfWeek.Sunday)
        {
            const string msgDimanche = "La date sélectionnée correspond à un dimanche.";
            if (ShowActionChoice("Agenda", msgDimanche, "Continuer", "Annuler") != ActionChoiceResult.Primary)
                return;
        }

        var sameDay = _repo.ListForDay(AppointmentDate);
        var unavailDay = _unavailRepo.ListForDay(AppointmentDate);
        if (AppointmentScheduling.OverlapsUnavailability(unavailDay, startMin, DurationMinutes))
        {
            MessageBox.Show(
                "Impossible de réserver ce créneau, vous êtes indisponible",
                "Agenda",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var durSave = DurationMinutes > 0 ? DurationMinutes : 30;
        var iso = AppointmentDate.ToString("yyyy-MM-dd");
        if (!ResolveLunchOverlapForSave(iso, AppointmentDate.Date, startMin, durSave, unavailDay))
            return;

        sameDay = _repo.ListForDay(AppointmentDate);
        if (AppointmentScheduling.HasOverlap(sameDay, startMin, DurationMinutes, EditingId > 0 ? EditingId : null))
        {
            MessageBox.Show(
                "Ce créneau chevauche un autre rendez-vous : même heure de début ou plages horaires qui se recouvrent.",
                "Agenda",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var timeStr = AppointmentScheduling.FormatMinutesAsHhMm(startMin);
        var patientIdForNotify = SelectedPatient.Id;
        try
        {
            var splitOneHourInTwo = false;
            if (EditingId == 0 && DurationMinutes == 60)
            {
                splitOneHourInTwo =
                    ShowActionChoice(
                        "Agenda — séance 1 heure",
                        "Souhaitez-vous scinder cette séance d'1 heure en 2 séances consécutives de 30 minutes pour le même patient ?",
                        "Scinder en 2 x 30 min",
                        "Garder 1 séance de 1 h") == ActionChoiceResult.Primary;
            }

            if (EditingId > 0)
            {
                var existing = _repo.GetById(EditingId);
                _repo.Update(EditingId, SelectedPatient.Id, SelectedTarif.Id, iso, timeStr, DurationMinutes, existing?.RecurrenceSeriesId);
            }
            else if (splitOneHourInTwo)
            {
                var secondStartMin = startMin + 30;
                var secondTime = AppointmentScheduling.FormatMinutesAsHhMm(secondStartMin);

                // Double sécurité avant insertion des 2 créneaux 30 min.
                if (AppointmentScheduling.HasOverlap(sameDay, startMin, 30, null)
                    || AppointmentScheduling.HasOverlap(sameDay, secondStartMin, 30, null)
                    || AppointmentScheduling.OverlapsUnavailability(unavailDay, startMin, 30)
                    || AppointmentScheduling.OverlapsUnavailability(unavailDay, secondStartMin, 30))
                {
                    MessageBox.Show(
                        "Impossible de scinder : un conflit est détecté sur au moins un des deux créneaux de 30 minutes.",
                        "Agenda",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                if (TryGetEffectiveLunchForDay(AppointmentDate.Date, out var ls2, out var le2)
                    && (AppointmentScheduling.OverlapsHalfOpenBlock(startMin, 30, ls2, le2)
                        || AppointmentScheduling.OverlapsHalfOpenBlock(secondStartMin, 30, ls2, le2)))
                {
                    MessageBox.Show(
                        "Impossible de scinder : au moins un des deux créneaux de 30 minutes chevauche la pause lunch.",
                        "Agenda",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                _repo.Insert(SelectedPatient.Id, SelectedTarif.Id, iso, timeStr, 30);
                _repo.Insert(SelectedPatient.Id, SelectedTarif.Id, iso, secondTime, 30);
            }
            else
                _repo.Insert(SelectedPatient.Id, SelectedTarif.Id, iso, timeStr, DurationMinutes);
            RefreshCalendar();
            ClearForm();
            AppointmentResponsibleNotify.ShowNotifyResponsibleDialog(_patientRepo, patientIdForNotify);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Agenda — enregistrement", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Delete()
    {
        if (EditingId <= 0) return;
        var row = _repo.GetById(EditingId);
        var patientLabel = row?.PatientDisplay ?? SelectedPatient?.Display ?? "(patient inconnu)";
        var dateLabel = row != null
            ? DateTime.ParseExact(row.DateIso, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("dddd d MMMM yyyy", UiCulture)
            : AppointmentDate.ToString("dddd d MMMM yyyy", UiCulture);
        var timeLabel = row != null ? NormalizeTime(row.StartTime) : SelectedTime;
        var msg = $"Supprimer le rendez-vous suivant ?\n\nPatient : {patientLabel}\n{dateLabel} à {timeLabel}";
        if (ShowActionChoice("Agenda", msg, "Supprimer", "Annuler") != ActionChoiceResult.Primary)
            return;
        var apptId = EditingId;
        var deletedDay = row != null
            ? DateTime.ParseExact(row.DateIso, "yyyy-MM-dd", CultureInfo.InvariantCulture).Date
            : AppointmentDate.Date;
        var patientIdForNotify = row?.PatientId ?? SelectedPatient?.Id ?? 0L;
        var seriesId = row?.RecurrenceSeriesId;
        if (!string.IsNullOrWhiteSpace(seriesId))
        {
            var scopeWindow = new RecurrenceDeleteScopeWindow
            {
                Owner = Application.Current?.MainWindow
            };
            var scope = scopeWindow.ShowDialog() == true
                ? scopeWindow.Scope
                : RecurrenceDeleteScope.Cancel;

            if (scope == RecurrenceDeleteScope.Cancel)
                return;
            if (scope == RecurrenceDeleteScope.CurrentAndFollowing)
            {
                try
                {
                    var fromIso = row!.DateIso;
                    foreach (var id in _repo.ListIdsInRecurrenceSeriesFromDate(seriesId, fromIso))
                    {
                        _seanceSvc.DeleteSeancesLinkedToAppointment(id);
                        _repo.Delete(id);
                    }

                    AgendaAppointmentDeleted?.Invoke(deletedDay);
                    RefreshCalendar();
                    ClearForm();
                    AppointmentResponsibleNotify.ShowNotifyResponsibleDialog(_patientRepo, patientIdForNotify);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "Agenda", MessageBoxButton.OK, MessageBoxImage.Error);
                }

                return;
            }
        }

        try
        {
            _repo.Delete(apptId);
            _seanceSvc.DeleteSeancesLinkedToAppointment(apptId);
            AgendaAppointmentDeleted?.Invoke(deletedDay);
            RefreshCalendar();
            ClearForm();
            AppointmentResponsibleNotify.ShowNotifyResponsibleDialog(_patientRepo, patientIdForNotify);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Agenda", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static ActionChoiceResult ShowActionChoice(
        string title,
        string message,
        string primaryLabel,
        string secondaryLabel,
        string? cancelLabel = null)
    {
        var win = new ActionChoiceWindow(title, message, primaryLabel, secondaryLabel, cancelLabel)
        {
            Owner = Application.Current?.MainWindow
        };
        return win.ShowDialog() == true ? win.Choice : ActionChoiceResult.Cancel;
    }

    private static DateTime StartOfWeekMonday(DateTime d)
    {
        var day = (int)d.DayOfWeek;
        var diff = day == (int)DayOfWeek.Sunday ? -6 : 1 - day;
        return d.Date.AddDays(diff);
    }
}
