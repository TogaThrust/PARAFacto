using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PARAFactoNative.Models;

namespace PARAFactoNative.Services;

/// <summary>Heures de début / durées et détection des chevauchements entre RDV.</summary>
public static class AppointmentScheduling
{
    public static bool TryParseTimeToMinutes(string? startTime, out int minutesSinceMidnight)
    {
        minutesSinceMidnight = 0;
        if (string.IsNullOrWhiteSpace(startTime)) return false;
        if (!TimeSpan.TryParse(startTime.Trim(), CultureInfo.InvariantCulture, out var ts)) return false;
        var m = (int)ts.TotalMinutes;
        if (m < 0 || m >= 24 * 60) return false;
        minutesSinceMidnight = m;
        return true;
    }

    public static string FormatMinutesAsHhMm(int minutesSinceMidnight)
    {
        var h = minutesSinceMidnight / 60;
        var m = minutesSinceMidnight % 60;
        return $"{h:00}:{m:00}";
    }

    /// <summary>True si [startMin, startMin+durationMin) chevauche un RDV existant (hors <paramref name="excludeAppointmentId"/>).</summary>
    public static bool HasOverlap(
        IReadOnlyList<AppointmentRow> sameDay,
        int startMin,
        int durationMin,
        long? excludeAppointmentId)
    {
        if (durationMin <= 0) return true;
        var endNew = startMin + durationMin;
        foreach (var a in sameDay)
        {
            if (excludeAppointmentId.HasValue && a.Id == excludeAppointmentId.Value) continue;
            if (!TryParseTimeToMinutes(a.StartTime, out var s)) continue;
            var dur = a.DurationMinutes > 0 ? a.DurationMinutes : 30;
            var e = s + dur;
            if (startMin < e && endNew > s) return true;
        }
        return false;
    }

    /// <summary>Parse une plage d’indisponibilité ; <paramref name="endMin"/> est exclusif (fin de plage).</summary>
    public static bool TryGetUnavailabilityInterval(UnavailabilityRow u, out int startMin, out int endMin)
    {
        startMin = 0;
        endMin = 0;
        if (!TryParseTimeToMinutes(u.StartTime, out var s)) return false;
        if (!TryParseTimeToMinutes(u.EndTime, out var e)) return false;
        if (e <= s) return false;
        startMin = s;
        endMin = e;
        return true;
    }

    public static bool OverlapsUnavailability(IReadOnlyList<UnavailabilityRow>? sameDay, int startMin, int durationMin)
    {
        if (sameDay is null || sameDay.Count == 0) return false;
        if (durationMin <= 0) durationMin = 30;
        var endNew = startMin + durationMin;
        foreach (var u in sameDay)
        {
            if (!TryGetUnavailabilityInterval(u, out var s, out var e)) continue;
            if (startMin < e && endNew > s) return true;
        }
        return false;
    }

    /// <summary>True si [startMin, startMin+durationMin) chevauche [blockStart, blockEnd) (fin exclusive).</summary>
    public static bool OverlapsHalfOpenBlock(int startMin, int durationMin, int blockStart, int blockEnd)
    {
        if (blockEnd <= blockStart) return false;
        if (durationMin <= 0) durationMin = 30;
        var endNew = startMin + durationMin;
        return startMin < blockEnd && endNew > blockStart;
    }

    /// <summary>Fusionne des intervalles [début, fin) qui se chevauchent ou se touchent.</summary>
    public static List<(int s, int e)> MergeBusyIntervals(IReadOnlyList<(int s, int e)> intervals)
    {
        var sorted = intervals.Where(x => x.e > x.s).OrderBy(x => x.s).ToList();
        var merged = new List<(int s, int e)>();
        foreach (var seg in sorted)
        {
            if (merged.Count == 0)
            {
                merged.Add(seg);
                continue;
            }

            var last = merged[^1];
            if (seg.s < last.e)
                merged[^1] = (last.s, Math.Max(last.e, seg.e));
            else
                merged.Add(seg);
        }

        return merged;
    }

    private static int CeilToStep(int value, int step) =>
        (value + step - 1) / step * step;

