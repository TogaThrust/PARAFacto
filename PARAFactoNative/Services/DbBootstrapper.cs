using System;
using System.Data;
using Dapper;

namespace PARAFactoNative.Services;

public static class DbBootstrapper
{
    // On garde simple: version "app" pour nos migrations.
    private const int TargetSchemaVersion = 15;

    public static void EnsureDatabase()
    {
        AppPaths.EnsureDataDir();

        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");

        EnsureAppMeta(cn);

        var vStr = cn.ExecuteScalar<string?>("SELECT value FROM app_meta WHERE key='schema_version';");
        _ = int.TryParse(vStr, out var v);

        if (v == 0)
        {
            // DB neuve : schéma de base puis colonnes additionnelles (comme pour les migrations)
            ApplyFreshSchema(cn);
            EnsurePatientsExtraColumns(cn);
            EnsureInvoicesPeriodColumn(cn);
            EnsureInvoicesRecipientColumn(cn);
            EnsureInvoicesUserCommentColumn(cn);
            EnsureAppointmentsTable(cn);
            EnsureAgendaUnavailabilityTable(cn);
            EnsureAgendaLunchDayOverrideTable(cn);
            EnsureAppointmentsRecurrenceSeriesColumn(cn);
            SetSchemaVersion(cn, TargetSchemaVersion);
            return;
        }

        // Migrations idempotentes (support legacy + ajout de colonnes)
        MigrateLegacyPatientsIfNeeded(cn);
        MigrateLegacyTarifsIfNeeded(cn);
        MigrateLegacySeancesIfNeeded(cn);

        EnsurePatientsExtraColumns(cn);
        EnsureTarifsUniqueIndex(cn);
        EnsureCoreTablesExist(cn);
        EnsureInvoicesPeriodColumn(cn);
        EnsureInvoicesRecipientColumn(cn);
        EnsureInvoicesUserCommentColumn(cn);
        EnsureAppointmentsTable(cn);
        EnsureAgendaUnavailabilityTable(cn);
        EnsureAgendaLunchDayOverrideTable(cn);
        EnsureAppointmentsRecurrenceSeriesColumn(cn);

        SetSchemaVersion(cn, TargetSchemaVersion);
    }

