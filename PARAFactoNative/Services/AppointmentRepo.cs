using System.Collections.Generic;
using System.Linq;
using Dapper;

namespace PARAFactoNative.Services;

public sealed class AppointmentRow
{
    public long Id { get; set; }
    public long PatientId { get; set; }
    public long TarifId { get; set; }
    public string DateIso { get; set; } = "";
    public string StartTime { get; set; } = "";
    public int DurationMinutes { get; set; }
    public string PatientNom { get; set; } = "";
    public string PatientPrenom { get; set; } = "";
    public string? RecurrenceSeriesId { get; set; }

    public string PatientDisplay => $"{PatientNom} {PatientPrenom}".Trim();
}

public sealed class AppointmentRepo
{
    private const string SelectSql = @"
SELECT
  a.id AS Id,
  a.patient_id AS PatientId,
  a.tarif_id AS TarifId,
  a.date_iso AS DateIso,
  a.start_time AS StartTime,
  a.duration_minutes AS DurationMinutes,
  COALESCE(p.nom,'') AS PatientNom,
  COALESCE(p.prenom,'') AS PatientPrenom,
  a.recurrence_series_id AS RecurrenceSeriesId
FROM appointments a
JOIN patients p ON p.id = a.patient_id
";

    public IReadOnlyList<AppointmentRow> ListBetweenInclusive(string fromIso, string toIso)
    {
        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");
        return cn.Query<AppointmentRow>($@"
{SelectSql}
WHERE a.date_iso >= @fromIso AND a.date_iso <= @toIso
ORDER BY a.date_iso, a.start_time;
", new { fromIso, toIso }).ToList();
    }

    public IReadOnlyList<AppointmentRow> ListForDay(DateTime day)
    {
        var d = day.ToString("yyyy-MM-dd");
        return ListBetweenInclusive(d, d);
    }

    public AppointmentRow? GetById(long id)
    {
        if (id <= 0) return null;
        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");
        return cn.QuerySingleOrDefault<AppointmentRow>($@"
{SelectSql}
WHERE a.id = @id;
", new { id });
    }

    public long Insert(long patientId, long tarifId, string dateIso, string startTime, int durationMinutes, string? recurrenceSeriesId = null)
    {
        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");
        cn.Execute(@"
INSERT INTO appointments(patient_id, tarif_id, date_iso, start_time, duration_minutes, recurrence_series_id)
VALUES(@patientId, @tarifId, @dateIso, @startTime, @durationMinutes, @recurrenceSeriesId);
", new { patientId, tarifId, dateIso, startTime, durationMinutes, recurrenceSeriesId });
        return cn.QuerySingle<long>("SELECT last_insert_rowid();");
    }

    public void Update(long id, long patientId, long tarifId, string dateIso, string startTime, int durationMinutes, string? recurrenceSeriesId = null)
    {
        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");
        cn.Execute(@"
UPDATE appointments
SET patient_id=@patientId,
    tarif_id=@tarifId,
    date_iso=@dateIso,
    start_time=@startTime,
    duration_minutes=@durationMinutes,
    recurrence_series_id=@recurrenceSeriesId
WHERE id=@id;
", new { id, patientId, tarifId, dateIso, startTime, durationMinutes, recurrenceSeriesId });
    }

    public void Delete(long id)
    {
        if (id <= 0) return;
        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");
        cn.Execute("DELETE FROM appointments WHERE id=@id;", new { id });
    }

    /// <summary>Identifiants des RDV de la série à partir d’une date (inclusive), triés par date puis heure.</summary>
    public IReadOnlyList<long> ListIdsInRecurrenceSeriesFromDate(string? seriesId, string fromIsoInclusive)
    {
        if (string.IsNullOrWhiteSpace(seriesId)) return new List<long>();
        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");
        return cn.Query<long>(@"
SELECT id FROM appointments
WHERE recurrence_series_id = @s AND date_iso >= @from
ORDER BY date_iso, start_time;
", new { s = seriesId, from = fromIsoInclusive }).ToList();
    }
}
