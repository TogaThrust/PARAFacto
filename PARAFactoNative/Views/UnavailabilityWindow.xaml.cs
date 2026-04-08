using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using PARAFactoNative.Services;

namespace PARAFactoNative.Views;

public partial class UnavailabilityWindow : Window
{
    private static readonly CultureInfo FrBe = CultureInfo.GetCultureInfo("fr-BE");

    private readonly Action? _refreshParent;
    private readonly UnavailabilityRepo _unavailRepo = new();
    private readonly AppointmentRepo _apptRepo = new();
    private readonly PatientRepo _patientRepo = new();
    private readonly Func<DateTime, (bool hasLunch, int lunchStart, int lunchEnd)> _getEffectiveLunch;
    private readonly Func<DateTime, (int startMin, int endMin)> _getEffectiveWorkday;

    private sealed class UnavailListItem
    {
        public required UnavailabilityRow Row { get; init; }
        public string Display { get; init; } = "";
    }

    public UnavailabilityWindow(
        Action? refreshParent,
        DateTime initialDate,
        Func<DateTime, (bool hasLunch, int lunchStart, int lunchEnd)> getEffectiveLunch,
        Func<DateTime, (int startMin, int endMin)> getEffectiveWorkday)
    {
        InitializeComponent();
        _refreshParent = refreshParent;
        _getEffectiveLunch = getEffectiveLunch;
        _getEffectiveWorkday = getEffectiveWorkday;

        var times = Enumerable.Range(0, 96).Select(i => FormatHhMm(i * 15)).ToList();
        StartCombo.ItemsSource = times;
        DatePick.SelectedDate = initialDate.Date;
        ApplyDefaultStartEndForSelectedDay();
        ReloadList();
    }

    private void ApplyDefaultStartEndForSelectedDay()
    {
        var d = DatePick.SelectedDate;
        if (d is null) return;
        if (StartCombo.ItemsSource is not IEnumerable<string> src)
            return;
        var times = src as IList<string> ?? src.ToList();

        var (ws, we) = _getEffectiveWorkday(d.Value.Date);
        var snappedStart = Math.Clamp((ws / 15) * 15, 0, 23 * 60 + 45);
        var startStr = FormatHhMm(snappedStart);
        StartCombo.SelectedItem = times.Contains(startStr) ? startStr : times.FirstOrDefault() ?? startStr;
        RebuildEndComboForStart();
        PickDefaultEndForClosing(we);
    }

    private void PickDefaultEndForClosing(int closingMin)
    {
        if (EndCombo.ItemsSource is not IEnumerable<string> endsEnum) return;
        var ends = endsEnum.ToList();
        if (ends.Count == 0) return;
        string? best = null;
        var bestDiff = int.MaxValue;
        foreach (var e in ends)
        {
            if (!TimeSpan.TryParse(e.Trim(), CultureInfo.InvariantCulture, out var ts)) continue;
            var m = (int)ts.TotalMinutes;
            if (m > closingMin) continue;
            var diff = closingMin - m;
            if (diff < bestDiff)
            {
                bestDiff = diff;
                best = e;
            }
        }

        EndCombo.SelectedItem = best ?? ends[^1];
    }

    private static string FormatHhMm(int minutesSinceMidnight)
    {
        var h = minutesSinceMidnight / 60;
        var m = minutesSinceMidnight % 60;
        return $"{h:00}:{m:00}";
    }

    private void RebuildEndComboForStart()
    {
        if (StartCombo.SelectedItem is not string ss
            || !TimeSpan.TryParse(ss.Trim(), CultureInfo.InvariantCulture, out var ts))
        {
            EndCombo.ItemsSource = Array.Empty<string>();
            return;
        }

        var startM = (int)ts.TotalMinutes;
        var ends = Enumerable.Range(0, 96)
            .Select(i => i * 15)
            .Where(m => m > startM)
            .Select(FormatHhMm)
            .ToList();
        EndCombo.ItemsSource = ends;
        if (EndCombo.SelectedItem is not string cur || !ends.Contains(cur))
            EndCombo.SelectedItem = ends.FirstOrDefault();
    }

