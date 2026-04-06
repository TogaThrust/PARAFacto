using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using PARAFactoNative.Models;

namespace PARAFactoNative.Services;

public sealed class SeanceService
{
    private sealed class SqliteColumnInfo
    {
        // PRAGMA table_info(...) retourne notamment "name"
        public string? name { get; set; }
    }

    private static bool HasColumn(System.Data.IDbConnection cn, string tableName, string columnName)
    {
        try
        {
            var cols = cn.Query<SqliteColumnInfo>($"PRAGMA table_info({tableName});");
            return cols.Any(c => string.Equals(c.name, columnName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    public Seance? GetById(long seanceId)
    {
        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");

        return cn.QuerySingleOrDefault<Seance>(@"
SELECT
  id            AS SeanceId,
  patient_id    AS PatientId,
  tarif_id      AS TarifId,
  date_iso      AS Date,
  is_cash       AS IsCash,
  commentaire   AS Commentaire,
  part_patient  AS PartPatientCents,
  part_mutuelle AS PartMutuelleCents
FROM seances
WHERE id=@id;
", new { id = seanceId });
    }

    /// <summary>Indique si une séance du jour porte déjà la marque d'import agenda pour ce RDV (évite les doublons).</summary>
    public bool SeanceExistsForRdvMarker(long appointmentId, DateTime date)
    {
        var tag = $"[RDV#{appointmentId}]";
        var d = date.ToString("yyyy-MM-dd");
        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");
        var n = cn.QuerySingle<long>(
            "SELECT COUNT(*) FROM seances WHERE date_iso=@d AND INSTR(IFNULL(commentaire,''), @tag)>0;",
            new { d, tag });
        return n > 0;
    }

    public void AddSeance(long patientId, long tarifId, DateTime date, bool isCash, string? commentaire)
    {
        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");

        // On fige les parts patient/mutuelle au moment de l'encodage
        var tarif = cn.QuerySingleOrDefault<Tarif>(@"
SELECT id AS Id,
       libelle AS Label,
       part_patient AS PartPatientCents,
       part_mutuelle AS PartMutuelleCents,
       is_active AS Active
FROM tarifs
WHERE id=@id;
", new { id = tarifId });

        if (tarif is null)
            throw new InvalidOperationException($"Tarif introuvable: {tarifId}");

        cn.Execute(@"
INSERT INTO seances(patient_id, tarif_id, date_iso, is_cash, commentaire, part_patient, part_mutuelle)
VALUES(@pid, @tid, @d, @cash, @com, @pp, @pm);
",
            new
            {
                pid = patientId,
                tid = tarifId,
                d = date.ToString("yyyy-MM-dd"),
                cash = isCash ? 1 : 0,
                com = commentaire,
                pp = tarif.PartPatientCents,
                pm = tarif.PartMutuelleCents
            });

        cn.Execute(@"
INSERT INTO audit_events(ts, event_type, entity, entity_id, details_json)
VALUES(@ts,'ADD_SEANCE','seances', last_insert_rowid(), @details);
",
            new
            {
                ts = DateTime.UtcNow.ToString("O"),
                details = System.Text.Json.JsonSerializer.Serialize(new
                {
                    patientId,
                    tarifId,
                    date = date.ToString("yyyy-MM-dd"),
                    isCash
                })
            });
    }

    public IReadOnlyList<SeanceRow> GetSeancesForDay(DateTime date)
    {
        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");

        // On enrichit le row pour le PDF journalier (CODE3, NISS, NOMENC, libellé tarif "encaissements")
        // sans casser les workspaces où certains champs n'existent pas encore.
        var hasCode3 = HasColumn(cn, "patients", "code3");
        var hasNom = HasColumn(cn, "patients", "nom");
        var hasPrenom = HasColumn(cn, "patients", "prenom");
        var hasNiss = HasColumn(cn, "patients", "niss");

        var hasTarifLibelle = HasColumn(cn, "tarifs", "libelle");
        var hasTarifEnc = HasColumn(cn, "tarifs", "libelle_encaissements");
        var hasNomenc = HasColumn(cn, "tarifs", "nomenclature");

        var code3Expr = hasCode3 ? "COALESCE(p.code3,'')" : "''";
        var nissExpr = hasNiss ? "COALESCE(p.niss,'')" : "''";
        var nomExpr = hasNom ? "COALESCE(p.nom,'')" : "''";
        var prenomExpr = hasPrenom ? "COALESCE(p.prenom,'')" : "''";

        var tarifLabelExpr = hasTarifLibelle ? "t.libelle" : "''";
        var tarifEncExpr = hasTarifEnc ? "t.libelle_encaissements" : tarifLabelExpr;
        var nomencExpr = hasNomenc ? "COALESCE(t.nomenclature,'')" : "''";

        var sql = $@"
SELECT
  s.id AS SeanceId,
  s.patient_id AS PatientId,
  s.tarif_id AS TarifId,
  {code3Expr} AS Code3,
  {nissExpr} AS Niss,
  {nomencExpr} AS Nomenclature,
  ({code3Expr} || ' — ' || {nomExpr} || ' ' || {prenomExpr}) AS PatientDisplay,
  {tarifLabelExpr} AS TarifLabel,
  {tarifEncExpr} AS TarifEncaissements,
  s.is_cash AS IsCash,
  s.part_patient AS PartPatientCents,
  s.part_mutuelle AS PartMutuelleCents,
  COALESCE(s.commentaire,'') AS Commentaire
FROM seances s
JOIN patients p ON p.id = s.patient_id
JOIN tarifs   t ON t.id = s.tarif_id
WHERE s.date_iso = @d
ORDER BY {nomExpr}, {prenomExpr}, {tarifLabelExpr};
";

        var rows = cn.Query<SeanceRow>(sql, new { d = date.ToString("yyyy-MM-dd") }).ToList();
        return rows
            .OrderBy(r => SeanceRdvTimeHelper.SortKeyForDayList(r))
            .ThenBy(r => r.PatientDisplay ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Id)
            .ToList();
    }

    /// <summary>Supprime les séances dont le commentaire contient la marque <c>[RDV#appointmentId]</c> (import agenda).</summary>
    public int DeleteSeancesLinkedToAppointment(long appointmentId)
    {
        var tag = $"[RDV#{appointmentId}]";
        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");
        return cn.Execute("DELETE FROM seances WHERE INSTR(IFNULL(commentaire,''), @tag) > 0;", new { tag });
    }

    public void DeleteSeance(long seanceId)
    {
        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");

        cn.Execute("DELETE FROM seances WHERE id=@id;", new { id = seanceId });

        cn.Execute(@"
INSERT INTO audit_events(ts, event_type, entity, entity_id, details_json)
VALUES(@ts,'DELETE_SEANCE','seances', @id, @details);
",
            new
            {
                ts = DateTime.UtcNow.ToString("O"),
                id = seanceId,
                details = System.Text.Json.JsonSerializer.Serialize(new { seanceId })
            });
    }

    public void UpdateSeance(long seanceId, long patientId, long tarifId, DateTime date, bool isCash, string? commentaire)
    {
        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");

        var previousCom = cn.QuerySingleOrDefault<string>(
            "SELECT commentaire FROM seances WHERE id=@id;", new { id = seanceId });
        commentaire = SeanceRdvTimeHelper.EnsurePreservedRdvMarkerOnUpdate(previousCom, commentaire);

        // Re-fige les parts au moment de la modification
        var tarif = cn.QuerySingleOrDefault<Tarif>(@"
SELECT id AS Id,
       libelle AS Label,
       part_patient AS PartPatientCents,
       part_mutuelle AS PartMutuelleCents,
       is_active AS Active
FROM tarifs
WHERE id=@id;
", new { id = tarifId });

        if (tarif is null)
            throw new InvalidOperationException($"Tarif introuvable: {tarifId}");

        cn.Execute(@"
UPDATE seances
SET patient_id=@pid,
    tarif_id=@tid,
    date_iso=@d,
    is_cash=@cash,
    commentaire=@com,
    part_patient=@pp,
    part_mutuelle=@pm
WHERE id=@id;
",
            new
            {
                id = seanceId,
                pid = patientId,
                tid = tarifId,
                d = date.ToString("yyyy-MM-dd"),
                cash = isCash ? 1 : 0,
                com = commentaire,
                pp = tarif.PartPatientCents,
                pm = tarif.PartMutuelleCents
            });

        cn.Execute(@"
INSERT INTO audit_events(ts, event_type, entity, entity_id, details_json)
VALUES(@ts,'UPDATE_SEANCE','seances', @id, @details);
",
            new
            {
                ts = DateTime.UtcNow.ToString("O"),
                id = seanceId,
                details = System.Text.Json.JsonSerializer.Serialize(new
                {
                    seanceId,
                    patientId,
                    tarifId,
                    date = date.ToString("yyyy-MM-dd"),
                    isCash
                })
            });
    }

    // =====================
    // ANNULATION helpers
    // =====================
    public void InsertSeanceWithId(Seance s)
    {
        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");

        // On réinsère exactement (id + parts figées)
        cn.Execute(@"
INSERT INTO seances(id, patient_id, tarif_id, date_iso, is_cash, commentaire, part_patient, part_mutuelle)
VALUES(@id, @pid, @tid, @d, @cash, @com, @pp, @pm);
",
            new
            {
                id = s.SeanceId,
                pid = s.PatientId,
                tid = s.TarifId,
                d = s.Date.ToString("yyyy-MM-dd"),
                cash = s.IsCash ? 1 : 0,
                com = s.Commentaire,
                pp = s.PartPatientCents,
                pm = s.PartMutuelleCents
            });
    }

    public void UpdateSeanceRaw(Seance s)
    {
        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");

        // On restaure exactement l'état précédent (y compris parts figées)
        cn.Execute(@"
UPDATE seances
SET patient_id=@pid,
    tarif_id=@tid,
    date_iso=@d,
    is_cash=@cash,
    commentaire=@com,
    part_patient=@pp,
    part_mutuelle=@pm
WHERE id=@id;
",
            new
            {
                id = s.SeanceId,
                pid = s.PatientId,
                tid = s.TarifId,
                d = s.Date.ToString("yyyy-MM-dd"),
                cash = s.IsCash ? 1 : 0,
                com = s.Commentaire,
                pp = s.PartPatientCents,
                pm = s.PartMutuelleCents
            });
    }
}
