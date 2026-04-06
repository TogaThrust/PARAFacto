using System.Collections.Generic;
using System.Linq;
using Dapper;

namespace PARAFactoNative.Services;

public sealed class LunchDayOverrideRow
{
    public long Id { get; set; }
    public string DateIso { get; set; } = "";
    /// <summary>« omit » = pas de lunch ce jour ; « moved » = plage remplacée.</summary>
    public string Mode { get; set; } = "";
    public string? StartTime { get; set; }
    public string? EndTime { get; set; }
}

public sealed class LunchDayOverrideRepo
{
    public LunchDayOverrideRow? GetForDateIso(string dateIso)
    {
        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");
        return cn.QuerySingleOrDefault<LunchDayOverrideRow>(@"
SELECT id AS Id, date_iso AS DateIso, mode AS Mode, start_time AS StartTime, end_time AS EndTime
FROM agenda_lunch_day_override
WHERE date_iso = @d
LIMIT 1;
", new { d = dateIso });
    }

    public IReadOnlyList<LunchDayOverrideRow> ListBetweenInclusive(string fromIso, string toIso)
    {
        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");
        return cn.Query<LunchDayOverrideRow>(@"
SELECT id AS Id, date_iso AS DateIso, mode AS Mode, start_time AS StartTime, end_time AS EndTime
FROM agenda_lunch_day_override
WHERE date_iso >= @fromIso AND date_iso <= @toIso
ORDER BY date_iso;
", new { fromIso, toIso }).ToList();
    }

    public void UpsertOmit(string dateIso)
    {
        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");
        cn.Execute(@"
INSERT INTO agenda_lunch_day_override(date_iso, mode, start_time, end_time)
VALUES(@d, 'omit', NULL, NULL)
ON CONFLICT(date_iso) DO UPDATE SET mode='omit', start_time=NULL, end_time=NULL;
", new { d = dateIso });
    }

    public void UpsertMoved(string dateIso, string startHhMm, string endHhMm)
    {
        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");
        cn.Execute(@"
INSERT INTO agenda_lunch_day_override(date_iso, mode, start_time, end_time)
VALUES(@d, 'moved', @s, @e)
ON CONFLICT(date_iso) DO UPDATE SET mode='moved', start_time=@s, end_time=@e;
", new { d = dateIso, s = startHhMm.Trim(), e = endHhMm.Trim() });
    }

    public void DeleteForDateIso(string dateIso)
    {
        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");
        cn.Execute("DELETE FROM agenda_lunch_day_override WHERE date_iso = @d;", new { d = dateIso });
    }
}
