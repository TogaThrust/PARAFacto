using System;
using System.Collections.Generic;

namespace PARAFactoNative.Services;

public enum RecurrencePatternKind
{
    Daily,
    WeeklySameWeekdayAsAnchor,
    MonthlySameDayAsAnchor,
    WeeklyOnFixedWeekday,
    MonthlyOnFixedDayOfMonth
}

public static class RecurrenceDateGenerator
{
    /// <summary>
    /// Dates de la série (incluant l’ancre), triées chronologiquement.
    /// Soit limite par date de fin (inclusive), soit par nombre d’occurrences (1–100).
    /// </summary>
    public static List<DateTime> Generate(
        DateTime anchorDate,
        RecurrencePatternKind kind,
        DayOfWeek? fixedWeekday,
        int? fixedDayOfMonth,
        bool limitByEndDate,
        DateTime endDateInclusive,
        int occurrenceCount)
    {
        anchorDate = anchorDate.Date;
        var end = limitByEndDate ? endDateInclusive.Date : DateTime.MaxValue.Date;
        var maxByCount = Math.Clamp(occurrenceCount, 1, 100);
        var r = new List<DateTime>();
        const int safety = 600;

        bool Take(DateTime d)
        {
            if (d < anchorDate) return false;
            if (limitByEndDate && d > end) return false;
            r.Add(d);
            return true;
        }

        bool Enough() => !limitByEndDate && r.Count >= maxByCount;

        switch (kind)
        {
            case RecurrencePatternKind.Daily:
            {
                var d = anchorDate;
                var n = 0;
                while (n++ < safety && (!limitByEndDate || d <= end))
                {
                    if (limitByEndDate && d > end) break;
                    Take(d);
                    if (Enough()) break;
                    d = d.AddDays(1);
                }

                break;
            }
            case RecurrencePatternKind.WeeklySameWeekdayAsAnchor:
            {
                var d = anchorDate;
                var n = 0;
                while (n++ < safety && (!limitByEndDate || d <= end))
                {
                    if (limitByEndDate && d > end) break;
                    Take(d);
                    if (Enough()) break;
                    d = d.AddDays(7);
                }

                break;
            }
            case RecurrencePatternKind.MonthlySameDayAsAnchor:
            {
                var d = anchorDate;
                var n = 0;
                while (n++ < safety && (!limitByEndDate || d <= end))
                {
                    if (limitByEndDate && d > end) break;
                    Take(d);
                    if (Enough()) break;
                    d = AddMonthsKeepDay(d, 1);
                }

                break;
            }
            case RecurrencePatternKind.WeeklyOnFixedWeekday:
            {
                var dow = fixedWeekday ?? anchorDate.DayOfWeek;
                var d = FirstWeekdayOnOrAfter(anchorDate, dow);
                var n = 0;
                while (n++ < safety && (!limitByEndDate || d <= end))
                {
                    if (limitByEndDate && d > end) break;
                    Take(d);
                    if (Enough()) break;
                    d = d.AddDays(7);
                }

                break;
            }
            case RecurrencePatternKind.MonthlyOnFixedDayOfMonth:
            {
                var dom = Math.Clamp(fixedDayOfMonth ?? anchorDate.Day, 1, 31);
                var d = FirstMonthlyDayOnOrAfter(anchorDate, dom);
                var n = 0;
                while (n++ < safety && (!limitByEndDate || d <= end))
                {
                    if (limitByEndDate && d > end) break;
                    Take(d);
                    if (Enough()) break;
                    var nextM = new DateTime(d.Year, d.Month, 1).AddMonths(1);
                    var dim = DateTime.DaysInMonth(nextM.Year, nextM.Month);
                    var day = Math.Min(dom, dim);
                    d = new DateTime(nextM.Year, nextM.Month, day);
                }

                break;
            }
            default:
                Take(anchorDate);
                break;
        }

        if (limitByEndDate)
            r.RemoveAll(x => x > end);
        else if (r.Count > maxByCount)
            r.RemoveRange(maxByCount, r.Count - maxByCount);

        r.Sort();
        return r;
    }

    private static DateTime FirstWeekdayOnOrAfter(DateTime anchor, DayOfWeek dow)
    {
        var d = anchor.Date;
        var diff = ((int)dow - (int)d.DayOfWeek + 7) % 7;
        return d.AddDays(diff);
    }

    private static DateTime FirstMonthlyDayOnOrAfter(DateTime anchor, int dayOfMonth)
    {
        var y = anchor.Year;
        var m = anchor.Month;
        for (var i = 0; i < 800; i++)
        {
            var dim = DateTime.DaysInMonth(y, m);
            var day = Math.Min(dayOfMonth, dim);
            var cand = new DateTime(y, m, day);
            if (cand >= anchor.Date)
                return cand;
            m++;
            if (m > 12)
            {
                m = 1;
                y++;
            }
        }

        return anchor;
    }

    private static DateTime AddMonthsKeepDay(DateTime d, int months)
    {
        var day = d.Day;
        var t = d.AddMonths(months);
        var dim = DateTime.DaysInMonth(t.Year, t.Month);
        var use = Math.Min(day, dim);
        return new DateTime(t.Year, t.Month, use);
    }
}
