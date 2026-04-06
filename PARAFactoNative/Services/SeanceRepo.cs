using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using Dapper;
using PARAFactoNative.Models;

namespace PARAFactoNative.Services;

public sealed class SeanceRepo
{
    private const string BaseSelect = @"
SELECT
  s.id AS SeanceId,
  s.date_iso AS DateIso,

  -- Patient mapping for SeanceRow (Code3 + PatientNom + PatientPrenom)
  p.code3  AS Code3,
  p.nom    AS PatientNom,
  p.prenom AS PatientPrenom,

  -- Tariff mapping
  t.libelle AS TarifLibelle,
  t.libelle AS TarifLabel,

  s.is_cash AS IsCash,
  s.part_patient  AS PartPatientCents,
  s.part_mutuelle AS PartMutuelleCents,
  s.commentaire   AS Commentaire
FROM seances s
JOIN patients p ON p.id = s.patient_id
JOIN tarifs   t ON t.id = s.tarif_id
";

    public List<SeanceRow> GetByDay(DateTime day)
        => Query("WHERE s.date_iso = @d", new { d = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) });

    public List<SeanceRow> GetByMonth(int year, int month)
    {
        var start = new DateTime(year, month, 1);
        var end = start.AddMonths(1);
        return Query("WHERE s.date_iso >= @a AND s.date_iso < @b", new
        {
            a = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            b = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        });
    }

    public List<SeanceRow> GetByYear(int year)
    {
        var start = new DateTime(year, 1, 1);
        var end = start.AddYears(1);
        return Query("WHERE s.date_iso >= @a AND s.date_iso < @b", new
        {
            a = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            b = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        });
    }

    public List<SeanceRow> Search(string q, DateTime? day = null)
    {
        q = (q ?? "").Trim();
        if (q.Length == 0)
            return day is null ? GetByDay(DateTime.Today) : GetByDay(day.Value);

        var where = @"
WHERE
  (p.code3 LIKE @p OR p.nom LIKE @p OR p.prenom LIKE @p OR t.libelle LIKE @p OR s.commentaire LIKE @p)
";

        if (day is not null)
            where += " AND s.date_iso = @d";

        var param = new
        {
            p = $"%{q}%",
            d = day?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        };
        return Query(where, param);
    }

    private static List<SeanceRow> Query(string where, object param)
    {
        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");
        var list = cn.Query<SeanceRow>(BaseSelect + "\n" + where + "\nORDER BY s.date_iso DESC, p.nom, p.prenom, t.libelle;", param).ToList();
        foreach (var r in list)
            r.RdvAgendaMarkerPrefix = SeanceRdvTimeHelper.TryGetRdvMarkerPrefix(r.Commentaire, out var p) ? p : null;
        return list;
    }
        public int GetLatestYearOrCurrent()
    {
        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");
        var y = cn.ExecuteScalar<long>("SELECT COALESCE(MAX(CAST(substr(date_iso,1,4) AS INTEGER)), 0) FROM seances;");
        if (y <= 0) y = DateTime.Today.Year;
        return (int)y;
    }

    public string? GetLastSeanceDateIso()
    {
        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");
        var s = cn.ExecuteScalar<string?>("SELECT MAX(date_iso) FROM seances;");
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    public bool HasSeancesForDay(DateTime day)
    {
        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");
        var iso = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var n = cn.ExecuteScalar<long>("SELECT COUNT(1) FROM seances WHERE date_iso=@d;", new { d = iso });
        return n > 0;
    }


    public int DeleteSeance(long seanceId)
    {
        using var cn = Db.Open();
        return cn.Execute("DELETE FROM seances WHERE id=@id;", new { id = seanceId });
    }

    public int UpdateSeance(long seanceId, long patientId, long tarifId, DateTime date, bool isCash, int partPatientCents, int partMutuelleCents, string? commentaire)
    {
        using var cn = Db.Open();
        return cn.Execute(@"
            UPDATE seances
            SET patient_id=@patientId,
                tarif_id=@tarifId,
                date_iso=@dateIso,
                is_cash=@isCash,
                part_patient=@pp,
                part_mutuelle=@pm,
                commentaire=@commentaire
            WHERE id=@id;",
            new
            {
                id = seanceId,
                patientId,
                tarifId,
                dateIso = date.ToString("yyyy-MM-dd"),
                isCash = isCash ? 1 : 0,
                pp = partPatientCents,
                pm = partMutuelleCents,
                commentaire = commentaire ?? ""
            });
    }


    public long? GetPatientIdByCode3(string? code3)
    {
        code3 = (code3 ?? "").Trim();
        if (code3.Length == 0) return null;

        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");
        return cn.ExecuteScalar<long?>(@"
SELECT id
FROM patients
WHERE upper(trim(code3)) = upper(trim(@code))
LIMIT 1;
", new { code = code3 });
    }

    public long? GetTarifIdByLabel(string? label)
    {
        label = (label ?? "").Trim();
        if (label.Length == 0) return null;

        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");
        return cn.ExecuteScalar<long?>(@"
SELECT id
FROM tarifs
WHERE upper(trim(libelle)) = upper(trim(@label))
LIMIT 1;
", new { label });
    }

}