    /// <summary>
    /// Premier début possible sans chevauchement (RDV ni indisponibilité), par pas de <paramref name="stepMinutes"/> minutes.
    /// Le créneau suivant peut commencer dès la fin du précédent ; la durée réservée est <paramref name="durationMinutes"/>.
    /// </summary>
    /// <param name="earliestStartMinInclusive">Si renseigné (ex. « maintenant » pour aujourd’hui), premier créneau ≥ cette minute, aligné au pas.</param>
    public static string? FindFirstAvailableStart(
        IReadOnlyList<AppointmentRow> sameDay,
        IReadOnlyList<UnavailabilityRow>? unavailabilitySameDay,
        int durationMinutes,
        long? excludeAppointmentId,
        int dayStartMin,
        int closingEndMin,
        int stepMinutes = 5,
        int? earliestStartMinInclusive = null,
        int? lunchBlockStartMin = null,
        int? lunchBlockEndMin = null)
    {
        if (durationMinutes <= 0) durationMinutes = 30;
        if (closingEndMin <= dayStartMin) return null;
        var t0 = dayStartMin;
        if (earliestStartMinInclusive.HasValue)
            t0 = Math.Max(t0, CeilToStep(earliestStartMinInclusive.Value, stepMinutes));
        var lunchStart = lunchBlockStartMin ?? -1;
        var lunchEnd = lunchBlockEndMin ?? -1;
        var checkLunch = lunchBlockStartMin.HasValue && lunchBlockEndMin.HasValue && lunchEnd > lunchStart;
        for (var t = t0; t + durationMinutes <= closingEndMin; t += stepMinutes)
        {
            if (!HasOverlap(sameDay, t, durationMinutes, excludeAppointmentId)
                && !OverlapsUnavailability(unavailabilitySameDay, t, durationMinutes)
                && (!checkLunch || !OverlapsHalfOpenBlock(t, durationMinutes, lunchStart, lunchEnd)))
                return FormatMinutesAsHhMm(t);
        }
        return null;
    }

    /// <summary>Formate « HH:mm » en libellé du type « 9 h 15 » (fr).</summary>
    public static string FormatHhMmFrenchHour(string hhMm)
    {
        if (!TryParseTimeToMinutes(hhMm, out var t)) return hhMm;
        var h = t / 60;
        var m = t % 60;
        return m == 0 ? $"{h} h" : $"{h} h {m:D2}";
    }

    /// <summary>
    /// Premier jour (à partir de <paramref name="searchStartDate"/>, inclus) où un créneau de <paramref name="durationMinutes"/> est libre.
    /// Exclut dimanches et jours fériés BE, comme la réservation dans l’agenda.
    /// </summary>
    public static (DateTime date, string startHhMm)? FindFirstAvailableSlotFromDateInclusive(
        DateTime searchStartDate,
        int maxCalendarDaysToScan,
        int durationMinutes,
        int dayStartMin,
        int closingEndMin,
        int stepMinutes,
        Func<DateTime, IReadOnlyList<AppointmentRow>> getAppointments,
        Func<DateTime, IReadOnlyList<UnavailabilityRow>> getUnavailability,
        Func<DateTime, (bool hasLunch, int lunchS, int lunchE)> getEffectiveLunch)
    {
        if (maxCalendarDaysToScan <= 0) return null;
        if (durationMinutes <= 0) durationMinutes = 30;
        var today = DateTime.Today;

        for (var i = 0; i < maxCalendarDaysToScan; i++)
        {
            var d = searchStartDate.Date.AddDays(i);
            if (d < today) continue;
            if (d.DayOfWeek == DayOfWeek.Sunday) continue;
            if (BelgianHolidayHelper.TryGetName(d, out _)) continue;

            int? earliest = null;
            if (d == today)
            {
                var n = DateTime.Now;
                earliest = n.Hour * 60 + n.Minute;
            }

            var sameDay = getAppointments(d);
            var unav = getUnavailability(d);
            var lunchInfo = getEffectiveLunch(d);
            int? lunchS = lunchInfo.hasLunch ? lunchInfo.lunchS : null;
            int? lunchE = lunchInfo.hasLunch ? lunchInfo.lunchE : null;

            var slot = FindFirstAvailableStart(
                sameDay,
                unav,
                durationMinutes,
                excludeAppointmentId: null,
                dayStartMin,
                closingEndMin,
                stepMinutes,
                earliest,
                lunchS,
                lunchE);

            if (slot != null)
                return (d, slot);
        }

        return null;
    }

