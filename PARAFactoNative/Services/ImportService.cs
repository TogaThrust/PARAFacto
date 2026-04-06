using Dapper;
using ExcelDataReader;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PARAFactoNative.Services;

public sealed class ImportService
{
    // Workspace par défaut : "Documents\\PARAFACTO_Native" (souvent OneDrive\Documents)
    // L'utilisateur centralise tous les fichiers vintage ici.
    public static string GetDefaultWorkspaceRoot()
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(docs, "PARAFACTO_Native");
    }

    public sealed record ImportResult(
        int Patients,
        int Tarifs,
        int Seances,
        int InvoicesPatient,
        int InvoicesMutuelle,
        int Payments,
        int Losses,
        int Comments)
    {
        // Compat UI legacy : certains ViewModels attendent encore ces noms.
        public int Factures => InvoicesPatient + InvoicesMutuelle;
        public int Paiements => Payments;
        public int Pertes => Losses;
    }

    public ImportResult ImportAll(
        string logoJournalierXlsmPath,
        string? baseFacturationXlsxPath,
        string? patientsInvoicesCsv,
        string? mutuellesInvoicesCsv,
        string? paymentsCsv,
        string? lossesCsv,
        string? dbCommentairesXlsx,
        string? mutualModifsCsv = null)
    {
        if (!File.Exists(logoJournalierXlsmPath))
            throw new FileNotFoundException("Fichier LOGO Journalier introuvable", logoJournalierXlsmPath);

        // ExcelDataReader needs this for legacy encodings
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        using var stream = File.Open(logoJournalierXlsmPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = ExcelReaderFactory.CreateReader(stream);
        var ds = reader.AsDataSet(); // we manage headers ourselves

        var dtPatients = FindSheet(ds, "RéférentielClients");
        var dtTarifs = FindSheet(ds, "TARIFS");

        // Séances: DB_JOURNALIER peut être dans LOGO Journalier OU dans BASE_FACTURATION.
        // On privilégie BASE_FACTURATION si un chemin est fourni.
        DataTable dtJournal;
        if (!string.IsNullOrWhiteSpace(baseFacturationXlsxPath) && File.Exists(baseFacturationXlsxPath!))
        {
            using var streamBase = File.Open(baseFacturationXlsxPath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var readerBase = ExcelReaderFactory.CreateReader(streamBase);
            var dsBase = readerBase.AsDataSet();
            dtJournal = FindSheet(dsBase, "DB_JOURNALIER");
        }
        else
        {
            dtJournal = FindSheet(ds, "DB_JOURNALIER");
        }

        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");

        using var tx = cn.BeginTransaction();

        // IMPORTANT: allow re-import without FK failures.
        // We must delete child tables BEFORE parents (patients/tarifs).
        WipeAllForImport(cn, tx);

        var p = ImportPatients_HeaderRow(cn, dtPatients, tx);
        var t = ImportTarifs_ByIndex(cn, dtTarifs, tx);
        var s = ImportSeances_FromJournalier_HeaderRow(cn, dtJournal, tx);

        // Optional extra imports (from files you provided)
        int invP = 0, invM = 0, pay = 0, loss = 0, comm = 0;

        if (!string.IsNullOrWhiteSpace(patientsInvoicesCsv) && File.Exists(patientsInvoicesCsv))
            invP = ImportInvoices_FromCsv(cn, patientsInvoicesCsv!, "patient", tx);

        if (!string.IsNullOrWhiteSpace(mutuellesInvoicesCsv) && File.Exists(mutuellesInvoicesCsv))
            invM = ImportInvoices_FromCsv(cn, mutuellesInvoicesCsv!, "mutuelle", tx);

        if (!string.IsNullOrWhiteSpace(paymentsCsv) && File.Exists(paymentsCsv))
            pay = ImportPayments_FromCsv(cn, paymentsCsv!, tx);

        if (!string.IsNullOrWhiteSpace(lossesCsv) && File.Exists(lossesCsv))
            loss = ImportLosses_FromCsv(cn, lossesCsv!, tx);

        if (!string.IsNullOrWhiteSpace(dbCommentairesXlsx) && File.Exists(dbCommentairesXlsx))
            comm = ImportComments_FromXlsx_ExcelDataReader(cn, dbCommentairesXlsx!, tx);

            // Import des révisions mutuelles (MUT_modifs.csv) → factures "modifiées" + table mutual_invoice_revisions
            int mutModifs = 0;
            if (!string.IsNullOrWhiteSpace(mutualModifsCsv) && File.Exists(mutualModifsCsv))
                mutModifs = ImportMutualModifs_FromCsv(cn, mutualModifsCsv!, tx);

        // Toujours resynchroniser paid_cents/status depuis la table payments (même si le fichier paiements n'a pas été importé).
        SyncInvoicePaidFromPayments(cn, tx);

        tx.Commit();

        return new ImportResult(p, t, s, invP, invM + mutModifs, pay, loss, comm);
    }

    private static void WipeAllForImport(IDbConnection cn, IDbTransaction tx)
    {
        // Children -> parents
        // NOTE: some early DBs were created without a few tables (losses, mutual_invoice_revisions, ...).
        // We guard deletes so "Réimporter" works even on older DBs without forcing users to delete the sqlite.
        bool TableExists(string name)
            => cn.ExecuteScalar<long>(
                   "SELECT COUNT(1) FROM sqlite_master WHERE type='table' AND name=@n;",
                   new { n = name }, transaction: tx) > 0;

        if (TableExists("payments")) cn.Execute("DELETE FROM payments;", transaction: tx);
        if (TableExists("losses")) cn.Execute("DELETE FROM losses;", transaction: tx);
        if (TableExists("mutual_invoice_revisions")) cn.Execute("DELETE FROM mutual_invoice_revisions;", transaction: tx);
        if (TableExists("invoice_lines")) cn.Execute("DELETE FROM invoice_lines;", transaction: tx);
        if (TableExists("invoices")) cn.Execute("DELETE FROM invoices;", transaction: tx);
        if (TableExists("seances")) cn.Execute("DELETE FROM seances;", transaction: tx);

        if (TableExists("tarifs")) cn.Execute("DELETE FROM tarifs;", transaction: tx);
        if (TableExists("patients")) cn.Execute("DELETE FROM patients;", transaction: tx);
    }

    public (int patients, int tarifs) ImportLogoJournalier(string xlsmPath)
    {
        // Legacy call kept for existing UI
        var r = ImportAll(
            logoJournalierXlsmPath: xlsmPath,
            baseFacturationXlsxPath: null,
            patientsInvoicesCsv: null,
            mutuellesInvoicesCsv: null,
            paymentsCsv: null,
            lossesCsv: null,
            dbCommentairesXlsx: null);

        return (r.Patients, r.Tarifs);
    }

    private static DataTable FindSheet(DataSet ds, string name)
    {
        // 1) match exact (trim + ignore case)
        foreach (DataTable t in ds.Tables)
            if (string.Equals(t.TableName?.Trim(), name, StringComparison.OrdinalIgnoreCase))
                return t;

        // 2) match "souple" : ignore espaces/_/-/$ + casse
        static string Norm(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            s = s.Trim();
            if (s.EndsWith("$", StringComparison.Ordinal)) s = s[..^1];
            s = s.Replace(" ", "").Replace("_", "").Replace("-", "");
            return s.ToUpperInvariant();
        }

        var wanted = Norm(name);
        foreach (DataTable t in ds.Tables)
            if (Norm(t.TableName) == wanted)
                return t;

        var available = string.Join(", ", ds.Tables.Cast<DataTable>().Select(t => $"'{t.TableName}'"));
        throw new InvalidOperationException($"Onglet '{name}' introuvable dans le fichier. Onglets disponibles: {available}");
    }

    // ---------- Patients (header row) ----------
    private static int ImportPatients_HeaderRow(IDbConnection cn, DataTable dt, IDbTransaction tx)
    {
        if (dt.Rows.Count == 0) return 0;

        var colIndex = BuildHeaderIndex(dt.Rows[0]);

        string Get(DataRow r, params string[] cols)
        {
            foreach (var col in cols)
            {
                if (!colIndex.TryGetValue(NormalizeHeader(col), out var idx)) continue;
                return S(r[idx]);
            }
            return "";
        }

        // Authoritative import
        cn.Execute("DELETE FROM patients;", transaction: tx);

        int count = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string ExtractCode3(DataRow r)
        {
            // 1) colonne nommée (quand elle existe)
            var raw = Get(r, "3 lettres");
            if (string.IsNullOrWhiteSpace(raw)) raw = Get(r, "Code 3 lettres");
            if (string.IsNullOrWhiteSpace(raw)) raw = Get(r, "Code");
            if (string.IsNullOrWhiteSpace(raw)) raw = Get(r, "Code3");

            // 2) fallback robuste : dans LOGO Journalier, le code 3 lettres est en colonne C (index 2)
            if (string.IsNullOrWhiteSpace(raw) && dt.Columns.Count > 2)
                raw = S(r[2]);

            raw = (raw ?? "").Trim();
            if (raw.Length == 0) return "";

            // Normalise: garde uniquement lettres, prend 3 premières
            var letters = new string(raw.Where(char.IsLetter).ToArray());
            if (letters.Length < 3) return "";
            var code = letters.Substring(0, 3).ToUpperInvariant();

            // Valide strict: 3 lettres A-Z (évite les IDs/date/n°)
            for (int k = 0; k < 3; k++)
                if (code[k] < 'A' || code[k] > 'Z') return "";
            return code;
        }

        for (int i = 1; i < dt.Rows.Count; i++)
        {
            var r = dt.Rows[i];

            var nom = Get(r, "Nom");
            var prenom = Get(r, "Prénom", "Prenom");
            var code3 = ExtractCode3(r);
            if (string.IsNullOrWhiteSpace(code3)) continue;
            if (!seen.Add(code3)) continue;

            var niss = Get(r, "Code NISS", "NISS");

            if (string.IsNullOrWhiteSpace(nom) && string.IsNullOrWhiteSpace(prenom) && string.IsNullOrWhiteSpace(code3))
                continue;

            var referend = Get(r, "REFEREND", "Referend");
            var rue = Get(r, "Rue");
            var numero = Get(r, "Numéro", "Numero");
            var adresse = string.Join(" ", new[] { rue, numero }.Where(s => !string.IsNullOrWhiteSpace(s)));

            var cp = Get(r, "CP");
            var ville = Get(r, "Ville");

            var pays = Get(r, "Pays");
            var mail = Get(r, "Mail", "Email", "E-mail");
            var tel = Get(r, "Telephone", "Téléphone", "Tel", "Gsm");

            var mutuelle = Get(r, "Mutuelle");
            var statut = NormalizeStatut(Get(r, "Statut")); // BIM / NON BIM

            var prenomMed = Get(r, "Prénom Méd Presc", "Prenom Med Presc");
            var nomMed = Get(r, "Nom Méd Presc", "Nom Med Presc");
            var codeMed = Get(r, "Code Medecin", "Code Médecin");

            var datePresc = Get(r, "Date de prescription");
            var dateAccord = Get(r, "Date d’accord", "Date d'accord");
            var periodeAccord = Get(r, "Période d’accord", "Periode d'accord");
            var nomenclature = Get(r, "Nomenclature");

            cn.Execute(@"
INSERT INTO patients(
  code3, nom, prenom, niss, statut, mutuelle,
  adresse, cp, ville,
  referend, numero, rue, pays, mail, telephone,
  prenom_med_presc, nom_med_presc, code_medecin,
  date_prescription, date_accord, periode_accord, nomenclature
)
VALUES (
  @code3,@nom,@prenom,@niss,@statut,@mutuelle,
  @adresse,@cp,@ville,
  @referend,@numero,@rue,@pays,@mail,@tel,
  @prenomMed,@nomMed,@codeMed,
  @datePresc,@dateAccord,@periodeAccord,@nomenclature
);",
                new
                {
                    code3 = S(code3),
                    nom = S(nom),
                    prenom = S(prenom),
                    niss = NullIfEmpty(niss),
                    statut,
                    mutuelle = NullIfEmpty(mutuelle),
                    adresse = NullIfEmpty(adresse),
                    cp = NullIfEmpty(cp),
                    ville = NullIfEmpty(ville),
                    referend = NullIfEmpty(referend),
                    numero = NullIfEmpty(numero),
                    rue = NullIfEmpty(rue),
                    pays = NullIfEmpty(pays),
                    mail = NullIfEmpty(mail),
                    tel = NullIfEmpty(tel),
                    prenomMed = NullIfEmpty(prenomMed),
                    nomMed = NullIfEmpty(nomMed),
                    codeMed = NullIfEmpty(codeMed),
                    datePresc = NullIfEmpty(datePresc),
                    dateAccord = NullIfEmpty(dateAccord),
                    periodeAccord = NullIfEmpty(periodeAccord),
                    nomenclature = NullIfEmpty(nomenclature)
                }, transaction: tx);

            count++;
        }

        return count;
    }

    // ---------- Tarifs (by index like before) ----------
    private static int ImportTarifs_ByIndex(IDbConnection cn, DataTable dt, IDbTransaction tx)
    {
        const int COL_LIBELLE = 1;  // B
        const int COL_PATIENT = 2;  // C
        const int COL_MUTUELLE = 3; // D

        cn.Execute("DELETE FROM tarifs;", transaction: tx);

        int count = 0;

        for (int i = 1; i < dt.Rows.Count; i++) // skip row 0
        {
            if (dt.Columns.Count <= COL_MUTUELLE) continue;

            var r = dt.Rows[i];

            var libelle = S(r[COL_LIBELLE]);
            if (string.IsNullOrWhiteSpace(libelle)) continue;

            var partPatient = MoneyToCents(r[COL_PATIENT]);
            var partMutuelle = MoneyToCents(r[COL_MUTUELLE]);

            cn.Execute(@"
INSERT INTO tarifs(libelle, part_patient, part_mutuelle, is_active)
VALUES (@libelle, @pp, @pm, 1);",
                new { libelle = libelle.Trim(), pp = partPatient, pm = partMutuelle }, transaction: tx);

            count++;
        }

        return count;
    }

    // ---------- Seances (DB_JOURNALIER header row) ----------
    private static int ImportSeances_FromJournalier_HeaderRow(IDbConnection cn, DataTable dt, IDbTransaction tx)
    {
        if (dt.Rows.Count == 0) return 0;

        var colIndex = BuildHeaderIndex(dt.Rows[0]);

        string Get(DataRow r, params string[] cols)
        {
            foreach (var col in cols)
            {
                if (!colIndex.TryGetValue(NormalizeHeader(col), out var idx)) continue;
                return S(r[idx]);
            }
            return "";
        }

        object? GetObj(DataRow r, params string[] cols)
        {
            foreach (var col in cols)
            {
                if (!colIndex.TryGetValue(NormalizeHeader(col), out var idx)) continue;
                return r[idx];
            }
            return null;
        }

        // Lookups
        var patients = cn.Query<(long id, string? code3, string? nom, string? prenom, string? niss)>(
            "SELECT id, code3, nom, prenom, niss FROM patients;", transaction: tx).ToList();

        var patientByNiss = patients
            .Where(p => !string.IsNullOrWhiteSpace(p.niss))
            .GroupBy(p => (p.niss ?? "").Trim())
            .ToDictionary(g => g.Key, g => g.First().id, StringComparer.OrdinalIgnoreCase);

        var patientByName = patients
            .Where(p => !string.IsNullOrWhiteSpace(p.nom) || !string.IsNullOrWhiteSpace(p.prenom))
            .GroupBy(p => NormalizeKey((p.nom ?? "") + "|" + (p.prenom ?? "")))
            .ToDictionary(g => g.Key, g => g.First().id, StringComparer.OrdinalIgnoreCase);

        var tarifByLib = cn.Query<(long id, string libelle)>("SELECT id, libelle FROM tarifs;", transaction: tx)
            .ToDictionary(t => NormalizeKey(t.libelle), t => t.id, StringComparer.OrdinalIgnoreCase);

        cn.Execute("DELETE FROM seances;", transaction: tx);

        int count = 0;

        for (int i = 1; i < dt.Rows.Count; i++)
        {
            var r = dt.Rows[i];

            var nom = Get(r, "Nom");
            var prenom = Get(r, "Prénom", "Prenom");
            var niss = Get(r, "NISS");

            long patientId = 0;

            if (!string.IsNullOrWhiteSpace(niss))
            {
                patientByNiss.TryGetValue(niss.Trim(), out patientId);
            }

            if (patientId == 0)
            {
                var key = NormalizeKey((nom ?? "") + "|" + (prenom ?? ""));
                patientByName.TryGetValue(key, out patientId);
            }

            if (patientId == 0)
                continue;

            // DATE : on la lit depuis DB_JOURNALIER (Excel peut fournir DateTime OU double OADate OU string)
            var dateIso = ToIsoDate(GetObj(r,
                "Date", "Jour", "Date séance", "Date seance", "DateSéance", "DateSeance", "Date ISO", "date_iso"));
            if (string.IsNullOrWhiteSpace(dateIso))
                continue;

            var tarifLabel = Get(r, "Tarif", "Nomenclature");
            if (string.IsNullOrWhiteSpace(tarifLabel)) continue;

            if (!tarifByLib.TryGetValue(NormalizeKey(tarifLabel), out var tarifId))
            {
                // if missing, create it from row amounts
                var ppNew = MoneyToCents(GetObj(r, "Facturé patient", "Facture patient"));
                var pmNew = MoneyToCents(GetObj(r, "Facturé mutuelle", "Facture mutuelle"));
                cn.Execute(@"INSERT INTO tarifs(libelle, part_patient, part_mutuelle, is_active) VALUES (@l,@pp,@pm,1);",
                    new { l = tarifLabel.Trim(), pp = ppNew, pm = pmNew }, transaction: tx);
                tarifId = cn.ExecuteScalar<long>("SELECT last_insert_rowid();", transaction: tx);
                tarifByLib[NormalizeKey(tarifLabel)] = tarifId;
            }

            var pp = MoneyToCents(GetObj(r, "Facturé patient", "Facture patient"));
            var pm = MoneyToCents(GetObj(r, "Facturé mutuelle", "Facture mutuelle"));
            var cash = ToBool(Get(r, "Cash patient", "Cash"));

            var commentaire = Get(r, "Commentaires", "Commentaire", "Note");
            var commentaireDb = string.IsNullOrWhiteSpace(commentaire) ? null : commentaire;

            cn.Execute(@"
INSERT INTO seances(patient_id, tarif_id, date_iso, is_cash, commentaire, part_patient, part_mutuelle)
VALUES(@patientId, @tarifId, @dateIso, @isCash, @commentaire, @pp, @pm);",
            new
            {
                patientId,
                tarifId,
                dateIso,
                isCash = cash ? 1 : 0,
                commentaire = commentaireDb,
                pp,
                pm
            }, transaction: tx);

            count++;
        }

        return count;
    }

    // ---------- INVOICES / PAYMENTS / LOSSES (CSV) ----------
    private static int ImportInvoices_FromCsv(IDbConnection cn, string csvPath, string kind, IDbTransaction tx)
    {
        var rows = ReadCsv(csvPath);

        cn.Execute("DELETE FROM invoice_lines WHERE invoice_id IN (SELECT id FROM invoices WHERE kind=@k);", new { k = kind }, transaction: tx);
        cn.Execute("DELETE FROM invoices WHERE kind=@k;", new { k = kind }, transaction: tx);

        // patient lookup by code3 and "Nom Prenom" (and keep details to build a nice recipient string)
        var pats = cn.Query<(long id, string nom, string prenom, string code3)>(
            "SELECT id, IFNULL(nom,''), IFNULL(prenom,''), IFNULL(code3,'') FROM patients;", transaction: tx).ToList();

        var patById = pats.ToDictionary(p => p.id, p => p);

        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in pats)
        {
            var k1 = NormalizeKey($"{p.nom} {p.prenom}");
            var k2 = NormalizeKey($"{p.prenom} {p.nom}");
            if (!string.IsNullOrWhiteSpace(k1) && !map.ContainsKey(k1)) map[k1] = p.id;
            if (!string.IsNullOrWhiteSpace(k2) && !map.ContainsKey(k2)) map[k2] = p.id;
            if (!string.IsNullOrWhiteSpace(p.code3))
            {
                var kc = NormalizeKey(p.code3);
                if (!map.ContainsKey(kc)) map[kc] = p.id;
            }
        }

        int count = 0;

        foreach (var r in rows)
        {
            var invoiceNo = r.Get("NumeroFacture", "InvoiceNo", "N°", "No", "Numero", "Numéro");
            var dateIso = ToIsoDate(r.Get("DateFacturation", "Date", "Date facture", "DateFacture"));
            var destin = r.Get("Destinataire", "Patient", "Mutuelle", "Nom");
            var period = r.Get("Periode", "Période", "Period");

            if (string.IsNullOrWhiteSpace(invoiceNo) || string.IsNullOrWhiteSpace(dateIso))
                continue;

            long? patientId = null;
            string? mutuelle = null;
            string? recipient = null;

            if (kind == "patient")
            {
                // 1) match sur nom/prénom ou référend (normalisé)
                var key = NormalizeKey(destin);
                if (!string.IsNullOrWhiteSpace(key) && map.TryGetValue(key, out var pid))
                    patientId = pid;

                // 2) fallback: si le CSV contient un "CODE3 — Nom Prénom" (ou juste CODE3), on mappe via code3
                if (patientId is null)
                {
                    var code3 = ExtractCode3(destin);
                    if (!string.IsNullOrWhiteSpace(code3) && map.TryGetValue(NormalizeKey(code3), out var pid2))
                        patientId = pid2;
                }

                // 3) dernier fallback: si destin est vide, on tente via code3 présent dans invoice_no (rare)
                if (patientId is null)
                {
                    var code3 = ExtractCode3(invoiceNo);
                    if (!string.IsNullOrWhiteSpace(code3) && map.TryGetValue(NormalizeKey(code3), out var pid3))
                        patientId = pid3;
                }

                // Recipient must always be filled for patient invoices.
                // If we have the patient id, we can build a stable "CODE - Nom Prenom" label.
                // Otherwise we keep the CSV destinataire as-is (avoids "PATIENT (non lié)" in UI).
                if (patientId is not null && patById.TryGetValue(patientId.Value, out var pdet))
                {
                    var code = (pdet.code3 ?? "").Trim().ToUpperInvariant();
                    var nom = (pdet.nom ?? "").Trim();
                    var prenom = (pdet.prenom ?? "").Trim();
                    recipient = string.IsNullOrWhiteSpace(code)
                        ? $"{nom} {prenom}".Trim()
                        : $"{code} - {nom} {prenom}".Trim();
                }
                else
                {
                    recipient = string.IsNullOrWhiteSpace(destin) ? null : destin.Trim();
                }
            }
            else
            {
                mutuelle = string.IsNullOrWhiteSpace(destin) ? null : destin.Trim();
                recipient = mutuelle;
            }

            var total = MoneyToCents(r.Get("Montant", "Total"));
            var pdfPath = r.Get("PdfPath", "PDF", "Chemin", "Path");

            // Heuristics for vintage credit notes imported from the patient invoices CSV:
            // - invoice number starts with NC-
            // - or total amount is negative
            var actualKind = kind;
            if (string.Equals(kind, "patient", StringComparison.OrdinalIgnoreCase))
            {
                if (invoiceNo.Trim().StartsWith("NC-", StringComparison.OrdinalIgnoreCase) || total < 0)
                    actualKind = "credit_note";
            }

            // Le montant payé et le statut viennent uniquement du fichier des paiements.
            // Une facture absente du fichier des paiements reste impayée (paid_cents = 0, solde = total).
            var paidCents = 0;
            var statusForImport = "unpaid";

            cn.Execute(@"
INSERT INTO invoices(invoice_no, kind, patient_id, mutuelle, date_iso, total_cents, paid_cents, status, ref_doc, recipient, period)
VALUES(@invoiceNo, @kind, @patientId, @mutuelle, @dateIso, @total, @paidCents, @status, @refDoc, @recipient, @period);",
                new
                {
                    invoiceNo = invoiceNo.Trim(),
                    kind = actualKind,
                    patientId,
                    mutuelle,
                    dateIso,
                    total,
                    paidCents,
                    status = statusForImport,
                    refDoc = string.IsNullOrWhiteSpace(pdfPath) ? null : pdfPath.Trim(),
                    recipient = string.IsNullOrWhiteSpace(recipient) ? null : recipient.Trim(),
                    period = string.IsNullOrWhiteSpace(period) ? null : period.Trim()
                }, transaction: tx);

            var invId = cn.ExecuteScalar<long>("SELECT last_insert_rowid();", transaction: tx);

            cn.Execute(@"
INSERT INTO invoice_lines(invoice_id, label, qty, unit_price_cents, total_cents, patient_part_cents, mutuelle_part_cents, date_iso)
VALUES(@invId, @label, 1, @total, @total, @pp, @pm, @dateIso);",
                new
                {
                    invId,
                    label = actualKind == "mutuelle" ? "Prestations mutuelle"
                          : actualKind == "credit_note" ? "Note de crédit"
                          : "Prestations",
                    total,
                    pp = actualKind == "patient" ? total : 0,
                    pm = actualKind == "mutuelle" ? total : 0,
                    dateIso
                }, transaction: tx);

            count++;
        }

        return count;
    }

    private static int ImportPayments_FromCsv(IDbConnection cn, string csvPath, IDbTransaction tx)
    {
        var rows = ReadCsv(csvPath);
        cn.Execute("DELETE FROM payments;", transaction: tx);

        int count = 0;

        foreach (var r in rows)
        {
            var key = r.Get("InvoiceKey", "Invoice", "Facture");
            if (string.IsNullOrWhiteSpace(key))
            {
                var invNoAlt = r.Get("NumeroFacture", "InvoiceNo", "N°", "No", "Numero", "Numéro");
                var typeAlt = r.Get("Type");
                if (!string.IsNullOrWhiteSpace(invNoAlt))
                {
                    var kindAlt = (typeAlt ?? "").Trim().ToUpperInvariant().StartsWith("MUT") ? "mutuelle" : "patient";
                    key = $"{kindAlt}|{invNoAlt.Trim()}";
                }
            }

            var paidDateIso = ToIsoDate(r.Get("PaidDate", "Date", "DatePaiement", "DatePaiemer", "Date paiement", "Date de paiement"));
            var amount = MoneyToCents(r.Get("Amount", "Montant", "MontantPaye"));
            var method = r.Get("Method", "Methode", "Méthode");
            var reference = r.Get("Reference", "Ref", "Référence", "Réf");

            if (string.IsNullOrWhiteSpace(key) || amount <= 0)
                continue;

            ParseInvoiceKey(key, out var kind, out var invoiceNo);

            var invRow = FindInvoiceByIdentifier(cn, tx, kind, invoiceNo);
            if (invRow.id == 0) continue;

            // Si la date de paiement est vide, utiliser la date de la facture pour ne pas perdre le paiement
            var paidDateToStore = !string.IsNullOrWhiteSpace(paidDateIso)
                ? paidDateIso
                : (invRow.date_iso != null && invRow.date_iso.Length >= 10 ? invRow.date_iso.Substring(0, 10) : DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

            cn.Execute(@"
INSERT INTO payments(invoice_id, paid_date, amount_cents, method, reference, created_at)
VALUES(@invoiceId, @paidDate, @amount, @method, @reference, datetime('now'));",
                new
                {
                    invoiceId = invRow.id,
                    paidDate = paidDateToStore,
                    amount,
                    method = string.IsNullOrWhiteSpace(method) ? "TRANSFER" : method.Trim(),
                    reference = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim()
                }, transaction: tx);

            count++;
        }

        SyncInvoicePaidFromPayments(cn, tx);
        return count;
    }

    /// <summary>Source de vérité : paid_cents depuis payments ; status = 'loss' si pertes en base, sinon paid/partial/unpaid depuis payments. Factures « modifiées » (MUT) exclues.</summary>
    private static void SyncInvoicePaidFromPayments(IDbConnection cn, IDbTransaction tx)
    {
        // 1) paid_cents pour toutes les factures (sauf modified)
        cn.Execute(@"
UPDATE invoices
SET paid_cents = (SELECT COALESCE(SUM(amount_cents), 0) FROM payments p WHERE p.invoice_id = invoices.id)
WHERE status IS NULL OR status != 'modified';", transaction: tx);

        // 2) status : si la facture a des lignes dans losses → 'loss', sinon paid/partial/unpaid selon les paiements
        cn.Execute(@"
UPDATE invoices
SET status = CASE
  WHEN id IN (SELECT DISTINCT invoice_id FROM losses) THEN 'loss'
  WHEN (SELECT COALESCE(SUM(amount_cents), 0) FROM payments p WHERE p.invoice_id = invoices.id) >= total_cents THEN 'paid'
  WHEN (SELECT COALESCE(SUM(amount_cents), 0) FROM payments p WHERE p.invoice_id = invoices.id) > 0 THEN 'partial'
  ELSE 'unpaid'
END
WHERE status IS NULL OR status != 'modified';", transaction: tx);
    }

    private static int ImportLosses_FromCsv(IDbConnection cn, string csvPath, IDbTransaction tx)
    {
        var rows = ReadCsv(csvPath);
        cn.Execute("DELETE FROM losses;", transaction: tx);

        int count = 0;

        foreach (var r in rows)
        {
            var key = r.Get("InvoiceKey", "Invoice", "Facture");
            if (string.IsNullOrWhiteSpace(key))
            {
                var invNoAlt = r.Get("NumeroFacture", "InvoiceNo", "N°", "No", "Numero", "Numéro");
                var typeAlt = r.Get("Type");
                if (!string.IsNullOrWhiteSpace(invNoAlt))
                {
                    var kindAlt = (typeAlt ?? "").Trim().ToUpperInvariant().StartsWith("MUT") ? "mutuelle" : "patient";
                    key = $"{kindAlt}|{invNoAlt.Trim()}";
                }
            }

            var lossDateIso = ToIsoDate(r.Get("LossDate", "Date", "DatePerte", "DatePerte", "Date perte", "Date de perte", "CreatedAt"));
            var amount = MoneyToCents(r.Get("Amount", "Montant", "MontantPerdu"));
            var reason = r.Get("Reason", "Raison", "Motif", "Commentaire");

            if (string.IsNullOrWhiteSpace(key) || amount == 0)
                continue;

            ParseInvoiceKey(key, out var kind, out var invoiceNo);

            var invRow = FindInvoiceByIdentifier(cn, tx, kind, invoiceNo);
            if (invRow.id == 0) continue;

            var lossDateToStore = !string.IsNullOrWhiteSpace(lossDateIso)
                ? lossDateIso
                : (invRow.date_iso != null && invRow.date_iso.Length >= 10 ? invRow.date_iso.Substring(0, 10) : DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

            cn.Execute(@"
INSERT INTO losses(invoice_id, loss_date, amount_cents, reason, created_at)
VALUES(@invoiceId, @lossDate, @amount, @reason, datetime('now'));",
                new { invoiceId = invRow.id, lossDate = lossDateToStore, amount, reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim() }, transaction: tx);

            cn.Execute(@"UPDATE invoices SET status='loss' WHERE id=@id;", new { id = invRow.id }, transaction: tx);

            count++;
        }

        return count;
    }

    /// <summary>Import MUT_modifs.csv : crée les factures mutuelles "modifiées" et les révisions.</summary>
    private static int ImportMutualModifs_FromCsv(IDbConnection cn, string csvPath, IDbTransaction tx)
    {
        var rows = ReadCsv(csvPath);
        int count = 0;

        foreach (var r in rows)
        {
            var invoiceNo = r.Get("NumeroFacture", "InvoiceNo", "N°").Trim();
            var mutuelle = r.Get("Mutuelle", "MutuelleName").Trim();
            var nouveauMontant = MoneyToCents(r.Get("NouveauMontant", "NouveauMontantEuro", "Montant"));
            var motif = r.Get("Motif", "Reason", "Raison").Trim();
            var refDoc = r.Get("RefDoc", "Reference", "ReferenceDoc").Trim();
            var changedAt = ToIsoDateTime(r.Get("DateTime", "Date", "ChangedAt"));

            if (string.IsNullOrWhiteSpace(invoiceNo) || nouveauMontant <= 0) continue;

            var origId = cn.ExecuteScalar<long?>("SELECT id FROM invoices WHERE kind='mutuelle' AND invoice_no=@n;", new { n = invoiceNo }, transaction: tx);
            if (origId is null) continue;

            var orig = cn.QueryFirstOrDefault<(string? mutuelle, string? date_iso, string? recipient, string? period)>(
                "SELECT mutuelle, date_iso, recipient, period FROM invoices WHERE id=@id;", new { id = origId.Value }, transaction: tx);
            if (orig == default) continue;

            var status = cn.ExecuteScalar<string?>("SELECT status FROM invoices WHERE id=@id;", new { id = origId.Value }, transaction: tx);
            var isSuperseded = string.Equals(status, "superseded", StringComparison.OrdinalIgnoreCase);

            if (!isSuperseded)
            {
                var revisionNo = cn.ExecuteScalar<int>("SELECT COALESCE(MAX(revision_no),0)+1 FROM mutual_invoice_revisions WHERE invoice_id=@id;", new { id = origId.Value }, transaction: tx);
                cn.Execute(@"
INSERT INTO mutual_invoice_revisions(invoice_id, revision_no, changed_at, new_total_cents, reason, reference_doc)
VALUES (@invoice_id, @revision_no, @changed_at, @new_total_cents, @reason, @reference_doc);",
                    new
                    {
                        invoice_id = origId.Value,
                        revision_no = revisionNo,
                        changed_at = string.IsNullOrEmpty(changedAt) ? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : changedAt,
                        new_total_cents = nouveauMontant,
                        reason = motif,
                        reference_doc = refDoc
                    }, transaction: tx);

                cn.Execute("UPDATE invoices SET status='superseded' WHERE id=@id;", new { id = origId.Value }, transaction: tx);
            }

            var modInvoiceNo = $"{invoiceNo}-MOD";
            var existingModId = cn.ExecuteScalar<long?>("SELECT id FROM invoices WHERE kind='mutuelle' AND invoice_no=@n AND ref_invoice_id=@refId;",
                new { n = modInvoiceNo, refId = origId.Value }, transaction: tx);

            if (existingModId is null)
            {
                cn.Execute(@"
INSERT INTO invoices(invoice_no, kind, patient_id, mutuelle, date_iso, total_cents, paid_cents, status, ref_invoice_id, reason, ref_doc, recipient, period)
VALUES (@invoice_no, 'mutuelle', NULL, @mutuelle, @date_iso, @total_cents, @paid_cents, 'modified', @ref_invoice_id, @reason, @ref_doc, @recipient, @period);",
                    new
                    {
                        invoice_no = modInvoiceNo,
                        mutuelle = orig.mutuelle ?? mutuelle,
                        date_iso = orig.date_iso ?? "",
                        total_cents = nouveauMontant,
                        paid_cents = nouveauMontant,
                        ref_invoice_id = origId.Value,
                        reason = motif,
                        ref_doc = refDoc,
                        recipient = orig.recipient ?? mutuelle,
                        period = orig.period ?? ""
                    }, transaction: tx);
                count++;
            }
            else
            {
                cn.Execute("UPDATE invoices SET total_cents=@t, paid_cents=@t, reason=@r, ref_doc=@d WHERE id=@id;",
                    new { t = nouveauMontant, r = motif, d = refDoc, id = existingModId.Value }, transaction: tx);
                var revNo = cn.ExecuteScalar<int>("SELECT COALESCE(MAX(revision_no),0)+1 FROM mutual_invoice_revisions WHERE invoice_id=@id;", new { id = origId.Value }, transaction: tx);
                cn.Execute(@"
INSERT INTO mutual_invoice_revisions(invoice_id, revision_no, changed_at, new_total_cents, reason, reference_doc)
VALUES (@invoice_id, @revision_no, @changed_at, @new_total_cents, @reason, @reference_doc);",
                    new
                    {
                        invoice_id = origId.Value,
                        revision_no = revNo,
                        changed_at = string.IsNullOrEmpty(changedAt) ? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : changedAt,
                        new_total_cents = nouveauMontant,
                        reason = motif,
                        reference_doc = refDoc
                    }, transaction: tx);
            }
        }

        return count;
    }

    private static string ToIsoDateTime(string? s)
    {
        s = (s ?? "").Trim();
        if (string.IsNullOrWhiteSpace(s)) return "";
        if (DateTime.TryParseExact(s, new[] { "dd-MM-yy HH:mm", "d-M-yy H:m", "dd/MM/yyyy HH:mm", "yyyy-MM-dd HH:mm:ss" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        if (DateTime.TryParse(s, CultureInfo.GetCultureInfo("fr-BE"), DateTimeStyles.None, out d))
            return d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        return "";
    }

    // ---------- COMMENTS (ExcelDataReader instead of ClosedXML to avoid URI issues) ----------
    private static int ImportComments_FromXlsx_ExcelDataReader(IDbConnection cn, string xlsxPath, IDbTransaction tx)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        using var stream = File.Open(xlsxPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = ExcelReaderFactory.CreateReader(stream);
        var ds = reader.AsDataSet();

        // take first sheet
        var dt = ds.Tables.Count > 0 ? ds.Tables[0] : null;
        if (dt is null || dt.Rows.Count == 0) return 0;

        // === patients comment column: older DBs may not have it, or may use a different name
        // We accept these names (first match wins): commentaire, commentaires, note
        string ResolvePatientCommentColumn()
        {
            // PRAGMA table_info returns columns with fields: cid, name, type, notnull, dflt_value, pk
            var cols = cn.Query<PragmaCol>("PRAGMA table_info(patients);", transaction: tx)
                         .Select(x => (x.Name ?? "").Trim())
                         .Where(x => x.Length > 0)
                         .ToList();

            bool Has(string n) => cols.Any(c => string.Equals(c, n, StringComparison.OrdinalIgnoreCase));

            if (Has("commentaire")) return "commentaire";
            if (Has("commentaires")) return "commentaires";
            if (Has("note")) return "note";

            // Create the canonical column if none exists
            cn.Execute("ALTER TABLE patients ADD COLUMN commentaire TEXT;", transaction: tx);
            return "commentaire";
        }

        var commentCol = ResolvePatientCommentColumn();

        // Whitelist to safely inject identifier (SQLite doesn't support parameterizing column names)
        if (!(commentCol.Equals("commentaire", StringComparison.OrdinalIgnoreCase)
           || commentCol.Equals("commentaires", StringComparison.OrdinalIgnoreCase)
           || commentCol.Equals("note", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Nom de colonne commentaire inattendu: " + commentCol);

        var sql = $"UPDATE patients SET {commentCol}=@c WHERE UPPER(code3)=UPPER(@code3);";

        int count = 0;
        for (int i = 1; i < dt.Rows.Count; i++) // skip header
        {
            var code3 = S(dt.Rows[i][0]);
            var comm = S(dt.Rows[i][1]);
            if (string.IsNullOrWhiteSpace(code3) || string.IsNullOrWhiteSpace(comm)) continue;

            // normalize OpenXML newline escape + Windows newlines to "\n"
            comm = comm
                .Replace("_x000D_", "\n")
                .Replace("\r\n", "\n")
                .Replace("\r", "\n");

            cn.Execute(sql, new { c = comm, code3 = code3.Trim() }, transaction: tx);
            count++;
        }
        return count;
    }
// ---------- Helpers ----------
    private static Dictionary<string, int> BuildHeaderIndex(DataRow headerRow)
    {
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int c = 0; c < headerRow.Table.Columns.Count; c++)
        {
            var h = S(headerRow[c]);
            if (string.IsNullOrWhiteSpace(h)) continue;
            var key = NormalizeHeader(h);
            if (!d.ContainsKey(key))
                d[key] = c;
        }
        return d;
    }

    private static string S(object? v) => (v?.ToString() ?? "").Trim();

    private static string? NullIfEmpty(string? s)
    {
        s = S(s);
        return s.Length == 0 ? null : s;
    }

    private static string NormalizeHeader(string? s)
    {
        s = (s ?? "").Trim();
        s = s.Replace(" ", " ");
        s = RemoveDiacritics(s);
        s = s.Replace("’", "'").Replace("´", "'").Replace("`", "'");
        s = RegexCollapseSpaces(s).ToLowerInvariant();
        return s;
    }

    // Normalise une clé "humaine" (destinataire, patient, référend, etc.) pour matcher malgré
    // les tirets longs, ponctuation, doubles espaces, accents, etc.
    // Exemple: "ABG — Abrassart Gabriel" => "ABG ABRASSART GABRIEL"
    private static string NormalizeKey(string? s)
    {
        s = (s ?? "").Trim();
        // NB: espace insécable (Excel/Word)
        s = s.Replace(" ", " ");
        s = RemoveDiacritics(s);
        s = s.Replace("’", "'").Replace("´", "'").Replace("`", "'");
        s = s.ToUpperInvariant();

        // remplace tout ce qui n'est pas lettre/chiffre par un espace (y compris — / - / _ / , etc.)
        s = Regex.Replace(s, @"[^A-Z0-9]+", " ");
        s = RegexCollapseSpaces(s);
        return s;
    }

    private static string ExtractCode3(string? s)
    {
        var t = NormalizeKey(s);
        var m = Regex.Match(t, @"\b([A-Z]{3})\b");
        return m.Success ? m.Groups[1].Value : "";
    }

    private static string RegexCollapseSpaces(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    private static string RemoveDiacritics(string text)
    {
        var formD = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(formD.Length);
        foreach (var ch in formD)
        {
            var uc = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (uc != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string NormalizeStatut(string? s)
    {
        var k = NormalizeKey(s);
        if (k.Contains("BIM")) return "BIM";
        return "NON BIM";
    }

    private static string NormalizeInvoiceStatus(string? s)
    {
        var k = NormalizeKey(s);
        if (k.Contains("PAY") || k.Contains("PAI")) return "paid";
        if (k.Contains("PERTE") || k.Contains("LOSS")) return "loss";
        if (k.Contains("PART")) return "partial";
        if (k.Contains("IMP")) return "unpaid";
        return "unpaid";
    }

    private static bool ToBool(string? s)
    {
        var k = NormalizeKey(s);
        return k == "1" || k == "TRUE" || k == "VRAI" || k == "OUI" || k == "YES" || k == "X";
    }

    private static string ToIsoDate(object? v)
    {
        if (v is null) return "";
        // ExcelDataReader: dates can come as DateTime, or numeric OADate
        if (v is DateTime dt) return dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (v is double dd)
        {
            try
            {
                var d = DateTime.FromOADate(dd);
                return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            catch { /* ignore */ }
        }
        return ToIsoDate(S(v));
    }

    private static string ToIsoDate(string? s)
    {
        s = S(s);
        if (string.IsNullOrWhiteSpace(s)) return "";

        if (Regex.IsMatch(s, @"^\d{4}-\d{2}-\d{2}$"))
            return s;

        // Format date seule
        if (DateTime.TryParseExact(s,
            new[] { "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "dd.MM.yyyy", "d.M.yyyy" },
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        // Format date + heure (ex. export CSV "27/01/2026 14:01")
        if (DateTime.TryParseExact(s,
            new[] { "dd/MM/yyyy HH:mm", "dd/MM/yyyy H:mm", "dd/MM/yyyy HH:mm:ss", "dd/MM/yyyy H:mm:ss" },
            CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
            return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        // Nombre sériel Excel (OADate) quand le CSV a été enregistré depuis Excel
        if (decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var num))
        {
            try
            {
                var dt = DateTime.FromOADate((double)num);
                return dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            catch { /* hors plage OADate */ }
        }

        if (DateTime.TryParse(s, CultureInfo.GetCultureInfo("fr-BE"), DateTimeStyles.None, out d))
            return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        return "";
    }

    private static int MoneyToCents(object? v)
    {
        if (v is null) return 0;

        // ExcelDataReader commonly returns double for numeric cells
        try
        {
            if (v is double dd) return (int)Math.Round((decimal)dd * 100m, MidpointRounding.AwayFromZero);
            if (v is float ff) return (int)Math.Round((decimal)ff * 100m, MidpointRounding.AwayFromZero);
            if (v is decimal dec) return (int)Math.Round(dec * 100m, MidpointRounding.AwayFromZero);
            if (v is int ii) return ii;
            if (v is long ll) return (int)ll;
        }
        catch { /* ignore */ }

        return MoneyToCents(S(v));
    }

    private static int MoneyToCents(string? s)
    {
        s = (s ?? "").Trim();
        if (s.Length == 0) return 0;

        s = s.Replace("\u00A0", " ");
        s = s.Replace("€", "").Replace("EUR", "", StringComparison.OrdinalIgnoreCase);
        s = s.Replace(" ", "").Replace("\t", "");
        s = s.Replace(",", ".");
        s = s.Replace("−", "-");
        if (s.StartsWith("(") && s.EndsWith(")")) s = "-" + s.Trim('(', ')');

        if (!decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
            return 0;

        return (int)Math.Round(d * 100m, MidpointRounding.AwayFromZero);
    }

        private sealed class PragmaCol
    {
        // Map PRAGMA table_info(...) column 'name'
        public string? Name { get; set; }
    }

private sealed class CsvRow
    {
        private readonly Dictionary<string, string> _d;
        public CsvRow(Dictionary<string, string> d) => _d = d;

        public string Get(params string[] names)
        {
            foreach (var n in names)
            {
                if (_d.TryGetValue(n, out var v)) return v ?? "";
                var key = NormalizeHeader(n);
                foreach (var kv in _d)
                    if (NormalizeHeader(kv.Key) == key) return kv.Value ?? "";
            }
            return "";
        }
    }

    private static List<CsvRow> ReadCsv(string path)
    {
        var rawLines = new List<string>();
        using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var sr = new StreamReader(stream, Encoding.GetEncoding(1252), detectEncodingFromByteOrderMarks: true))
        {
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                rawLines.Add(line);
            }
        }
        if (rawLines.Count == 0) return new();

        // Détection du séparateur : si la première ligne contient plus de colonnes avec virgule qu'avec point-virgule, utiliser la virgule
        var firstLine = rawLines[0];
        var partsSemi = ParseCsvLine(firstLine, ';');
        var partsComma = ParseCsvLine(firstLine, ',');
        var useComma = partsComma.Length > partsSemi.Length && partsComma.Length >= 2;
        var separator = useComma ? ',' : ';';

        var lines = rawLines.Select(l => ParseCsvLine(l, separator)).ToList();
        var headers = lines[0].Select(CleanCsvCell).ToArray();
        var res = new List<CsvRow>();

        for (int i = 1; i < lines.Count; i++)
        {
            var parts = lines[i];
            if (parts.All(p => string.IsNullOrWhiteSpace(CleanCsvCell(p)))) continue;

            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int c = 0; c < headers.Length && c < parts.Length; c++)
                d[headers[c]] = CleanCsvCell(parts[c]);

            res.Add(new CsvRow(d));
        }
        return res;
    }

    private static string[] ParseCsvLine(string line, char separator)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            var ch = line[i];

            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                    continue;
                }

                inQuotes = !inQuotes;
                continue;
            }

            if (ch == separator && !inQuotes)
            {
                parts.Add(sb.ToString());
                sb.Clear();
                continue;
            }

            sb.Append(ch);
        }

        parts.Add(sb.ToString());
        return parts.ToArray();
    }


    private static string CleanCsvCell(string? s)
    {
        s = (s ?? "").Trim().Trim('\uFEFF').Trim();
        if (s.Length >= 2 && s.StartsWith("\"") && s.EndsWith("\""))
            s = s.Substring(1, s.Length - 2);
        return s.Trim();
    }

    private static void ParseInvoiceKey(string invoiceKey, out string kind, out string invoiceNo)
    {
        kind = "patient";
        invoiceNo = invoiceKey.Trim();

        var parts = invoiceKey.Split('|');
        if (parts.Length >= 2)
        {
            var t = NormalizeKey(parts[0]);
            invoiceNo = parts[1].Trim();
            kind = t.StartsWith("MUT", StringComparison.OrdinalIgnoreCase) ? "mutuelle" : "patient";
        }

        invoiceNo = NormalizeInvoiceNoForLookup(invoiceNo);
    }

    /// <summary>Recherche une facture par kind + invoice_no. Si non trouvée et kind=patient, réessaie avec kind=mutuelle (clé CSV sans type ex. "02-2026-02 SOLIDARIS").</summary>
    private static (long id, string? date_iso) FindInvoiceByIdentifier(IDbConnection cn, IDbTransaction tx, string kind, string invoiceNo)
    {
        if (string.IsNullOrWhiteSpace(invoiceNo)) return (0, null);
        var row = cn.QueryFirstOrDefault<(long id, string? date_iso)>(
            "SELECT id, date_iso FROM invoices WHERE kind=@k AND invoice_no=@n;",
            new { k = kind, n = invoiceNo }, transaction: tx);
        if (row.id != 0) return row;
        if (string.Equals(kind, "patient", StringComparison.OrdinalIgnoreCase))
        {
            row = cn.QueryFirstOrDefault<(long id, string? date_iso)>(
                "SELECT id, date_iso FROM invoices WHERE kind='mutuelle' AND invoice_no=@n;",
                new { n = invoiceNo }, transaction: tx);
        }
        return row;
    }

    /// <summary>Extrait le numéro de facture au format MM-YYYY-NN quand la clé contient un espace (ex. "01-2025 01-2025-01" → "01-2025-01").</summary>
    private static string NormalizeInvoiceNoForLookup(string invoiceNo)
    {
        if (string.IsNullOrWhiteSpace(invoiceNo)) return invoiceNo;
        var s = invoiceNo.Trim();
        if (!s.Contains(' ')) return s;
        var match = Regex.Match(s, @"\b(\d{2}-\d{4}-\d{2})\b");
        return match.Success ? match.Groups[1].Value : s;
    }

    /// <summary>
    /// Import "1 clic" depuis l'arborescence standardisée sous Documents\PARAFACTO_Native.
    /// Répertoire utilisé : Documents\PARAFACTO_Native (ou OneDrive\Documents\PARAFACTO_Native).
    /// </summary>
    public ImportResult ImportAllFromDefaultFolder(string rootFolder)
    {
        if (string.IsNullOrWhiteSpace(rootFolder))
            throw new ArgumentException("rootFolder is required", nameof(rootFolder));

        if (!Directory.Exists(rootFolder))
            throw new InvalidOperationException(
                $"Le dossier d'import n'existe pas :\n{rootFolder}\n\nCréez ce dossier et placez-y au minimum le fichier \"LOGO Journalier.xlsm\".");

        var logoJournalier = Path.Combine(rootFolder, "LOGO Journalier.xlsm");
        if (!File.Exists(logoJournalier))
            throw new FileNotFoundException(
                $"Fichier obligatoire introuvable :\n{logoJournalier}\n\nPlacez \"LOGO Journalier.xlsm\" dans le dossier :\n{rootFolder}",
                logoJournalier);

        var baseFacturation = Path.Combine(rootFolder, "BASE_FACTURATION.xlsx");
        var dbCommentaires = Path.Combine(rootFolder, "DB_COMMENTAIRES.xlsx");

        var patientsInvoices = Path.Combine(rootFolder, "patients_invoices_log.csv");
        var mutuellesInvoices = Path.Combine(rootFolder, "mutuelles_invoices_log.csv");

        var paiementsFolder = Path.Combine(rootFolder, "FICHIERS PAIEMENTS");
        var payments = Path.Combine(paiementsFolder, "payments_log.csv");
        var losses = Path.Combine(paiementsFolder, "losses_log.csv");
        if (!File.Exists(payments)) payments = Path.Combine(rootFolder, "payments_log.csv");
        if (!File.Exists(losses)) losses = Path.Combine(rootFolder, "losses_log.csv");

        var mutModifs = Path.Combine(rootFolder, "MUT_modifs.csv");
        if (!File.Exists(mutModifs)) mutModifs = null;

        return ImportAll(
            logoJournalierXlsmPath: logoJournalier,
            baseFacturationXlsxPath: baseFacturation,
            patientsInvoicesCsv: patientsInvoices,
            mutuellesInvoicesCsv: mutuellesInvoices,
            paymentsCsv: payments,
            lossesCsv: losses,
            dbCommentairesXlsx: dbCommentaires,
            mutualModifsCsv: mutModifs
        );
    }

    /// <summary>
    /// Surcharge pratique sans paramètre, pour les ViewModels.
    /// </summary>
    public ImportResult ImportAllFromDefaultFolder()
        => ImportAllFromDefaultFolder(GetDefaultWorkspaceRoot());
}