    private static void EnsureAppMeta(IDbConnection cn)
    {
        cn.Execute(@"
CREATE TABLE IF NOT EXISTS app_meta(
  key   TEXT PRIMARY KEY,
  value TEXT NOT NULL
);
");
    }



    private static void EnsureInvoicesPeriodColumn(IDbConnection cn)
    {
        // Ajout colonne "period" (YYYY-MM) pour verrouillage/filtrage par mois.
        // Idempotent.
        var hasPeriod = cn.QuerySingle<int>(@"
SELECT COUNT(1)
FROM pragma_table_info('invoices')
WHERE name='period';");
        if (hasPeriod == 0)
        {
            cn.Execute("ALTER TABLE invoices ADD COLUMN period TEXT;");
        }

        // Backfill depuis date_iso (YYYY-MM-DD)
        cn.Execute(@"
UPDATE invoices
SET period = substr(date_iso, 1, 7)
WHERE (period IS NULL OR trim(period)='') AND length(date_iso) >= 7;
");
    }

    private static void EnsureInvoicesRecipientColumn(IDbConnection cn)
    {
        if (ColumnExists(cn, "invoices", "recipient")) return;
        cn.Execute("ALTER TABLE invoices ADD COLUMN recipient TEXT;");
    }

    private static void EnsureInvoicesUserCommentColumn(IDbConnection cn)
    {
        if (ColumnExists(cn, "invoices", "user_comment")) return;
        cn.Execute("ALTER TABLE invoices ADD COLUMN user_comment TEXT;");
    }

    private static void EnsureAppointmentsTable(IDbConnection cn)
    {
        if (TableExists(cn, "appointments")) return;
        cn.Execute(@"
CREATE TABLE appointments(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  patient_id INTEGER NOT NULL,
  tarif_id INTEGER NOT NULL,
  date_iso TEXT NOT NULL,
  start_time TEXT NOT NULL,
  duration_minutes INTEGER NOT NULL DEFAULT 30,
  created_at TEXT NOT NULL DEFAULT (datetime('now')),
  FOREIGN KEY(patient_id) REFERENCES patients(id) ON DELETE CASCADE,
  FOREIGN KEY(tarif_id) REFERENCES tarifs(id) ON DELETE RESTRICT
);
CREATE INDEX IF NOT EXISTS ix_appointments_date ON appointments(date_iso);
");
    }

    private static void EnsureAppointmentsRecurrenceSeriesColumn(IDbConnection cn)
    {
        AddColumnIfMissing(cn, "appointments", "recurrence_series_id", "TEXT");
        cn.Execute("CREATE INDEX IF NOT EXISTS ix_appointments_recurrence_series ON appointments(recurrence_series_id);");
    }

    private static void EnsureAgendaUnavailabilityTable(IDbConnection cn)
    {
        if (TableExists(cn, "agenda_unavailability")) return;
        cn.Execute(@"
CREATE TABLE agenda_unavailability(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  date_iso TEXT NOT NULL,
  start_time TEXT NOT NULL,
  end_time TEXT NOT NULL,
  reason TEXT,
  created_at TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS ix_agenda_unavailability_date ON agenda_unavailability(date_iso);
");
    }

    private static void EnsureAgendaLunchDayOverrideTable(IDbConnection cn)
    {
        if (TableExists(cn, "agenda_lunch_day_override")) return;
        cn.Execute(@"
CREATE TABLE agenda_lunch_day_override(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  date_iso TEXT NOT NULL UNIQUE,
  mode TEXT NOT NULL,
  start_time TEXT,
  end_time TEXT,
  created_at TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS ix_agenda_lunch_day_override_date ON agenda_lunch_day_override(date_iso);
");
    }

    private static void SetSchemaVersion(IDbConnection cn, int v)
    {
        cn.Execute(@"
INSERT INTO app_meta(key,value) VALUES('schema_version', @v)
ON CONFLICT(key) DO UPDATE SET value=excluded.value;
", new { v = v.ToString() });
    }

    private static void ApplyFreshSchema(IDbConnection cn)
    {
        // Tables principales (alignées avec ton diag)
        cn.Execute(@"
CREATE TABLE IF NOT EXISTS patients(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  code3 TEXT,
  nom TEXT,
  prenom TEXT,
  niss TEXT,
  statut TEXT,
  mutuelle TEXT,
  rue TEXT,
  numero TEXT,
  adresse TEXT,
  cp TEXT,
  ville TEXT,
  pays TEXT,
  mail TEXT,
  telephone TEXT,
  referend TEXT,
  prenom_med_presc TEXT,
  nom_med_presc TEXT,
  code_medecin TEXT,
  date_prescription TEXT,
  date_accord TEXT,
  periode_accord TEXT,
  nomenclature TEXT,
  created_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE IF NOT EXISTS tarifs(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  libelle TEXT NOT NULL,
  part_patient INTEGER NOT NULL,
  part_mutuelle INTEGER NOT NULL,
  is_active INTEGER NOT NULL DEFAULT 1,
  created_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_tarifs_libelle ON tarifs(libelle);

CREATE TABLE IF NOT EXISTS seances(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  patient_id INTEGER NOT NULL,
  tarif_id INTEGER NOT NULL,
  date_iso TEXT NOT NULL,
  is_cash INTEGER NOT NULL DEFAULT 0,
  commentaire TEXT,
  part_patient INTEGER NOT NULL,
  part_mutuelle INTEGER NOT NULL,
  created_at TEXT NOT NULL DEFAULT (datetime('now')),
  FOREIGN KEY(patient_id) REFERENCES patients(id) ON DELETE RESTRICT,
  FOREIGN KEY(tarif_id) REFERENCES tarifs(id) ON DELETE RESTRICT
);

CREATE TABLE IF NOT EXISTS invoices(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  invoice_no TEXT NOT NULL,
  kind TEXT NOT NULL,
  patient_id INTEGER,
  mutuelle TEXT,
  date_iso TEXT NOT NULL,
  total_cents INTEGER NOT NULL DEFAULT 0,
  paid_cents  INTEGER NOT NULL DEFAULT 0,
  status TEXT NOT NULL DEFAULT 'unpaid',
  ref_invoice_id INTEGER,
  reason TEXT,
  ref_doc TEXT,
  recipient TEXT,
  user_comment TEXT,
  period TEXT,
  created_at TEXT NOT NULL DEFAULT (datetime('now')),
  FOREIGN KEY(patient_id) REFERENCES patients(id) ON DELETE SET NULL,
  FOREIGN KEY(ref_invoice_id) REFERENCES invoices(id) ON DELETE SET NULL
);

CREATE TABLE IF NOT EXISTS invoice_lines(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  invoice_id INTEGER NOT NULL,
  label TEXT,
  qty INTEGER NOT NULL DEFAULT 1,
  unit_price_cents INTEGER NOT NULL DEFAULT 0,
  total_cents INTEGER NOT NULL DEFAULT 0,
  patient_part_cents INTEGER NOT NULL DEFAULT 0,
  mutuelle_part_cents INTEGER NOT NULL DEFAULT 0,
  date_iso TEXT,
  created_at TEXT NOT NULL DEFAULT (datetime('now')),
  FOREIGN KEY(invoice_id) REFERENCES invoices(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS payments(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  invoice_id INTEGER NOT NULL,
  paid_date TEXT NOT NULL,
  amount_cents INTEGER NOT NULL,
  method TEXT NOT NULL,
  reference TEXT,
  created_at TEXT NOT NULL DEFAULT (datetime('now')),
  FOREIGN KEY(invoice_id) REFERENCES invoices(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS audit_events(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  ts TEXT NOT NULL,
  actor TEXT,
  event_type TEXT NOT NULL,
  entity TEXT NOT NULL,
  entity_id INTEGER,
  details_json TEXT
);

CREATE TABLE IF NOT EXISTS invoice_counters(
  year INTEGER NOT NULL,
  month INTEGER NOT NULL,
  last_seq INTEGER NOT NULL,
  updated_at TEXT NOT NULL,
  PRIMARY KEY(year, month)
);

-- Pertes (liées aux factures)
CREATE TABLE IF NOT EXISTS losses(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  invoice_id INTEGER NOT NULL,
  loss_date TEXT NOT NULL,
  amount_cents INTEGER NOT NULL,
  reason TEXT,
  created_at TEXT NOT NULL DEFAULT (datetime('now')),
  FOREIGN KEY(invoice_id) REFERENCES invoices(id) ON DELETE CASCADE
);

-- Historique des révisions des factures mutuelles (si utilisé)
CREATE TABLE IF NOT EXISTS mutual_invoice_revisions(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  invoice_id INTEGER NOT NULL,
  revision_no INTEGER NOT NULL,
  changed_at TEXT NOT NULL,
  new_total_cents INTEGER NOT NULL,
  reason TEXT NOT NULL,
  reference_doc TEXT NOT NULL,
  created_at TEXT NOT NULL DEFAULT (datetime('now')),
  FOREIGN KEY(invoice_id) REFERENCES invoices(id) ON DELETE CASCADE,
  UNIQUE(invoice_id, revision_no)
);

-- Rendez-vous (agenda)
CREATE TABLE IF NOT EXISTS appointments(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  patient_id INTEGER NOT NULL,
  tarif_id INTEGER NOT NULL,
  date_iso TEXT NOT NULL,
  start_time TEXT NOT NULL,
  duration_minutes INTEGER NOT NULL DEFAULT 30,
  created_at TEXT NOT NULL DEFAULT (datetime('now')),
  FOREIGN KEY(patient_id) REFERENCES patients(id) ON DELETE CASCADE,
  FOREIGN KEY(tarif_id) REFERENCES tarifs(id) ON DELETE RESTRICT
);
CREATE INDEX IF NOT EXISTS ix_appointments_date ON appointments(date_iso);
");
    }

    private static bool ColumnExists(IDbConnection cn, string table, string column)
    {
        var n = cn.ExecuteScalar<long>($"SELECT COUNT(1) FROM pragma_table_info('{table}') WHERE name=@c;", new { c = column });
        return n > 0;
    }

    private static void AddColumnIfMissing(IDbConnection cn, string table, string column, string ddlType)
    {
        if (ColumnExists(cn, table, column)) return;
        cn.Execute($"ALTER TABLE {table} ADD COLUMN {column} {ddlType};");
    }

    private static void EnsureCoreTablesExist(IDbConnection cn)
    {
        // Crée celles qui manquent sans toucher aux existantes
        ApplyFreshSchema(cn);
    }

    private static void EnsurePatientsExtraColumns(IDbConnection cn)
    {
        // Ajout des colonnes "riches" (si DB déjà existante)
        AddColumnIfMissing(cn, "patients", "rue", "TEXT");
        AddColumnIfMissing(cn, "patients", "numero", "TEXT");
        AddColumnIfMissing(cn, "patients", "pays", "TEXT");
        AddColumnIfMissing(cn, "patients", "mail", "TEXT");
        AddColumnIfMissing(cn, "patients", "telephone", "TEXT");
        AddColumnIfMissing(cn, "patients", "referend", "TEXT");
        AddColumnIfMissing(cn, "patients", "prenom_med_presc", "TEXT");
        AddColumnIfMissing(cn, "patients", "nom_med_presc", "TEXT");
        AddColumnIfMissing(cn, "patients", "code_medecin", "TEXT");
        AddColumnIfMissing(cn, "patients", "date_prescription", "TEXT");
        AddColumnIfMissing(cn, "patients", "date_accord", "TEXT");
        AddColumnIfMissing(cn, "patients", "periode_accord", "TEXT");
        AddColumnIfMissing(cn, "patients", "nomenclature", "TEXT");
        AddColumnIfMissing(cn, "patients", "commentaire", "TEXT");

        // colonne adresse existait déjà dans ta version 9, mais au cas où
        AddColumnIfMissing(cn, "patients", "adresse", "TEXT");
        AddColumnIfMissing(cn, "patients", "cp", "TEXT");
        AddColumnIfMissing(cn, "patients", "ville", "TEXT");
    }

    private static void EnsureTarifsUniqueIndex(IDbConnection cn)
    {
        // indispensable pour l'UPSERT ON CONFLICT(libelle)
        cn.Execute("CREATE UNIQUE INDEX IF NOT EXISTS ux_tarifs_libelle ON tarifs(libelle);");
    }

    private static void MigrateLegacyPatientsIfNeeded(IDbConnection cn)
    {
        // Cas legacy: patients(patient_id, last_name, first_name, ...)
        if (!TableExists(cn, "patients")) return;

        var hasLast = ColumnExists(cn, "patients", "last_name");
        var hasNom = ColumnExists(cn, "patients", "nom");
        if (!hasLast || hasNom) return;

        cn.Execute("PRAGMA foreign_keys = OFF;");
        cn.Execute(@"
ALTER TABLE patients RENAME TO patients_legacy;
");
        ApplyFreshSchema(cn); // recrée patients (si pas déjà)

        // copie minimale
        cn.Execute(@"
INSERT INTO patients(id, code3, nom, prenom, niss, statut, mutuelle, adresse, cp, ville, created_at)
SELECT
  patient_id,
  code3,
  last_name,
  first_name,
  niss,
  statut,
  mutual_name,
  address1,
  zip,
  city,
  created_at
FROM patients_legacy;
");

        cn.Execute("DROP TABLE patients_legacy;");
        cn.Execute("PRAGMA foreign_keys = ON;");
    }

    private static void MigrateLegacyTarifsIfNeeded(IDbConnection cn)
    {
        if (!TableExists(cn, "tarifs")) return;
        var hasTarifId = ColumnExists(cn, "tarifs", "tarif_id");
        var hasId = ColumnExists(cn, "tarifs", "id");
        if (!hasTarifId || hasId) return;

        cn.Execute("PRAGMA foreign_keys = OFF;");
        cn.Execute("ALTER TABLE tarifs RENAME TO tarifs_legacy;");

        cn.Execute(@"
CREATE TABLE IF NOT EXISTS tarifs(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  libelle TEXT NOT NULL,
  part_patient INTEGER NOT NULL,
  part_mutuelle INTEGER NOT NULL,
  is_active INTEGER NOT NULL DEFAULT 1,
  created_at TEXT NOT NULL DEFAULT (datetime('now'))
);
");

        cn.Execute(@"
INSERT INTO tarifs(id, libelle, part_patient, part_mutuelle, is_active, created_at)
SELECT tarif_id, label, part_patient_cents, part_mutuelle_cents, CASE WHEN active THEN 1 ELSE 0 END, created_at
FROM tarifs_legacy;
");

        cn.Execute("DROP TABLE tarifs_legacy;");
        EnsureTarifsUniqueIndex(cn);
        cn.Execute("PRAGMA foreign_keys = ON;");
    }

    private static void MigrateLegacySeancesIfNeeded(IDbConnection cn)
    {
        if (!TableExists(cn, "seances")) return;

        var hasSeanceId = ColumnExists(cn, "seances", "seance_id");
        var hasId = ColumnExists(cn, "seances", "id");
        if (!hasSeanceId || hasId) return;

        cn.Execute("PRAGMA foreign_keys = OFF;");
        cn.Execute("ALTER TABLE seances RENAME TO seances_legacy;");

        cn.Execute(@"
CREATE TABLE IF NOT EXISTS seances(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  patient_id INTEGER NOT NULL,
  tarif_id INTEGER NOT NULL,
  date_iso TEXT NOT NULL,
  is_cash INTEGER NOT NULL DEFAULT 0,
  commentaire TEXT,
  part_patient INTEGER NOT NULL,
  part_mutuelle INTEGER NOT NULL,
  created_at TEXT NOT NULL DEFAULT (datetime('now')),
  FOREIGN KEY(patient_id) REFERENCES patients(id) ON DELETE RESTRICT,
  FOREIGN KEY(tarif_id) REFERENCES tarifs(id) ON DELETE RESTRICT
);
");

        cn.Execute(@"
INSERT INTO seances(id, patient_id, tarif_id, date_iso, is_cash, commentaire, part_patient, part_mutuelle, created_at)
SELECT seance_id,
       patient_id,
       tarif_id,
       date_iso,
       CASE WHEN is_cash THEN 1 ELSE 0 END,
       commentaire,
       part_patient_cents,
       part_mutuelle_cents,
       created_at
FROM seances_legacy;
");

        cn.Execute("DROP TABLE seances_legacy;");
        cn.Execute("PRAGMA foreign_keys = ON;");
    }

    private static bool TableExists(IDbConnection cn, string table)
    {
        var n = cn.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type='table' AND name=@t;", new { t = table });
        return n > 0;
    }
}