    /// <summary>
    /// Message utilisateur lorsqu’aucun créneau n’est libre le jour du conflit mais qu’on propose une date ultérieure (fenêtre d’environ six mois).
    /// </summary>
    public static string BuildMessageWhenNoSameDayRelocateSlot(
        CultureInfo cultureFr,
        DateTime conflictDay,
        int moveDurationMinutes,
        int dayStartMin,
        int closingEndMin,
        Func<DateTime, IReadOnlyList<AppointmentRow>> getAppointments,
        Func<DateTime, IReadOnlyList<UnavailabilityRow>> getUnavailability,
        Func<DateTime, (bool hasLunch, int lunchS, int lunchE)> getEffectiveLunch,
        int horizonDays = 180)
    {
        var found = FindFirstAvailableSlotFromDateInclusive(
            conflictDay.Date.AddDays(1),
            horizonDays,
            moveDurationMinutes,
            dayStartMin,
            closingEndMin,
            5,
            getAppointments,
            getUnavailability,
            getEffectiveLunch);

        if (found is null)
            return "Aucun créneau de remplacement n'a été trouvé pour ce jour. Aucune date libre ne correspond dans les six prochains mois.";

        var dateStr = found.Value.date.ToString("dddd d MMMM yyyy", cultureFr);
        var timeStr = FormatHhMmFrenchHour(found.Value.startHhMm);
        return $"Aucun créneau de remplacement n'a été trouvé pour ce jour. Le premier créneau disponible est à la date du {dateStr} à {timeStr}.";
    }

    /// <summary>RDV dont [début, début+dur) chevauche [lunchStart, lunchEnd) (fin exclusive). Utilise l’heure du formulaire pour le RDV en cours d’édition.</summary>
    public static List<AppointmentRow> ListAppointmentsOverlappingLunch(
        IReadOnlyList<AppointmentRow> sameDay,
        int lunchStart,
        int lunchEnd,
        long? editingAppointmentId,
        int editingStartMin,
        int editingDurMin)
    {
        var r = new List<AppointmentRow>();
        foreach (var a in sameDay)
        {
            int s, d;
            if (editingAppointmentId.HasValue && a.Id == editingAppointmentId.Value)
            {
                s = editingStartMin;
                d = editingDurMin > 0 ? editingDurMin : 30;
            }
            else
            {
                if (!TryParseTimeToMinutes(a.StartTime, out s)) continue;
                d = a.DurationMinutes > 0 ? a.DurationMinutes : 30;
            }

            if (OverlapsHalfOpenBlock(s, d, lunchStart, lunchEnd))
                r.Add(a);
        }

        return r.OrderBy(a => a.StartTime, StringComparer.Ordinal).ToList();
    }

    private static bool SlotCollidesWithAppointments(
        IReadOnlyList<AppointmentRow> sameDay,
        int t,
        int durationMinutes,
        long? excludeAppointmentId,
        long? editingAppointmentId,
        int editingStartMin,
        int editingDurationMin,
        bool pendingNewRdvNotInDb,
        int pendingStartMin,
        int pendingDurationMin)
    {
        var endT = t + durationMinutes;
        foreach (var a in sameDay)
        {
            if (excludeAppointmentId.HasValue && a.Id == excludeAppointmentId.Value) continue;
            int s, d;
            if (editingAppointmentId.HasValue && a.Id == editingAppointmentId.Value)
            {
                s = editingStartMin;
                d = editingDurationMin > 0 ? editingDurationMin : 30;
            }
            else
            {
                if (!TryParseTimeToMinutes(a.StartTime, out s)) continue;
                d = a.DurationMinutes > 0 ? a.DurationMinutes : 30;
            }

            if (t < s + d && endT > s) return true;
        }

        if (pendingNewRdvNotInDb)
        {
            var pd = pendingDurationMin > 0 ? pendingDurationMin : 30;
            if (t < pendingStartMin + pd && endT > pendingStartMin) return true;
        }

        return false;
    }

