using System.Collections.Generic;
using System.Linq;
using Dapper;

namespace PARAFactoNative.Services;

public sealed class UnavailabilityRow
{
    public long Id { get; set; }
    public string DateIso { get; set; } = "";
    public string StartTime { get; set; } = "";
    public string EndTime { get; set; } = "";
    public string? Reason { get; set; }
}

public sealed class UnavailabilityRepo
{
    public IReadOnlyList<UnavailabilityRow> ListBetweenInclusive(string fromIso, string toIso)
    {
        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");
        return cn.Query<UnavailabilityRow>(@"
SELECT id AS Id, date_iso AS DateIso, start_time AS StartTime, end_time AS EndTime, reason AS Reason
FROM agenda_unavailability
WHERE date_iso >= @fromIso AND date_iso <= @toIso
ORDER BY date_iso, start_time;
", new { fromIso, toIso }).ToList();
    }

    public IReadOnlyList<UnavailabilityRow> ListForDay(DateTime day)
    {
        var d = day.ToString("yyyy-MM-dd");
        return ListBetweenInclusive(d, d);
    }

    public long Insert(string dateIso, string startTime, string endTime, string? reason)
    {
        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");
        cn.Execute(@"
INSERT INTO agenda_unavailability(date_iso, start_time, end_time, reason)
VALUES(@dateIso, @startTime, @endTime, @reason);
", new { dateIso, startTime, endTime, reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim() });
        return cn.QuerySingle<long>("SELECT last_insert_rowid();");
    }

    public void Delete(long id)
    {
        if (id <= 0) return;
        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");
        cn.Execute("DELETE FROM agenda_unavailability WHERE id=@id;", new { id });
    }

    public void Update(long id, string startTime, string endTime, string? reason)
    {
        if (id <= 0) return;
        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");
        cn.Execute(@"
UPDATE agenda_unavailability
SET start_time=@startTime, end_time=@endTime, reason=@reason
WHERE id=@id;
", new
        {
            id,
            startTime = startTime.Trim(),
            endTime = endTime.Trim(),
            reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()
        });
    }
}