    private void StartCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e) => RebuildEndComboForStart();

    private void DatePick_OnSelectedDateChanged(object? sender, SelectionChangedEventArgs e)
    {
        ApplyDefaultStartEndForSelectedDay();
        ReloadList();
    }

    private void ReloadList()
    {
        var d = DatePick.SelectedDate;
        if (d is null)
        {
            UnavailList.ItemsSource = Array.Empty<UnavailListItem>();
            return;
        }

        var rows = _unavailRepo.ListForDay(d.Value);
        UnavailList.ItemsSource = rows.Select(r =>
        {
            var reason = string.IsNullOrWhiteSpace(r.Reason) ? "" : $" — {r.Reason.Trim()}";
            return new UnavailListItem
            {
                Row = r,
                Display = $"{r.StartTime} – {r.EndTime}{reason}"
            };
        }).ToList();
    }

    private static string NormalizeTimeDisplay(string t)
    {
        t = (t ?? "").Trim();
        if (TimeSpan.TryParse(t, CultureInfo.InvariantCulture, out var ts))
            return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}";
        return t;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var day = DatePick.SelectedDate;
        if (day is null)
        {
            MessageBox.Show("Choisissez une date.", "Indisponibilité", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (StartCombo.SelectedItem is not string startStr || EndCombo.SelectedItem is not string endStr)
        {
            MessageBox.Show("Choisissez l’heure de début et de fin.", "Indisponibilité", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TimeSpan.TryParse(startStr.Trim(), CultureInfo.InvariantCulture, out var tsStart)
            || !TimeSpan.TryParse(endStr.Trim(), CultureInfo.InvariantCulture, out var tsEnd))
        {
            MessageBox.Show("Heures invalides.", "Indisponibilité", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var startMin = (int)tsStart.TotalMinutes;
        var endMin = (int)tsEnd.TotalMinutes;
        if (endMin <= startMin)
        {
            MessageBox.Show("L’heure de fin doit être après l’heure de début.", "Indisponibilité", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var iso = day.Value.ToString("yyyy-MM-dd");
        var owner = Owner ?? Application.Current?.MainWindow;
        var (wds, wdc) = _getEffectiveWorkday(day.Value.Date);

        while (true)
        {
            var sameDay = _apptRepo.ListForDay(day.Value);
            var unavailDay = _unavailRepo.ListForDay(day.Value);
            var conflicts = AppointmentScheduling.ListAppointmentsOverlappingLunch(
                sameDay,
                startMin,
                endMin,
                null,
                0,
                0);

            if (conflicts.Count == 0)
            {
                try
                {
                    _unavailRepo.Insert(iso, startStr.Trim(), endStr.Trim(), ReasonBox.Text);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "Indisponibilité", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                ReasonBox.Clear();
                _refreshParent?.Invoke();
                ReloadList();
                return;
            }

            var first = conflicts[0];
            var detail = $"Rendez-vous concerné : {first.PatientDisplay} à {NormalizeTimeDisplay(first.StartTime)}.";
            var choiceWin = new LunchVsAppointmentConflictWindow(
                detail,
                "Cette plage d’indisponibilité empiète sur un rendez-vous existant. Souhaitez-vous modifier l’indisponibilité ou déplacer le rendez-vous ?",
                "Modifier l’indisponibilité",
                "Déplacer le rendez-vous",
                "Indisponibilité — conflit") { Owner = owner };

            if (choiceWin.ShowDialog() != true)
                return;

            if (choiceWin.Choice == LunchVsAppointmentConflictWindow.ConflictChoice.ModifyLunch)
                return;

            if (choiceWin.Choice != LunchVsAppointmentConflictWindow.ConflictChoice.MoveAppointment)
                return;

            var moveDur = first.DurationMinutes > 0 ? first.DurationMinutes : 30;
            int? earliest = null;
            if (day.Value.Date == DateTime.Today)
            {
                var n = DateTime.Now;
                earliest = n.Hour * 60 + n.Minute;
            }

            var (hasLunch, lunchS, lunchE) = _getEffectiveLunch(day.Value.Date);
            int? lunchBlockS = hasLunch ? lunchS : null;
            int? lunchBlockE = hasLunch ? lunchE : null;

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
                wds,
                wdc,
                5,
                earliest,
                lunchBlockS,
                lunchBlockE,
                startMin,
                endMin);

            if (slots.Count == 0)
            {
                var msg = AppointmentScheduling.BuildMessageWhenNoSameDayRelocateSlot(
                    FrBe,
                    day.Value,
                    moveDur,
                    wds,
                    wdc,
                    d => _apptRepo.ListForDay(d),
                    d => _unavailRepo.ListForDay(d),
                    _getEffectiveLunch);
                MessageBox.Show(msg, "Indisponibilité", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var phone = _patientRepo.GetTelephoneByPatientId(first.PatientId);
            var dateFr = day.Value.ToString("dddd d MMMM yyyy", FrBe);
            var relocateWin = new AppointmentRelocateSlotWindow(
                $"Patient : {first.PatientDisplay}",
                $"Date : {dateFr} — actuellement à {NormalizeTimeDisplay(first.StartTime)} ({moveDur} min).",
                slots,
                phone) { Owner = owner };

            if (relocateWin.ShowDialog() != true)
                return;
            var newHhMm = relocateWin.SelectedSlotHhMm;
            if (string.IsNullOrWhiteSpace(newHhMm)) return;

            try
            {
                _apptRepo.Update(first.Id, first.PatientId, first.TarifId, iso, newHhMm.Trim(), moveDur, first.RecurrenceSeriesId);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Indisponibilité — déplacement", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            AppointmentResponsibleNotify.ShowNotifyResponsibleDialog(_patientRepo, first.PatientId, "Indisponibilité");
            _refreshParent?.Invoke();
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (UnavailList.SelectedItem is not UnavailListItem item)
        {
            MessageBox.Show("Sélectionnez une ligne à supprimer.", "Indisponibilité", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!ChoiceDialog.AskYesNo(
                "Indisponibilité",
                "Supprimer cette indisponibilité ?",
                "Supprimer",
                "Annuler",
                this))
            return;

        try
        {
            _unavailRepo.Delete(item.Row.Id);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Indisponibilité", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _refreshParent?.Invoke();
        ReloadList();
    }
}