    /// <summary>Premier RDV dont la plage chevauche <paramref name="startMin"/> / durée (hors <paramref name="excludeAppointmentId"/>).</summary>
    public static AppointmentRow? TryGetFirstOverlappingAppointment(
        IReadOnlyList<AppointmentRow> sameDay,
        int startMin,
        int durationMin,
        long? excludeAppointmentId)
    {
        if (durationMin <= 0) durationMin = 30;
        var endNew = startMin + durationMin;
        foreach (var a in sameDay)
        {
            if (excludeAppointmentId.HasValue && a.Id == excludeAppointmentId.Value) continue;
            if (!TryParseTimeToMinutes(a.StartTime, out var s)) continue;
            var d = a.DurationMinutes > 0 ? a.DurationMinutes : 30;
            var e = s + d;
            if (startMin < e && endNew > s) return a;
        }

        return null;
    }

    /// <summary>Première indisponibilité dont la plage chevauche <paramref name="startMin"/> / durée.</summary>
    public static UnavailabilityRow? TryGetFirstOverlappingUnavailability(
        IReadOnlyList<UnavailabilityRow>? sameDay,
        int startMin,
        int durationMin)
    {
        if (sameDay is null || sameDay.Count == 0) return null;
        if (durationMin <= 0) durationMin = 30;
        var endNew = startMin + durationMin;
        foreach (var u in sameDay)
        {
            if (!TryGetUnavailabilityInterval(u, out var s, out var e)) continue;
            if (startMin < e && endNew > s) return u;
        }

        return null;
    }

    /// <summary>Liste tous les débuts possibles (pas <paramref name="stepMinutes"/>), en excluant un RDV (celui qu’on déplace), avec prise en compte du RDV en cours d’édition / insertion et du lunch proposé.</summary>
    public static List<string> ListAvailableStartTimes(
        IReadOnlyList<AppointmentRow> sameDay,
        IReadOnlyList<UnavailabilityRow>? unavailabilitySameDay,
        int durationMinutes,
        long? excludeAppointmentId,
        long? editingAppointmentId,
        int editingStartMin,
        int editingDurationMin,
        bool pendingNewRdvNotInDb,
        int pendingStartMin,
        int pendingDurationMin,
        int dayStartMin,
        int closingEndMin,
        int stepMinutes = 5,
        int? earliestStartMinInclusive = null,
        int? lunchBlockStartMin = null,
        int? lunchBlockEndMin = null,
        int? pendingExtraHalfOpenStartMin = null,
        int? pendingExtraHalfOpenEndMin = null)
    {
        var result = new List<string>();
        if (durationMinutes <= 0) durationMinutes = 30;
        if (closingEndMin <= dayStartMin) return result;
        var t0 = dayStartMin;
        if (earliestStartMinInclusive.HasValue)
            t0 = Math.Max(t0, CeilToStep(earliestStartMinInclusive.Value, stepMinutes));
        var lunchStart = lunchBlockStartMin ?? -1;
        var lunchEnd = lunchBlockEndMin ?? -1;
        var checkLunch = lunchBlockStartMin.HasValue && lunchBlockEndMin.HasValue && lunchEnd > lunchStart;
        var checkExtra = pendingExtraHalfOpenStartMin.HasValue && pendingExtraHalfOpenEndMin.HasValue
                         && pendingExtraHalfOpenEndMin.Value > pendingExtraHalfOpenStartMin.Value;
        var extraS = pendingExtraHalfOpenStartMin ?? 0;
        var extraE = pendingExtraHalfOpenEndMin ?? 0;
        for (var t = t0; t + durationMinutes <= closingEndMin; t += stepMinutes)
        {
            if (SlotCollidesWithAppointments(
                    sameDay,
                    t,
                    durationMinutes,
                    excludeAppointmentId,
                    editingAppointmentId,
                    editingStartMin,
                    editingDurationMin,
                    pendingNewRdvNotInDb,
                    pendingStartMin,
                    pendingDurationMin))
                continue;
            if (OverlapsUnavailability(unavailabilitySameDay, t, durationMinutes))
                continue;
            if (checkLunch && OverlapsHalfOpenBlock(t, durationMinutes, lunchStart, lunchEnd))
                continue;
            if (checkExtra && OverlapsHalfOpenBlock(t, durationMinutes, extraS, extraE))
                continue;
            result.Add(FormatMinutesAsHhMm(t));
        }

        return result;
    }
}
