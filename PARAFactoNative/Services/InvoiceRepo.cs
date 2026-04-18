using System;
using System.Collections.Generic;
using System.Globalization;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using PARAFactoNative.Models;

namespace PARAFactoNative.Services;

public sealed class InvoiceRepo
{
    /// <summary>Recherche avec filtre optionnel par plage de dates (date_iso) ou par numéro de facture (invoice_no préfixes MM-YYYY).
    /// Quand invoiceNoMonthPrefixes est fourni, on filtre sur invoice_no (ex. 04-2025-.., NC-04-2025-..) et on ignore dateFrom/dateTo.</summary>
    public List<Invoice> Search(string? kind, DateTime? dateFrom, DateTime? dateTo, string? status, string? q,
        List<string>? invoiceNoMonthPrefixes = null)
    {
        kind = (kind ?? "ANY").Trim();
        status = (status ?? "ANY").Trim();
        q = (q ?? "").Trim();

        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");

        var sql = @"
SELECT
  i.id            AS Id,
  i.invoice_no    AS InvoiceNo,
  i.kind          AS Kind,
  i.patient_id    AS PatientId,
  i.mutuelle      AS Mutuelle,
  i.date_iso      AS DateIso,
  i.total_cents   AS TotalCents,
  i.paid_cents    AS PaidCents,
  i.status        AS Status,
  i.ref_invoice_id AS RefInvoiceId,
  i.reason        AS Reason,
  i.ref_doc       AS RefDoc,
  i.user_comment  AS UserComment,
  (SELECT MAX(p.paid_date) FROM payments p WHERE p.invoice_id = CASE WHEN (i.status = 'modified' AND i.ref_invoice_id IS NOT NULL AND i.ref_invoice_id > 0) THEN i.ref_invoice_id ELSE i.id END) AS LastPaymentDateIso,
  CASE
    WHEN TRIM(COALESCE(i.recipient,'')) <> '' THEN TRIM(i.recipient)
    WHEN i.kind = 'mutuelle' THEN COALESCE(i.mutuelle,'')
    WHEN p.id IS NOT NULL AND TRIM(COALESCE(p.referend,'')) <> '' THEN TRIM(p.referend)
    WHEN p.id IS NOT NULL THEN TRIM(COALESCE(p.nom,'') || ' ' || COALESCE(p.prenom,''))
    WHEN i.kind = 'credit_note' THEN 'NOTE DE CREDIT'
    ELSE COALESCE(i.mutuelle,'')
  END AS Recipient
FROM invoices i
LEFT JOIN patients p ON p.id = i.patient_id
WHERE 1=1
";

        var dyn = new DynamicParameters();

        if (!string.Equals(kind, "ANY", StringComparison.OrdinalIgnoreCase))
        {
            sql += " AND i.kind = @kind\n";
            dyn.Add("kind", kind.ToLowerInvariant());
        }

        if (!string.Equals(status, "ANY", StringComparison.OrdinalIgnoreCase))
        {
            var statusLower = status.ToLowerInvariant();
            if (statusLower == "paid")
            {
                // Payé = facture soldée (hors « loss » partiellement payée : celle-ci va dans Partielles + Pertes).
                sql += " AND (lower(trim(i.status)) = 'paid' OR lower(trim(i.status)) = 'acquittee')\n";
            }
            else if (statusLower == "partial")
            {
                sql += @" AND (
  lower(trim(i.status)) = 'partial'
  OR (
    lower(trim(i.status)) = 'loss'
    AND lower(trim(COALESCE(i.kind,''))) <> 'credit_note'
    AND i.paid_cents > 0
    AND i.paid_cents < i.total_cents
  )
)
";
            }
            else if (statusLower == "loss")
            {
                // Exclure les factures encore marquées « loss » sans perte nette (statut obsolète ou somme des lignes = 0).
                sql += @" AND i.status = 'loss'
  AND COALESCE((SELECT SUM(l.amount_cents) FROM losses l WHERE l.invoice_id = i.id), 0) != 0
";
            }
            else
            {
                sql += " AND i.status = @status\n";
                dyn.Add("status", statusLower);
            }
        }

        if (invoiceNoMonthPrefixes != null && invoiceNoMonthPrefixes.Count > 0)
        {
            // Période basée sur le numéro de facture (MM-YYYY) : factures 04-2025-.. et NC-04-2025-..
            var orParts = new List<string>();
            for (var i = 0; i < invoiceNoMonthPrefixes.Count; i++)
            {
                var p = invoiceNoMonthPrefixes[i].Trim();
                if (p.Length == 0) continue;
                orParts.Add($"(i.invoice_no LIKE @invP{i} OR i.invoice_no LIKE @invNC{i})");
                dyn.Add($"invP{i}", p + "-%");
                dyn.Add($"invNC{i}", "NC-" + p + "-%");
            }
            if (orParts.Count > 0)
                sql += " AND (" + string.Join(" OR ", orParts) + ")\n";
        }
        else if (dateFrom.HasValue || dateTo.HasValue)
        {
            if (dateFrom.HasValue)
            {
                sql += " AND i.date_iso >= @dateFrom\n";
                dyn.Add("dateFrom", dateFrom.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            }
            if (dateTo.HasValue)
            {
                sql += " AND i.date_iso <= @dateTo\n";
                dyn.Add("dateTo", dateTo.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            }
        }

        if (q.Length > 0)
        {
            // Inclure recipient : c’est souvent ce qui alimente « Facturé à » (mutuelles, libellés métier) alors que nom/prénom patient sont vides ou différents.
            sql += @" AND (
  i.invoice_no LIKE @q ESCAPE '\' OR
  i.mutuelle   LIKE @q ESCAPE '\' OR
  i.recipient  LIKE @q ESCAPE '\' OR
  p.nom        LIKE @q ESCAPE '\' OR
  p.prenom     LIKE @q ESCAPE '\' OR
  i.ref_doc    LIKE @q ESCAPE '\' OR
  i.user_comment LIKE @q ESCAPE '\'
)
";
            dyn.Add("q", EscapeLikePattern(q));
        }

        sql += "ORDER BY i.date_iso DESC, i.invoice_no DESC;";

        return cn.Query<Invoice>(sql, dyn).ToList();
    }

    /// <summary>Construit le motif LIKE avec échappement de % _ \ (ESCAPE '\').</summary>
    private static string EscapeLikePattern(string raw)
    {
        var s = (raw ?? "")
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
        return $"%{s}%";
    }

    private static int? ParseIntOrNull(string? s)
    {
        s = (s ?? "").Trim();
        if (s.Length == 0) return null;
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return v;
        return null;
    }

    /// <summary>Vérifie s'il existe des factures (patients ou mutuelles) pour le mois donné. Basé sur le numéro de facture (MM-YYYY), pas sur date_iso ni date de paiement.</summary>
    public bool HasAnyInvoicesForPeriod(string periodYYYYMM)
    {
        if (string.IsNullOrWhiteSpace(periodYYYYMM)) return false;
        periodYYYYMM = periodYYYYMM.Trim();
        if (periodYYYYMM.Length != 7) return false;
        // period = YYYY-MM → préfixe numéro = MM-YYYY (ex. 2026-03 → 03-2026)
        var parts = periodYYYYMM.Split('-');
        if (parts.Length != 2 || parts[0].Length != 4 || parts[1].Length != 2) return false;
        var prefix = parts[1] + "-" + parts[0];       // 03-2026 (patients, mutuelles 03-2026-xx)
        var prefixEtat = "ETAT-" + prefix;            // ETAT-03-2026 (états mutuelles)

        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");

        // Factures patients (03-2026-01, ...) et mutuelles (03-2026-xx ou ETAT-03-2026-MUTUELLE)
        return cn.ExecuteScalar<long>(
            "SELECT COUNT(1) FROM invoices WHERE kind IN ('patient','mutuelle') AND (invoice_no LIKE @pref OR invoice_no LIKE @prefEtat);",
            new { pref = prefix + "%", prefEtat = prefixEtat + "%" }) > 0;
    }

    /// <summary>Supprime les factures du mois donné (patients et mutuelles dont le numéro correspond au mois). Basé sur le numéro de facture (MM-YYYY), pas sur date_iso.</summary>
    public int DeleteInvoicesForPeriod(string periodYYYYMM)
    {
        if (string.IsNullOrWhiteSpace(periodYYYYMM)) return 0;
        periodYYYYMM = periodYYYYMM.Trim();
        if (periodYYYYMM.Length != 7) return 0;
        var parts = periodYYYYMM.Split('-');
        if (parts.Length != 2 || parts[0].Length != 4 || parts[1].Length != 2) return 0;
        var prefix = parts[1] + "-" + parts[0];
        var prefixEtat = "ETAT-" + prefix;

        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");

        using var tx = cn.BeginTransaction();

        var rootIds = cn.Query<long>(@"
SELECT id
FROM invoices
WHERE kind IN ('patient','mutuelle') AND (invoice_no LIKE @pref OR invoice_no LIKE @prefEtat);
", new { pref = prefix + "%", prefEtat = prefixEtat + "%" }, transaction: tx).ToList();

        if (rootIds.Count == 0)
        {
            tx.Commit();
            return 0;
        }

        var allIds = rootIds.Concat(
            cn.Query<long>(@"
SELECT id
FROM invoices
WHERE ref_invoice_id IN @ids;
", new { ids = rootIds }, transaction: tx)
        ).Distinct().ToList();

        cn.Execute("DELETE FROM payments WHERE invoice_id IN @ids;", new { ids = allIds }, transaction: tx);

        if (cn.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type='table' AND name='losses';", transaction: tx) > 0)
            cn.Execute("DELETE FROM losses WHERE invoice_id IN @ids;", new { ids = allIds }, transaction: tx);

        if (cn.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type='table' AND name='mutual_invoice_revisions';", transaction: tx) > 0)
            cn.Execute("DELETE FROM mutual_invoice_revisions WHERE invoice_id IN @ids OR new_invoice_id IN @ids;", new { ids = allIds }, transaction: tx);

        cn.Execute("DELETE FROM invoice_lines WHERE invoice_id IN @ids;", new { ids = allIds }, transaction: tx);
        var deleted = cn.Execute("DELETE FROM invoices WHERE id IN @ids;", new { ids = allIds }, transaction: tx);

        tx.Commit();
        return deleted;
    }

    // =====================================================================
    // AJOUTS pour FacturesViewModel (tes erreurs CS1061 + CS1503)
    // =====================================================================

    /// <summary>
    /// Liste des factures patient qui ont besoin d'être reliées (patient_id manquant) ou dont le PDF n'est pas référencé.
    /// </summary>
    public List<Invoice> GetPatientInvoicesNeedingLink()
    {
        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");

        // On considère "à relier" si:
        // - kind = 'patient'
        // - et (patient_id NULL/0) OU (ref_doc vide)
        return cn.Query<Invoice>(@"
SELECT
  i.id            AS Id,
  i.invoice_no    AS InvoiceNo,
  i.kind          AS Kind,
  i.patient_id    AS PatientId,
  i.mutuelle      AS Mutuelle,
  i.date_iso      AS DateIso,
  i.total_cents   AS TotalCents,
  i.paid_cents    AS PaidCents,
  i.status        AS Status,
  i.ref_invoice_id AS RefInvoiceId,
  i.reason        AS Reason,
  i.ref_doc       AS RefDoc
FROM invoices i
WHERE i.kind='patient'
  AND (
       i.patient_id IS NULL OR i.patient_id=0
       OR TRIM(COALESCE(i.ref_doc,''))=''
  )
ORDER BY i.date_iso DESC, i.invoice_no DESC;
").ToList();
    }

    /// <summary>
    /// Met à jour ref_doc en se basant sur invoice_no (string) — c'est ce que ton ViewModel appelle.
    /// </summary>
    public void UpdateRefDoc(string invoiceNo, string? refDoc)
    {
        invoiceNo = (invoiceNo ?? "").Trim();
        if (invoiceNo.Length == 0) return;

        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");

        cn.Execute(@"
UPDATE invoices
SET ref_doc = @refDoc
WHERE invoice_no = @no;
", new { no = invoiceNo, refDoc });
    }

    /// <summary>
    /// Variante si un jour tu veux update par Id.
    /// </summary>
    public void UpdateRefDoc(long invoiceId, string? refDoc)
    {
        if (invoiceId <= 0) return;

        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");

        cn.Execute(@"
UPDATE invoices
SET ref_doc = @refDoc
WHERE id = @id;
", new { id = invoiceId, refDoc });
    }

    /// <summary>
    /// Relie une facture (invoice_no) à un patient en essayant de retrouver patient_id via "recipient" (référent ou nom).
    /// Ton ViewModel appelle UpdateRecipient(invoiceNo, recipientString).
    /// </summary>
    public void UpdateRecipient(string invoiceNo, string recipient)
    {
        invoiceNo = (invoiceNo ?? "").Trim();
        recipient = (recipient ?? "").Trim();
        if (invoiceNo.Length == 0 || recipient.Length == 0) return;

        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");

        // 1) match exact sur referend
        var pid = cn.ExecuteScalar<long?>(@"
SELECT id
FROM patients
WHERE TRIM(COALESCE(referend,'')) <> ''
  AND lower(TRIM(referend)) = lower(TRIM(@r))
LIMIT 1;
", new { r = recipient });

        // 2) fallback: match "nom prenom" contient recipient (ou inverse)
        if (!pid.HasValue || pid.Value <= 0)
        {
            pid = cn.ExecuteScalar<long?>(@"
SELECT id
FROM patients
WHERE
  lower(TRIM(COALESCE(nom,'')) || ' ' || TRIM(COALESCE(prenom,''))) LIKE lower('%' || @r || '%')
   OR lower(@r) LIKE '%' || lower(TRIM(COALESCE(nom,'')) || ' ' || TRIM(COALESCE(prenom,''))) || '%'
LIMIT 1;
", new { r = recipient });
        }

        if (!pid.HasValue || pid.Value <= 0)
            return; // pas de match => on ne touche pas

        cn.Execute(@"
UPDATE invoices
SET patient_id = @pid
WHERE invoice_no = @no;
", new { pid = pid.Value, no = invoiceNo });
    }

    /// <summary>
    /// Variante explicite si tu veux directement pousser un patient_id.
    /// </summary>
    public void UpdateRecipient(string invoiceNo, long patientId)
    {
        invoiceNo = (invoiceNo ?? "").Trim();
        if (invoiceNo.Length == 0 || patientId <= 0) return;

        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");

        cn.Execute(@"
UPDATE invoices
SET patient_id = @pid
WHERE invoice_no = @no;
", new { pid = patientId, no = invoiceNo });
    }

    // =====================
    // Paiements / pertes / notes de crédit / corrections mutuelles
    // =====================

    public Invoice? GetById(long id)
    {
        if (id <= 0) return null;
        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");
        return cn.QueryFirstOrDefault<Invoice>(@"
SELECT
  id             AS Id,
  invoice_no     AS InvoiceNo,
  kind           AS Kind,
  patient_id     AS PatientId,
  mutuelle       AS Mutuelle,
  date_iso       AS DateIso,
  total_cents    AS TotalCents,
  paid_cents     AS PaidCents,
  status         AS Status,
  ref_invoice_id AS RefInvoiceId,
  reason         AS Reason,
  ref_doc        AS RefDoc,
  user_comment   AS UserComment,
  period         AS Period,
  recipient      AS Recipient
FROM invoices
WHERE id=@id;", new { id });
    }

    public void UpdateUserComment(long invoiceId, string? userComment)
    {
        if (invoiceId <= 0) return;
        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");
        cn.Execute("UPDATE invoices SET user_comment=@c WHERE id=@id;", new
        {
            id = invoiceId,
            c = string.IsNullOrWhiteSpace(userComment) ? null : userComment.Trim()
        });
    }

    /// <summary>Retourne l'adresse formatée du patient (rue, CP ville, pays) pour l'encadré FACTURÉ À, ou null.</summary>
    public string? GetPatientAddressLines(long? patientId)
    {
        if (patientId is null or <= 0) return null;
        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");
        var row = cn.QueryFirstOrDefault<(string? rue, string? numero, string? adresse, string? cp, string? ville, string? pays)>(@"
SELECT rue, numero, adresse, cp, ville, pays FROM patients WHERE id = @id;", new { id = patientId.Value });
        if (row == default) return null;
        var parts = new List<string>();
        var street = (row.rue ?? "").Trim();
        if (!string.IsNullOrEmpty(row.numero)) street = (street + " " + row.numero.Trim()).Trim();
        if (string.IsNullOrEmpty(street) && !string.IsNullOrWhiteSpace(row.adresse)) street = (row.adresse ?? "").Trim();
        if (!string.IsNullOrEmpty(street)) parts.Add(street);
        var cp = (row.cp ?? "").Trim();
        var city = (row.ville ?? "").Trim();
        if (!string.IsNullOrEmpty(cp) || !string.IsNullOrEmpty(city))
            parts.Add((cp + " " + city).Trim());
        if (!string.IsNullOrWhiteSpace(row.pays)) parts.Add((row.pays ?? "").Trim());
        return parts.Count == 0 ? null : string.Join("\n", parts);
    }

    public sealed class PaymentApplyResult
    {
        public int OverpaidCents { get; init; }
    }

    public sealed class PaymentHistoryRow
    {
        public long Id { get; init; }
        public string PaidDateIso { get; init; } = "";
        public int AmountCents { get; init; }
        public string Method { get; init; } = "";
        public string Reference { get; init; } = "";
    }

    public sealed class MutualRevisionDetails
    {
        public int OriginalTotalCents { get; init; }
        public string ModifiedAt { get; init; } = "";
        public string Reason { get; init; } = "";
        public string ReferenceDoc { get; init; } = "";
    }

    /// <summary>Texte du champ commentaires (<c>user_comment</c>) pour une facture mutuelle modifiée.</summary>
    public static string BuildMutualModifiedInvoiceUserComment(string? reason, string? referenceDoc)
    {
        var r = (reason ?? "").Trim();
        var d = (referenceDoc ?? "").Trim();
        if (r.Length == 0 && d.Length == 0) return "";
        if (d.Length == 0) return r;
        if (r.Length == 0) return "Réf. document mutuelle : " + d;
        return $"{r}\nRéf. document mutuelle : {d}";
    }

    /// <summary>Dernière révision + total d’origine, pour affichage détail / PDF (réf. mutuelle distincte du <c>ref_doc</c> PDF).</summary>
    public MutualRevisionDetails? GetLastMutualRevisionDetails(long originalInvoiceId)
    {
        if (originalInvoiceId <= 0) return null;
        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");
        var origTotal = cn.ExecuteScalar<int?>("SELECT total_cents FROM invoices WHERE id=@id;", new { id = originalInvoiceId });
        if (!origTotal.HasValue) return null;

        var row = cn.QueryFirstOrDefault<(string? ModifiedAt, string? Reason, string? ReferenceDoc)>(@"
SELECT changed_at AS ModifiedAt, reason AS Reason, reference_doc AS ReferenceDoc
FROM mutual_invoice_revisions
WHERE invoice_id=@id
ORDER BY revision_no DESC
LIMIT 1;
", new { id = originalInvoiceId });

        return new MutualRevisionDetails
        {
            OriginalTotalCents = origTotal.Value,
            ModifiedAt = (row.ModifiedAt ?? "").Trim(),
            Reason = (row.Reason ?? "").Trim(),
            ReferenceDoc = (row.ReferenceDoc ?? "").Trim()
        };
    }

    public PaymentApplyResult AddPayment(long invoiceId, string paidDateIso, int amountCents, string method, string reference)
    {
        if (invoiceId <= 0 || amountCents <= 0) return new PaymentApplyResult();

        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");

        var inv = cn.QueryFirstOrDefault<Invoice>(@"
SELECT
  id             AS Id,
  invoice_no     AS InvoiceNo,
  kind           AS Kind,
  patient_id     AS PatientId,
  mutuelle       AS Mutuelle,
  date_iso       AS DateIso,
  total_cents    AS TotalCents,
  paid_cents     AS PaidCents,
  status         AS Status,
  ref_invoice_id AS RefInvoiceId,
  reason         AS Reason,
  ref_doc        AS RefDoc,
  period         AS Period,
  recipient      AS Recipient
FROM invoices
WHERE id=@id;", new { id = invoiceId });
        if (inv is null) return new PaymentApplyResult();

        cn.Execute(@"
INSERT INTO payments(invoice_id, paid_date, amount_cents, method, reference)
VALUES (@invoice_id, @paid_date, @amount_cents, @method, @reference);
", new
        {
            invoice_id = invoiceId,
            paid_date = paidDateIso,
            amount_cents = amountCents,
            method = method ?? "",
            reference = reference ?? ""
        });

        var newPaid = inv.PaidCents + amountCents;
        if (string.Equals(inv.Kind, "credit_note", StringComparison.OrdinalIgnoreCase))
        {
            var ncMag = Math.Abs(inv.TotalCents);
            var paidToStore = newPaid;
            var over = Math.Max(0, newPaid - ncMag);
            var newStatus = paidToStore >= ncMag ? "paid" : (paidToStore > 0 ? "partial" : "unpaid");
            cn.Execute("UPDATE invoices SET paid_cents=@p, status=@s WHERE id=@id;", new { p = paidToStore, s = newStatus, id = invoiceId });
            return new PaymentApplyResult { OverpaidCents = over };
        }

        var overPatient = Math.Max(0, newPaid - inv.TotalCents);
        var paidToStorePatient = newPaid;
        var newStatusPatient = paidToStorePatient >= inv.TotalCents ? "paid" : "partial";
        cn.Execute("UPDATE invoices SET paid_cents=@p, status=@s WHERE id=@id;", new { p = paidToStorePatient, s = newStatusPatient, id = invoiceId });

        // Si la facture était en PERTE (losses existants) mais est maintenant payée, supprimer la perte.
        // Sinon, la ligne restera rouge (status=loss) / incohérente lors des rafraîchissements.
        if (paidToStorePatient >= inv.TotalCents)
        {
            try { cn.Execute("DELETE FROM losses WHERE invoice_id=@id;", new { id = invoiceId }); } catch { /* table absente sur vieux schémas */ }
            cn.Execute("UPDATE invoices SET status='paid' WHERE id=@id;", new { id = invoiceId });
        }

        return new PaymentApplyResult { OverpaidCents = overPatient };
    }

    /// <summary>Historique des paiements d'une facture (ordre chronologique).</summary>
    public List<PaymentHistoryRow> GetPaymentHistory(long invoiceId)
    {
        if (invoiceId <= 0) return new List<PaymentHistoryRow>();
        using var cn = Db.Open();
        return cn.Query<PaymentHistoryRow>(@"
SELECT id AS Id, paid_date AS PaidDateIso, amount_cents AS AmountCents, method AS Method, reference AS Reference
FROM payments
WHERE invoice_id = @id
ORDER BY paid_date, id;", new { id = invoiceId }).ToList();
    }

    public void ReplacePayments(long invoiceId, List<(string paidDateIso, int amountCents, string method, string reference)> payments)
    {
        if (invoiceId <= 0) return;
        payments ??= new List<(string, int, string, string)>();

        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");
        using var tx = cn.BeginTransaction();

        var inv = cn.QueryFirstOrDefault<Invoice>(@"
SELECT id AS Id, total_cents AS TotalCents, status AS Status, kind AS Kind
FROM invoices
WHERE id=@id;", new { id = invoiceId }, transaction: tx);
        if (inv is null) { tx.Rollback(); return; }

        cn.Execute("DELETE FROM payments WHERE invoice_id=@id;", new { id = invoiceId }, transaction: tx);

        foreach (var p in payments)
        {
            if (p.amountCents == 0) continue;
            var date = (p.paidDateIso ?? "").Trim();
            if (date.Length < 10) continue;
            date = date.Substring(0, 10);
            cn.Execute(@"
INSERT INTO payments(invoice_id, paid_date, amount_cents, method, reference)
VALUES (@invoice_id, @paid_date, @amount_cents, @method, @reference);",
                new
                {
                    invoice_id = invoiceId,
                    paid_date = date,
                    amount_cents = p.amountCents,
                    method = (p.method ?? "").Trim(),
                    reference = (p.reference ?? "").Trim()
                }, transaction: tx);
        }

        var paidSum = cn.ExecuteScalar<long>("SELECT COALESCE(SUM(amount_cents), 0) FROM payments WHERE invoice_id=@id;", new { id = invoiceId }, transaction: tx);
        var paidToStore = paidSum > int.MaxValue ? int.MaxValue : (int)paidSum;

        if (string.Equals(inv.Kind, "credit_note", StringComparison.OrdinalIgnoreCase))
        {
            var ncMag = Math.Abs(inv.TotalCents);
            if (paidToStore >= ncMag)
            {
                try { cn.Execute("DELETE FROM losses WHERE invoice_id=@id;", new { id = invoiceId }, transaction: tx); } catch { }
                cn.Execute("UPDATE invoices SET paid_cents=@p, status='paid' WHERE id=@id;", new { p = paidToStore, id = invoiceId }, transaction: tx);
            }
            else
            {
                long lossNetNc = 0;
                try
                {
                    lossNetNc = cn.ExecuteScalar<long?>("SELECT COALESCE(SUM(amount_cents), 0) FROM losses WHERE invoice_id=@id;", new { id = invoiceId }, transaction: tx) ?? 0;
                }
                catch { lossNetNc = 0; }

                var statusNc = lossNetNc != 0 ? "loss" : (paidToStore > 0 ? "partial" : "unpaid");
                cn.Execute("UPDATE invoices SET paid_cents=@p, status=@s WHERE id=@id;", new { p = paidToStore, s = statusNc, id = invoiceId }, transaction: tx);
            }

            tx.Commit();
            return;
        }

        // Si paiement total : supprimer la perte éventuelle et mettre paid
        if (paidToStore >= inv.TotalCents)
        {
            try { cn.Execute("DELETE FROM losses WHERE invoice_id=@id;", new { id = invoiceId }, transaction: tx); } catch { }
            cn.Execute("UPDATE invoices SET paid_cents=@p, status='paid' WHERE id=@id;", new { p = paidToStore, id = invoiceId }, transaction: tx);
        }
        else
        {
            // Perte nette non nulle → statut loss ; sinon partiel / impayé (évite loss avec somme des lignes = 0)
            long lossNet = 0;
            try
            {
                lossNet = cn.ExecuteScalar<long?>("SELECT COALESCE(SUM(amount_cents), 0) FROM losses WHERE invoice_id=@id;", new { id = invoiceId }, transaction: tx) ?? 0;
            }
            catch { lossNet = 0; }

            var status = lossNet != 0 ? "loss" : (paidToStore > 0 ? "partial" : "unpaid");
            cn.Execute("UPDATE invoices SET paid_cents=@p, status=@s WHERE id=@id;", new { p = paidToStore, s = status, id = invoiceId }, transaction: tx);
        }

        tx.Commit();
    }

    public void DeclareLoss(long invoiceId, string lossDateIso, int remainingCents, string reason)
    {
        if (invoiceId <= 0) return;
        if (remainingCents <= 0) return;

        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");

        cn.Execute(@"
INSERT INTO losses(invoice_id, loss_date, amount_cents, reason)
VALUES (@invoice_id, @loss_date, @amount_cents, @reason);
", new
        {
            invoice_id = invoiceId,
            loss_date = lossDateIso,
            amount_cents = -Math.Abs(remainingCents),
            reason = reason ?? ""
        });

        cn.Execute("UPDATE invoices SET status='loss' WHERE id=@id;", new { id = invoiceId });
    }

    public sealed class CreditNoteCandidate
    {
        public long InvoiceId { get; init; }
        public string Label { get; init; } = "";
    }

    public List<CreditNoteCandidate> ListCreditNoteCandidates()
    {
        using var cn = Db.Open();
        var rows = cn.Query<Invoice>(@"
SELECT * FROM invoices
WHERE kind IN ('patient','mutuelle')
  AND COALESCE(status,'') <> 'superseded'
ORDER BY date_iso DESC, invoice_no DESC
LIMIT 500;
").ToList();

        return rows.Select(i => new CreditNoteCandidate
        {
            InvoiceId = i.Id,
            Label = FormatCreditNoteCandidateLabel(i)
        }).ToList();
    }

    /// <summary>Aligné sur le solde affiché en grille (facture patient / mutuelle, même règle que <c>BalanceCents</c>).</summary>
    public int GetInvoiceBalanceDisplayCents(long invoiceId)
    {
        var inv = GetById(invoiceId);
        if (inv is null) return 0;
        if (string.Equals(inv.Kind, "credit_note", StringComparison.OrdinalIgnoreCase))
        {
            var lossMag = Math.Abs(GetLossTotalCents(invoiceId));
            return Math.Abs(inv.TotalCents) - inv.PaidCents - lossMag;
        }

        var lossMag2 = Math.Abs(GetLossTotalCents(invoiceId));
        var nc = GetCreditNoteTotalCents(invoiceId);
        var raw = inv.TotalCents - inv.PaidCents - lossMag2;
        if (raw < 0)
            return Math.Min(0, raw + nc);
        if (raw == 0)
            return 0;
        return raw - nc;
    }

    private string FormatCreditNoteCandidateLabel(Invoice i)
    {
        var no = (i.InvoiceNo ?? "").Trim();
        var who = (i.Recipient ?? "").Trim();
        var totalEuro = i.TotalCents / 100m;
        var bal = GetInvoiceBalanceDisplayCents(i.Id);
        var balEuro = bal / 100m;
        return $"{no} — {who} — total {totalEuro:0.00} €, solde {balEuro:0.00} €";
    }

    /// <summary>Somme des montants des notes de crédit ayant cette facture en référence (en centimes).</summary>
    public int GetCreditNoteTotalCents(long refInvoiceId)
    {
        if (refInvoiceId <= 0) return 0;
        using var cn = Db.Open();
        var sum = cn.ExecuteScalar<long?>(@"
SELECT COALESCE(SUM(ABS(total_cents)), 0)
FROM invoices
WHERE kind = 'credit_note' AND ref_invoice_id = @id;", new { id = refInvoiceId });
        return (int)(sum ?? 0);
    }

    /// <summary>
    /// Corrige le montant TTC enregistré d'une note de crédit (colonne liste / stats).
    /// En base, les NC ont <c>total_cents</c> négatif ; le PDF utilise <c>Math.Abs(total_cents)</c>.
    /// </summary>
    /// <param name="creditInvoiceId"><c>invoices.id</c> de la ligne <c>kind = 'credit_note'</c>.</param>
    /// <param name="amountCentsPositive">Montant crédité en centimes (ex. 2295 pour 22,95 €).</param>
    /// <param name="regeneratePdf">Si vrai, régénère le PDF depuis le nouveau montant (écrase le fichier).</param>
    public bool TryUpdateCreditNoteTotalCents(long creditInvoiceId, int amountCentsPositive, bool regeneratePdf = false)
    {
        if (creditInvoiceId <= 0 || amountCentsPositive <= 0) return false;

        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");
        var kind = cn.ExecuteScalar<string?>("SELECT kind FROM invoices WHERE id=@id;", new { id = creditInvoiceId });
        if (!string.Equals(kind, "credit_note", StringComparison.OrdinalIgnoreCase))
            return false;

        var stored = -Math.Abs(amountCentsPositive);
        var n = cn.Execute("UPDATE invoices SET total_cents=@t WHERE id=@id AND kind='credit_note';", new { t = stored, id = creditInvoiceId });
        if (n <= 0)
            return false;

        if (regeneratePdf)
            return TryGenerateCreditNotePdf(creditInvoiceId);

        return true;
    }

    /// <returns>Identifiant de la note de crédit créée, ou 0 si échec.</returns>
    public long CreateCreditNoteFromInvoice(long refInvoiceId, int amountCents, string dateIso)
    {
        if (refInvoiceId <= 0 || amountCents <= 0) return 0;

        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");

        var inv = cn.QueryFirstOrDefault<Invoice>("SELECT * FROM invoices WHERE id=@id;", new { id = refInvoiceId });
        if (inv is null) return 0;

        // Série propre aux NC : NC-MM-YYYY-NN (1ère NC du mois = …-01), indépendante des factures patient.
        var mmYyyy = WorkspacePaths.ToMMYYYY(ResolvePeriodYyyyMmForNumbering(inv));
        var nextSuffix = GetNextMonthlyCreditNoteSuffix(cn, mmYyyy);
        var ncNo = $"NC-{mmYyyy}-{nextSuffix:00}";

        var refNo = (inv.InvoiceNo ?? "").Trim();
        var userComment = refNo.Length == 0
            ? "NC relative à la facture (n° inconnu)"
            : $"NC relative à la facture {refNo}";

        cn.Execute(@"
INSERT INTO invoices(invoice_no, kind, patient_id, mutuelle, date_iso, total_cents, paid_cents, status, ref_invoice_id, reason, ref_doc, recipient, period, user_comment)
VALUES (@invoice_no, 'credit_note', @patient_id, @mutuelle, @date_iso, @total_cents, 0, 'unpaid', @ref_invoice_id, @reason, @ref_doc, @recipient, @period, @user_comment);
", new
        {
            invoice_no = ncNo,
            patient_id = inv.PatientId,
            mutuelle = inv.Mutuelle,
            date_iso = dateIso,
            total_cents = -Math.Abs(amountCents),
            ref_invoice_id = inv.Id,
            reason = "Note de crédit",
            ref_doc = "",
            recipient = inv.Recipient,
            period = inv.Period,
            user_comment = userComment
        });

        return cn.ExecuteScalar<long>("SELECT last_insert_rowid();");
    }

    /// <summary>Génère le PDF de la note de crédit et met à jour <c>ref_doc</c> (chemin relatif).</summary>
    public bool TryGenerateCreditNotePdf(long creditInvoiceId)
    {
        if (creditInvoiceId <= 0) return false;
        try
        {
            var credit = GetById(creditInvoiceId);
            if (credit is null || !string.Equals(credit.Kind, "credit_note", StringComparison.OrdinalIgnoreCase))
                return false;

            var refId = credit.RefInvoiceId ?? 0;
            var refInv = refId > 0 ? GetById(refId) : null;
            if (refInv is null) return false;

            var period = !string.IsNullOrWhiteSpace(refInv.Period)
                ? refInv.Period!
                : ((refInv.DateIso ?? "").Length >= 7 ? refInv.DateIso![..7] : "0000-00");

            var pdf = new InvoicePdfService();
            var path = pdf.BuildCreditNotePdfPath(credit.InvoiceNo, credit.Recipient, period, refInv.Kind ?? "patient");
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var lineTotal = Math.Abs(credit.TotalCents);
            var lines = new List<InvoiceLineRow>
            {
                new(
                    string.IsNullOrWhiteSpace(credit.DateIso)
                        ? DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                        : credit.DateIso.Trim(),
                    (credit.Reason ?? "Note de crédit").Trim(),
                    lineTotal)
            };

            var recipientAddress = GetPatientAddressLines(credit.PatientId);
            pdf.GenerateCreditNotePdf(credit, refInv, lines, path, recipientAddress);

            var root = WorkspacePaths.TryFindWorkspaceRoot();
            var rel = WorkspacePaths.MakeRelativeToRoot(root, path);
            UpdateRefDoc(credit.InvoiceNo, rel);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Crée une révision mutuelle (facture d’origine en <c>superseded</c>, copie <c>-MOD</c> en <c>modified</c>).</summary>
    /// <returns>Chemin absolu du PDF d’état modifié si généré, sinon null ; message d’erreur PDF éventuel.</returns>
    public (string? PdfAbsolutePath, string? PdfError) CreateMutualRevision(long invoiceId, int newTotalCents, string reason, string referenceDoc)
    {
        if (invoiceId <= 0 || newTotalCents <= 0)
            return (null, null);

        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");

        var inv = GetById(invoiceId);
        if (inv is null) return (null, null);
        if (!string.Equals(inv.Kind, "mutuelle", StringComparison.OrdinalIgnoreCase))
            return (null, null);

        var next = cn.ExecuteScalar<int>(@"
SELECT COALESCE(MAX(revision_no),0)+1
FROM mutual_invoice_revisions
WHERE invoice_id=@id;
", new { id = invoiceId });

        cn.Execute(@"
INSERT INTO mutual_invoice_revisions(invoice_id, revision_no, changed_at, new_total_cents, reason, reference_doc)
VALUES (@invoice_id, @revision_no, datetime('now'), @new_total_cents, @reason, @reference_doc);
", new
        {
            invoice_id = invoiceId,
            revision_no = next,
            new_total_cents = newTotalCents,
            reason = reason ?? "",
            reference_doc = referenceDoc ?? ""
        });

        cn.Execute("UPDATE invoices SET status='superseded' WHERE id=@id;", new { id = invoiceId });

        var newNo = BuildModifiedMutualInvoiceNo(inv.InvoiceNo ?? "");
        cn.Execute(@"
INSERT INTO invoices(invoice_no, kind, patient_id, mutuelle, date_iso, total_cents, paid_cents, status, ref_invoice_id, reason, ref_doc, recipient, period, user_comment)
VALUES (@invoice_no, 'mutuelle', NULL, @mutuelle, @date_iso, @total_cents, 0, 'modified', @ref_invoice_id, @reason, @ref_doc, @recipient, @period, @user_comment);
", new
        {
            invoice_no = newNo,
            mutuelle = inv.Mutuelle,
            date_iso = inv.DateIso,
            total_cents = newTotalCents,
            ref_invoice_id = invoiceId,
            reason = reason ?? "",
            ref_doc = referenceDoc ?? "",
            recipient = inv.Recipient,
            period = inv.Period,
            user_comment = BuildMutualModifiedInvoiceUserComment(reason, referenceDoc)
        });

        // PDF même sans lignes de récap (plus aucune séance AO pour la mutuelle / le mois) : le bloc « nouveau total AO » reste pertinent.
        try
        {
            var pdf = new InvoicePdfService();
            var rows = BuildMutualRecap(inv.Mutuelle ?? "", inv.Period ?? "");
            var meta = new MutualRecapMeta(newNo, reason ?? "", referenceDoc ?? "", DateTime.Today);
            var path = pdf.BuildMutualRecapModifPdfPath(inv.Mutuelle ?? "", inv.Period ?? "");
            pdf.GenerateMutualRecapPdf(
                inv.Mutuelle ?? "",
                inv.Period ?? "",
                rows,
                meta,
                path,
                isModification: true,
                modifiedTotalAoCents: newTotalCents,
                initialTotalAoCentsForModification: inv.TotalCents);

            var root = WorkspacePaths.TryFindWorkspaceRoot();
            var rel = WorkspacePaths.MakeRelativeToRoot(root, path);
            UpdateRefDoc(newNo, rel);

            if (!string.IsNullOrWhiteSpace(root) && !string.IsNullOrWhiteSpace(rel))
            {
                var abs = WorkspacePaths.ResolvePath(root, rel);
                if (File.Exists(abs))
                    return (abs, null);
            }

            return File.Exists(path) ? (path, null) : (null, "PDF introuvable après génération.");
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>Supprime une facture mutuelle <c>status=modified</c> (-MOD), retire la dernière ligne de <c>mutual_invoice_revisions</c> sur l’originale et réactive celle-ci s’il ne reste plus de copie modifiée.</summary>
    public bool TryDeleteModifiedMutualInvoice(long modifiedInvoiceId)
    {
        if (modifiedInvoiceId <= 0) return false;

        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");

        var inv = cn.QueryFirstOrDefault<Invoice>("SELECT * FROM invoices WHERE id=@id;", new { id = modifiedInvoiceId });
        if (inv is null) return false;
        if (!string.Equals(inv.Kind, "mutuelle", StringComparison.OrdinalIgnoreCase)) return false;
        var noNorm = NormalizeMutualInvoiceNoForModCheck(inv.InvoiceNo);
        var isModifiedRow = string.Equals(inv.Status, "modified", StringComparison.OrdinalIgnoreCase)
                            || noNorm.EndsWith("-MOD", StringComparison.OrdinalIgnoreCase);
        if (!isModifiedRow) return false;

        using var tx = cn.BeginTransaction();
        try
        {
            var effectiveOrigId = ResolveEffectiveOriginalMutualInvoiceId(inv, noNorm, modifiedInvoiceId, cn, tx);

            // Supprimer la ligne modifiée même si ref_invoice_id est NULL (origine supprimée / données legacy) ou si le tiret du n° n’est pas ASCII.
            var deleted = cn.Execute(
                @"DELETE FROM invoices
WHERE id=@id AND kind='mutuelle'
  AND (
        lower(trim(COALESCE(status,'')))='modified'
     OR replace(replace(replace(trim(COALESCE(invoice_no,'')), char(8211), '-'), char(8213), '-'), char(8209), '-') LIKE '%-MOD'
  );",
                new { id = modifiedInvoiceId }, transaction: tx);
            if (deleted == 0)
            {
                tx.Rollback();
                return false;
            }

            if (effectiveOrigId > 0)
            {
                var origExists = cn.ExecuteScalar<long>(
                    "SELECT COUNT(1) FROM invoices WHERE id=@id;",
                    new { id = effectiveOrigId }, transaction: tx) > 0;

                if (origExists)
                {
                    var maxRev = cn.ExecuteScalar<int?>(
                        "SELECT MAX(revision_no) FROM mutual_invoice_revisions WHERE invoice_id=@id;",
                        new { id = effectiveOrigId }, transaction: tx);
                    if (maxRev is > 0)
                    {
                        cn.Execute(
                            "DELETE FROM mutual_invoice_revisions WHERE invoice_id=@id AND revision_no=@r;",
                            new { id = effectiveOrigId, r = maxRev.Value }, transaction: tx);
                    }

                    var linkedMods = (int)(cn.ExecuteScalar<long>(
                        "SELECT COUNT(1) FROM invoices WHERE ref_invoice_id=@oid AND kind='mutuelle';",
                        new { oid = effectiveOrigId }, transaction: tx));

                    var orphanMods = CountOrphanModifiedMutualRowsSameBase(
                        cn, tx, inv.Mutuelle, inv.Recipient, StripMutualModSuffixFromNormalized(noNorm));

                    if (linkedMods == 0 && orphanMods == 0)
                    {
                        var paid = cn.ExecuteScalar<long?>("SELECT paid_cents FROM invoices WHERE id=@id;", new { id = effectiveOrigId }, transaction: tx) ?? 0;
                        var total = cn.ExecuteScalar<long?>("SELECT total_cents FROM invoices WHERE id=@id;", new { id = effectiveOrigId }, transaction: tx) ?? 0;
                        var st = paid >= total ? "paid" : (paid > 0 ? "partial" : "unpaid");
                        cn.Execute("UPDATE invoices SET status=@s WHERE id=@id;", new { s = st, id = effectiveOrigId }, transaction: tx);
                    }
                }
            }

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            return false;
        }

        // Ne jamais faire échouer la suppression en base si le PDF est absent, le chemin est invalide ou le fichier est verrouillé.
        try
        {
            TryDeleteMutualModificationPdfsOnDisk(inv);
        }
        catch
        {
            /* chemins ref_doc corrompus, caractères invalides, etc. */
        }

        return true;
    }

    /// <summary>Efface du disque le(s) PDF d'état modifié (ref_doc, nom actuel avec n° -MOD, ancien suffixe <c>-MOD.pdf</c>).</summary>
    private static void TryDeleteMutualModificationPdfsOnDisk(Invoice inv)
    {
        try
        {
            var rel = (inv.RefDoc ?? "").Trim();

            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void addCandidate(string? path)
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                try
                {
                    var full = Path.GetFullPath(path.Trim());
                    if (File.Exists(full))
                        candidates.Add(full);
                }
                catch
                {
                    /* chemin invalide */
                }
            }

            if (rel.Length > 0)
            {
                try
                {
                    if (Path.IsPathRooted(rel))
                        addCandidate(rel);
                    else
                    {
                        var root = WorkspacePaths.TryFindWorkspaceRoot();
                        if (!string.IsNullOrWhiteSpace(root))
                            addCandidate(WorkspacePaths.ResolvePath(root, rel));
                    }
                }
                catch
                {
                    /* ref_doc illisible (ex. ancienne valeur non chemin) */
                }
            }

            var pdf = new InvoicePdfService();
            foreach (var abs in pdf.GetMutualModificationPdfPathsForDeletion(
                         inv.Mutuelle, inv.Recipient, inv.Period, inv.DateIso, inv.InvoiceNo))
                candidates.Add(abs);

            foreach (var p in candidates)
            {
                try
                {
                    File.Delete(p);
                }
                catch
                {
                    /* fichier absent ou verrouillé */
                }
            }
        }
        catch
        {
            /* aucune exception ne doit remonter : la ligne DB est déjà supprimée */
        }
    }

    /// <summary>Numéro de la facture mutuelle modifiée : numéro original + suffixe "-MOD".</summary>
    private static string BuildModifiedMutualInvoiceNo(string originalInvoiceNo)
        => $"{originalInvoiceNo.Trim()}-MOD";

    /// <summary>Normalise tirets typographiques pour reconnaître le suffixe <c>-MOD</c>.</summary>
    private static string NormalizeMutualInvoiceNoForModCheck(string? invoiceNo)
    {
        var s = (invoiceNo ?? "").Trim();
        if (s.Length == 0) return "";
        return s
            .Replace('\u2011', '-') // non-breaking hyphen
            .Replace('\u2012', '-') // figure dash
            .Replace('\u2013', '-') // en dash
            .Replace('\u2014', '-') // em dash
            .Replace('\u2010', '-') // hyphen
            .Replace('\u2212', '-'); // minus sign
    }

    /// <summary>Retire le suffixe <c>-MOD</c> (insensible à la casse) sur un numéro déjà normalisé.</summary>
    private static string StripMutualModSuffixFromNormalized(string normalizedNo)
    {
        if (normalizedNo.Length < 4) return normalizedNo;
        if (normalizedNo.EndsWith("-MOD", StringComparison.OrdinalIgnoreCase))
            return normalizedNo[..^4];
        return normalizedNo;
    }

    /// <summary>Compare mutuelle / destinataire entre deux lignes (champs parfois permutés en base).</summary>
    private static bool MutualIdentityCrossMatch(string? aMutuelle, string? aRecipient, string? bMutuelle, string? bRecipient)
    {
        static string Z(string? s) => (s ?? "").Trim();
        var am = Z(aMutuelle);
        var ar = Z(aRecipient);
        var bm = Z(bMutuelle);
        var br = Z(bRecipient);
        static bool Eq(string x, string y) =>
            x.Length > 0 && y.Length > 0 && string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
        return Eq(am, bm) || Eq(am, br) || Eq(ar, bm) || Eq(ar, br);
    }

    /// <summary>
    /// Résout l’identifiant de la facture mutuelle d’origine pour une ligne <c>-MOD</c> :
    /// <c>ref_invoice_id</c> si valide, sinon recherche d’une ligne <c>superseded</c> avec le même n° de base et la même mutuelle (données legacy sans FK).
    /// </summary>
    private static long ResolveEffectiveOriginalMutualInvoiceId(
        Invoice modInv, string normalizedModNo, long modifiedInvoiceId, IDbConnection cn, IDbTransaction tx)
    {
        var origId = modInv.RefInvoiceId ?? 0;
        if (origId > 0)
        {
            var n = cn.ExecuteScalar<long>("SELECT COUNT(1) FROM invoices WHERE id=@id;", new { id = origId }, transaction: tx);
            if (n > 0) return origId;
        }

        var baseNo = StripMutualModSuffixFromNormalized(normalizedModNo);
        if (string.IsNullOrEmpty(baseNo)) return 0;

        var mut = modInv.Mutuelle;
        var recip = modInv.Recipient;
        var period = (modInv.Period ?? "").Trim();

        var rows = cn.Query<(long Id, string? InvoiceNo, string? Period, string? Mutuelle, string? Recipient)>(@"
SELECT id, invoice_no, period, mutuelle, recipient FROM invoices
WHERE kind='mutuelle'
  AND lower(trim(COALESCE(status,'')))='superseded'
  AND id <> @modId;",
            new { modId = modifiedInvoiceId }, transaction: tx).ToList();

        var matches = rows
            .Where(r => string.Equals(NormalizeMutualInvoiceNoForModCheck(r.InvoiceNo), baseNo, StringComparison.OrdinalIgnoreCase))
            .Where(r => MutualIdentityCrossMatch(r.Mutuelle, r.Recipient, mut, recip))
            .ToList();

        if (matches.Count == 0) return 0;
        if (matches.Count == 1) return matches[0].Id;

        if (period.Length > 0)
        {
            var narrowed = matches
                .Where(r => string.Equals((r.Period ?? "").Trim(), period, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (narrowed.Count == 1) return narrowed[0].Id;
        }

        return 0;
    }

    /// <summary>
    /// Compte les factures mutuelles modifiées encore présentes sans <c>ref_invoice_id</c> (legacy),
    /// avec le même n° de base et la même mutuelle que la ligne <c>-MOD</c> supprimée.
    /// </summary>
    private static int CountOrphanModifiedMutualRowsSameBase(
        IDbConnection cn, IDbTransaction tx, string? mutuelle, string? recipient, string baseNoNormalized)
    {
        if (string.IsNullOrEmpty(baseNoNormalized)) return 0;

        var rows = cn.Query<(long Id, string? InvoiceNo, string? Mutuelle, string? Recipient)>(@"
SELECT id, invoice_no, mutuelle, recipient FROM invoices
WHERE kind='mutuelle'
  AND COALESCE(ref_invoice_id,0) = 0
  AND (
        lower(trim(COALESCE(status,'')))='modified'
     OR replace(replace(replace(trim(COALESCE(invoice_no,'')), char(8211), '-'), char(8213), '-'), char(8209), '-') LIKE '%-MOD'
  );", transaction: tx).ToList();

        var c = 0;
        foreach (var row in rows)
        {
            if (!MutualIdentityCrossMatch(row.Mutuelle, row.Recipient, mutuelle, recipient)) continue;
            var n = NormalizeMutualInvoiceNoForModCheck(row.InvoiceNo);
            if (!n.EndsWith("-MOD", StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(StripMutualModSuffixFromNormalized(n), baseNoNormalized, StringComparison.OrdinalIgnoreCase))
                continue;
            c++;
        }

        return c;
    }

    /// <summary>
    /// Réactive les factures mutuelles encore en <c>superseded</c> alors qu’aucune copie <c>-MOD</c> ne subsiste
    /// (suppression hors application, base importée, etc.). À appeler avant l’affichage de la liste des factures.
    /// </summary>
    public int RepairStrandedSupersededMutualInvoices()
    {
        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");

        var ids = cn.Query<long>(@"
SELECT id FROM invoices
WHERE kind='mutuelle' AND lower(trim(COALESCE(status,'')))='superseded';").ToList();
        if (ids.Count == 0) return 0;

        var repaired = 0;
        foreach (var oid in ids)
        {
            using var tx = cn.BeginTransaction();
            try
            {
                var orig = cn.QueryFirstOrDefault<Invoice>("SELECT * FROM invoices WHERE id=@id;", new { id = oid }, transaction: tx);
                if (orig is null)
                {
                    tx.Rollback();
                    continue;
                }

                var linked = (int)(cn.ExecuteScalar<long>(
                    "SELECT COUNT(1) FROM invoices WHERE kind='mutuelle' AND ref_invoice_id=@id;",
                    new { id = oid }, transaction: tx));
                if (linked > 0)
                {
                    tx.Rollback();
                    continue;
                }

                var noNorm = NormalizeMutualInvoiceNoForModCheck(orig.InvoiceNo);
                var baseNo = StripMutualModSuffixFromNormalized(noNorm);
                if (CountOrphanModifiedMutualRowsSameBase(cn, tx, orig.Mutuelle, orig.Recipient, baseNo) > 0)
                {
                    tx.Rollback();
                    continue;
                }

                cn.Execute("DELETE FROM mutual_invoice_revisions WHERE invoice_id=@id;", new { id = oid }, transaction: tx);

                var paid = cn.ExecuteScalar<long?>("SELECT paid_cents FROM invoices WHERE id=@id;", new { id = oid }, transaction: tx) ?? 0;
                var total = cn.ExecuteScalar<long?>("SELECT total_cents FROM invoices WHERE id=@id;", new { id = oid }, transaction: tx) ?? 0;
                var st = paid >= total ? "paid" : (paid > 0 ? "partial" : "unpaid");
                cn.Execute("UPDATE invoices SET status=@s WHERE id=@id;", new { s = st, id = oid }, transaction: tx);

                tx.Commit();
                repaired++;
            }
            catch
            {
                tx.Rollback();
            }
        }

        return repaired;
    }

    /// <summary>
    /// Remet un statut cohérent (payé / partiel / impayé) sur les factures encore en <c>loss</c> sans perte nette enregistrée.
    /// </summary>
    public int RepairStaleLossInvoiceStatus()
    {
        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");
        if (cn.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type='table' AND name='losses';") == 0)
            return 0;

        return cn.Execute(@"
UPDATE invoices
SET status = CASE
  WHEN lower(trim(COALESCE(kind,''))) = 'credit_note' AND paid_cents >= ABS(total_cents) THEN 'paid'
  WHEN lower(trim(COALESCE(kind,''))) = 'credit_note' AND paid_cents > 0 THEN 'partial'
  WHEN lower(trim(COALESCE(kind,''))) = 'credit_note' THEN 'unpaid'
  WHEN paid_cents >= total_cents THEN 'paid'
  WHEN paid_cents > 0 THEN 'partial'
  ELSE 'unpaid'
END
WHERE lower(trim(COALESCE(status,''))) = 'loss'
  AND COALESCE((SELECT SUM(l.amount_cents) FROM losses l WHERE l.invoice_id = invoices.id), 0) = 0;
");
    }

    /// <summary>Résultat de <see cref="PurgeNonDemoSessionsInvoicesMutualsAndJournalierPdfs"/>.</summary>
    public sealed class PurgeNonDemoBillingResult
    {
        public int SeancesDeleted { get; init; }
        public int InvoicesDeleted { get; init; }
        public int JournalierPdfsDeleted { get; init; }
    }

    /// <summary>
    /// Patient « démo » : nom, prénom ou référend contenant <c>-Démo</c> ou <c>-Demo</c> (insensible à la casse).
    /// Supprime les séances hors démo, les factures patients et NC liées, toutes les factures mutuelles, puis les PDF journaliers générés.
    /// </summary>
    public PurgeNonDemoBillingResult PurgeNonDemoSessionsInvoicesMutualsAndJournalierPdfs()
    {
        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");

        var nonDemoPatientIds = cn.Query<long>(@"
SELECT id FROM patients
WHERE NOT (
  instr(lower(trim(COALESCE(nom,'')||' '||COALESCE(prenom,'')||' '||COALESCE(referend,''))), '-démo') > 0
  OR instr(lower(trim(COALESCE(nom,'')||' '||COALESCE(prenom,'')||' '||COALESCE(referend,''))), '-demo') > 0
);").ToList();

        var sentinel = new List<long> { -1L };
        var patientIdParam = nonDemoPatientIds.Count > 0 ? nonDemoPatientIds : sentinel;

        var patientInvoiceIds = cn.Query<long>(@"
SELECT id FROM invoices WHERE kind='patient' AND patient_id IN @ids;", new { ids = patientIdParam }).ToList();

        var pinvParam = patientInvoiceIds.Count > 0 ? patientInvoiceIds : sentinel;
        var creditNoteIds = cn.Query<long>(@"
SELECT id FROM invoices
WHERE kind='credit_note'
  AND (patient_id IN @pids OR ref_invoice_id IN @pinv);",
            new { pids = patientIdParam, pinv = pinvParam }).ToList();

        var mutualInvoiceIds = cn.Query<long>("SELECT id FROM invoices WHERE kind='mutuelle';").ToList();

        var allInvoiceIds = patientInvoiceIds
            .Concat(creditNoteIds)
            .Concat(mutualInvoiceIds)
            .Distinct()
            .ToList();

        var seancesDeleted = 0;
        var invoicesDeleted = 0;

        using (var tx = cn.BeginTransaction())
        {
            if (nonDemoPatientIds.Count > 0)
                seancesDeleted = cn.Execute("DELETE FROM seances WHERE patient_id IN @ids;", new { ids = nonDemoPatientIds }, transaction: tx);

            if (allInvoiceIds.Count > 0)
            {
                cn.Execute("DELETE FROM payments WHERE invoice_id IN @ids;", new { ids = allInvoiceIds }, transaction: tx);

                if (cn.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type='table' AND name='losses';", transaction: tx) > 0)
                    cn.Execute("DELETE FROM losses WHERE invoice_id IN @ids;", new { ids = allInvoiceIds }, transaction: tx);

                if (cn.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type='table' AND name='mutual_invoice_revisions';", transaction: tx) > 0)
                    cn.Execute("DELETE FROM mutual_invoice_revisions WHERE invoice_id IN @ids;", new { ids = allInvoiceIds }, transaction: tx);

                cn.Execute("DELETE FROM invoice_lines WHERE invoice_id IN @ids;", new { ids = allInvoiceIds }, transaction: tx);
                invoicesDeleted = cn.Execute("DELETE FROM invoices WHERE id IN @ids;", new { ids = allInvoiceIds }, transaction: tx);
            }

            tx.Commit();
        }

        var journalierDeleted = 0;
        try
        {
            var root = WorkspacePaths.TryFindWorkspaceRoot();
            if (!string.IsNullOrWhiteSpace(root))
            {
                var dir = Path.Combine(root, "JOURNALIERS PDF");
                if (Directory.Exists(dir))
                {
                    foreach (var f in Directory.EnumerateFiles(dir, "ENCAISSEMENTS_*.pdf", SearchOption.TopDirectoryOnly))
                    {
                        try
                        {
                            File.Delete(f);
                            journalierDeleted++;
                        }
                        catch
                        {
                            /* fichier verrouillé */
                        }
                    }
                }
            }
        }
        catch
        {
            /* workspace inaccessible */
        }

        return new PurgeNonDemoBillingResult
        {
            SeancesDeleted = seancesDeleted,
            InvoicesDeleted = invoicesDeleted,
            JournalierPdfsDeleted = journalierDeleted
        };
    }

    /// <summary>Somme nette des pertes (en centimes) pour une facture. Les annulations (montant négatif) réduisent le total.</summary>
    public int GetLossTotalCents(long invoiceId)
    {
        if (invoiceId <= 0) return 0;
        using var cn = Db.Open();
        var sum = cn.ExecuteScalar<long?>("SELECT COALESCE(SUM(amount_cents), 0) FROM losses WHERE invoice_id=@id;", new { id = invoiceId });
        return (int)(sum ?? 0);
    }

    public void ClearLoss(long invoiceId)
    {
        if (invoiceId <= 0) return;

        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");
        using var tx = cn.BeginTransaction();

        // Supprimer toutes les pertes liées à la facture
        try { cn.Execute("DELETE FROM losses WHERE invoice_id=@id;", new { id = invoiceId }, transaction: tx); } catch { }

        // Recalculer paid_cents depuis les paiements
        var total = cn.ExecuteScalar<long?>("SELECT total_cents FROM invoices WHERE id=@id;", new { id = invoiceId }, transaction: tx) ?? 0;
        var paidSum = cn.ExecuteScalar<long?>("SELECT COALESCE(SUM(amount_cents),0) FROM payments WHERE invoice_id=@id;", new { id = invoiceId }, transaction: tx) ?? 0;
        var paidToStore = paidSum > int.MaxValue ? int.MaxValue : (int)paidSum;

        var status = paidToStore >= total ? "paid" : (paidToStore > 0 ? "partial" : "unpaid");
        cn.Execute("UPDATE invoices SET paid_cents=@p, status=@s WHERE id=@id;", new { p = paidToStore, s = status, id = invoiceId }, transaction: tx);

        tx.Commit();
    }

    /// <summary>Date de la dernière révision pour une facture originale (mutuelle remplacée). Utilisé pour afficher les détails d'une facture modifiée.</summary>
    public string? GetLastRevisionChangedAt(long originalInvoiceId)
    {
        if (originalInvoiceId <= 0) return null;
        using var cn = Db.Open();
        return cn.ExecuteScalar<string?>(@"
SELECT changed_at FROM mutual_invoice_revisions
WHERE invoice_id=@id ORDER BY revision_no DESC LIMIT 1;", new { id = originalInvoiceId });
    }

    public List<InvoiceLine> GetLines(long invoiceId)
    {
        if (invoiceId <= 0) return new();
        using var cn = Db.Open();
        return cn.Query<InvoiceLine>(@"
SELECT id AS Id, invoice_id AS InvoiceId, label AS Label, qty AS Qty,
       unit_price_cents AS UnitPriceCents, total_cents AS TotalCents,
       patient_part_cents AS PatientPartCents, mutuelle_part_cents AS MutuellePartCents,
       date_iso AS DateIso, created_at AS CreatedAt
FROM invoice_lines WHERE invoice_id=@id ORDER BY date_iso, id;", new { id = invoiceId }).ToList();
    }

    

    // Construit les lignes "patient" depuis les séances du mois (fallback si invoice_lines vides)
    public List<SeanceRow> GetSeancesForPatientInvoice(long patientId, string period)
    {
        if (patientId <= 0 || string.IsNullOrWhiteSpace(period)) return new();
        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");
        var from = period.Trim() + "-01";
        var to = NextMonth(period.Trim()) + "-01";
        return cn.Query<SeanceRow>(@"
SELECT s.id AS Id, s.patient_id AS PatientId, s.date_iso AS DateIso, s.part_patient AS PartPatient, s.part_mutuelle AS PartMutuelle, s.commentaire AS Commentaire,
       s.is_cash AS IsCash,
       p.nom AS PatientNom, p.prenom AS PatientPrenom, COALESCE(p.referend,'') AS Referend,
       t.libelle AS TarifLibelle
FROM seances s
JOIN patients p ON p.id = s.patient_id
JOIN tarifs t ON t.id = s.tarif_id
WHERE s.patient_id=@pid AND s.date_iso >= @from AND s.date_iso < @to
ORDER BY s.date_iso, s.id;
", new { pid = patientId, from, to }).ToList();
    }

    public List<MutualRow> BuildMutualRecap(string mutualName, string period)
    {
        mutualName = (mutualName ?? "").Trim();
        period = (period ?? "").Trim();
        if (string.IsNullOrWhiteSpace(mutualName) || string.IsNullOrWhiteSpace(period)) return new();

        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");
        var from = period + "-01";
        var to = NextMonth(period) + "-01";

        // AO = part_mutuelle ; Ticket = part_patient. On n'inclut que les patients avec une part mutuelle > 0 (sinon rien à facturer à la mutuelle).
        var rows = cn.Query(@"
SELECT 
   (p.prenom || ' ' || p.nom) AS PatientName,
   COALESCE(p.niss,'') AS Niss,
   CASE
    WHEN TRIM(UPPER(COALESCE(p.statut,''))) = 'BIM' THEN 1
    WHEN INSTR(UPPER(COALESCE(p.statut,'')), 'NON') > 0 AND INSTR(UPPER(COALESCE(p.statut,'')), 'BIM') > 0 THEN 0
    WHEN INSTR(UPPER(COALESCE(p.statut,'')), 'BIM') > 0 THEN 1
    ELSE 0
  END AS IsBim,
   SUM(s.part_mutuelle) AS AoCents,
   SUM(s.part_patient) AS TicketCents
FROM seances s
JOIN patients p ON p.id = s.patient_id
WHERE p.mutuelle = @mutu
  AND s.date_iso >= @from AND s.date_iso < @to
GROUP BY p.id
HAVING SUM(s.part_mutuelle) > 0
ORDER BY p.nom, p.prenom;
", new { mutu = mutualName, from, to }).ToList();

        return rows.Select(r => new MutualRow
        {
            PatientName = (string)r.PatientName,
            Niss = (string)r.Niss,
            IsBim = (long)r.IsBim,
            AoCents = (int)(long)r.AoCents,
            TicketCents = (int)(long)r.TicketCents
        }).ToList();
    }

    private static string NextMonth(string period)
    {
        if (DateTime.TryParseExact(period + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d.AddMonths(1).ToString("yyyy-MM");
        return period;
    }

    /// <summary>Période YYYY-MM pour numérotation (priorité <c>period</c>, sinon début de <c>date_iso</c>).</summary>
    private static string ResolvePeriodYyyyMmForNumbering(Invoice inv)
    {
        var p = (inv.Period ?? "").Trim();
        if (p.Length >= 7 && p[4] == '-')
            return p[..7];
        var d = (inv.DateIso ?? "").Trim();
        if (d.Length >= 7 && d[4] == '-')
            return d[..7];
        return DateTime.UtcNow.ToString("yyyy-MM", CultureInfo.InvariantCulture);
    }

    /// <summary>Prochain suffixe <c>NN</c> pour factures patient <c>MM-YYYY-NN</c> uniquement.</summary>
    private static int GetNextMonthlyPatientInvoiceSuffix(IDbConnection cn, string mmYyyyPrefix)
    {
        mmYyyyPrefix = (mmYyyyPrefix ?? "").Trim();
        if (mmYyyyPrefix.Length < 7)
            return 1;
        var pref = $"{mmYyyyPrefix}-";
        return cn.ExecuteScalar<int>(@"
SELECT COALESCE(MAX(CAST(substr(invoice_no, LENGTH(@pref) + 1) AS INTEGER)), 0) + 1
FROM invoices
WHERE kind = 'patient'
  AND invoice_no LIKE (@pref || '%')
  AND substr(invoice_no, LENGTH(@pref) + 1) GLOB '[0-9]*';", new { pref });
    }

    /// <summary>Prochain suffixe pour notes de crédit <c>NC-MM-YYYY-NN</c> (série mensuelle indépendante des factures).</summary>
    private static int GetNextMonthlyCreditNoteSuffix(IDbConnection cn, string mmYyyyPrefix)
    {
        mmYyyyPrefix = (mmYyyyPrefix ?? "").Trim();
        if (mmYyyyPrefix.Length < 7)
            return 1;
        var pref = $"NC-{mmYyyyPrefix}-";
        return cn.ExecuteScalar<int>(@"
SELECT COALESCE(MAX(CAST(substr(invoice_no, LENGTH(@pref) + 1) AS INTEGER)), 0) + 1
FROM invoices
WHERE kind = 'credit_note'
  AND invoice_no LIKE (@pref || '%')
  AND substr(invoice_no, LENGTH(@pref) + 1) GLOB '[0-9]*';", new { pref });
    }

public int CountExistingForPeriod(string kind, string periodYYYYMM)
{
    kind = (kind ?? "").Trim();
    periodYYYYMM = (periodYYYYMM ?? "").Trim();
    if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(periodYYYYMM)) return 0;
    using var cn = Db.Open();
    cn.Execute("PRAGMA foreign_keys = ON;");
    return cn.ExecuteScalar<int>("SELECT COUNT(1) FROM invoices WHERE kind=@k AND period=@p;", new { k = kind, p = periodYYYYMM });
}

public string? GetLastGeneratedPeriod(string kind)
{
    kind = (kind ?? "").Trim().ToLowerInvariant();
    if (kind.Length == 0) return null;

    using var cn = Db.Open();
    cn.Execute("PRAGMA foreign_keys = ON;");

    // Ne garder que les périodes strictement valides YYYY-MM (évite les valeurs legacy type "25-sept").
    // Exemple valide: 2026-03 ; mois 01..12 obligatoire.
    var p = cn.ExecuteScalar<string?>(@"
SELECT MAX(period)
FROM invoices
WHERE kind = @k
  AND period IS NOT NULL
  AND length(period) = 7
  AND substr(period, 5, 1) = '-'
  AND substr(period, 1, 4) GLOB '[0-9][0-9][0-9][0-9]'
  AND substr(period, 6, 2) GLOB '[0-9][0-9]'
  AND substr(period, 6, 2) BETWEEN '01' AND '12';",
        new { k = kind });

    return string.IsNullOrWhiteSpace(p) ? null : p.Trim();
}




    // =====================================================================
    // Génération mensuelle (Patients / Mutuelles)
    // =====================================================================

    /// <summary>
    /// Par groupe (référend ou patient), sépare les séances payées en espèces au cabinet des autres : une facture acquittée par bloc cash,
    /// une facture à payer pour le reste. Ainsi un patient en cash seul dans un journalier obtient bien son acquittée à la mensuelle.
    /// </summary>
    /// <param name="invoiceDateIso">Date à porter sur les factures (YYYY-MM-DD). Souvent le 1er du mois suivant la période.</param>
    public (int created, string folder, List<string> pdfs) GenerateMonthlyPatientInvoices(string periodYYYYMM, string invoiceDateIso, bool deleteExisting, InvoicePdfService pdf)
    {
        if (string.IsNullOrWhiteSpace(periodYYYYMM)) return (0, WorkspacePaths.FACTURES_MENSUELLES_PATIENTS_ROOT(), new());
        periodYYYYMM = periodYYYYMM.Trim();
        if (string.IsNullOrWhiteSpace(invoiceDateIso)) invoiceDateIso = periodYYYYMM + "-01";

        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");

        if (deleteExisting)
            cn.Execute("DELETE FROM invoices WHERE kind='patient' AND period=@p;", new { p = periodYYYYMM });

        // Charger toutes les séances du mois (avec patient + référend)
        var from = periodYYYYMM + "-01";
        var to = NextMonth(periodYYYYMM) + "-01";
        var allSeances = cn.Query<SeanceRow>(@"
SELECT s.id AS Id, s.patient_id AS PatientId, s.date_iso AS DateIso, s.part_patient AS PartPatient, s.part_mutuelle AS PartMutuelle, s.commentaire AS Commentaire,
       s.is_cash AS IsCash,
       p.nom AS PatientNom, p.prenom AS PatientPrenom, COALESCE(p.referend,'') AS Referend,
       t.libelle AS TarifLibelle
FROM seances s
JOIN patients p ON p.id = s.patient_id
JOIN tarifs t ON t.id = s.tarif_id
WHERE s.date_iso >= @from AND s.date_iso < @to
ORDER BY s.date_iso, s.id;
", new { from, to }).ToList();

        if (allSeances.Count == 0)
            return (0, WorkspacePaths.FACTURES_MENSUELLES_PATIENTS_ROOT(), new());

        // Grouper par destinataire :
        // - si référend non vide → une facture par référend (tous les patients liés)
        // - sinon → une facture par patient
        var groups = allSeances.GroupBy(s =>
        {
            var referend = (s.Referend ?? "").Trim();
            if (referend.Length > 0)
                return $"R|{referend}";
            return $"P|{s.PatientId}";
        });

        var outFolder = WorkspacePaths.PatientMonthFolder(periodYYYYMM);
        var pdfs = new List<string>();
        var created = 0;

        foreach (var group in groups)
        {
            var seances = group.ToList();
            if (seances.Count == 0) continue;

            var first = seances[0];
            var referend = (first.Referend ?? "").Trim();
            var recipient = referend.Length > 0
                ? referend
                : $"{first.PatientNom} {first.PatientPrenom}".Trim();

            var mmYYYY = WorkspacePaths.ToMMYYYY(periodYYYYMM);

            // Espèces au cabinet : une facture acquittée par lot de séances cash (indépendamment du tiers payant du même groupe / mois).
            // Séances non cash : facture « classique » impayée (tiers payant).
            foreach (var subset in new[]
                     {
                         seances.Where(s => s.IsCash).ToList(),
                         seances.Where(s => !s.IsCash).ToList()
                     })
            {
                if (subset.Count == 0) continue;

                var head = subset[0];
                var anyPatientId = head.PatientId;
                var isAcquittedCash = subset.All(s => s.IsCash);

                var nextNo = GetNextMonthlyPatientInvoiceSuffix(cn, mmYYYY);
                var invoiceNo = $"{mmYYYY}-{nextNo:00}";

                var totalPatientCents = subset.Sum(x => x.PartPatient);
                var status = isAcquittedCash ? "acquittee" : "unpaid";
                var paidCents = isAcquittedCash ? totalPatientCents : 0;

                cn.Execute(@"
INSERT INTO invoices (invoice_no, kind, patient_id, mutuelle, date_iso, total_cents, paid_cents, status, ref_invoice_id, reason, ref_doc, recipient, period)
VALUES (@no,'patient',@pid,NULL,@date,@total,@paid,@status,NULL,NULL,NULL,@recipient,@period);",
                    new
                    {
                        no = invoiceNo,
                        pid = anyPatientId,
                        date = invoiceDateIso,
                        total = totalPatientCents,
                        paid = paidCents,
                        status,
                        recipient,
                        period = periodYYYYMM
                    });

                var invoiceId = cn.ExecuteScalar<long>("SELECT last_insert_rowid();");

                foreach (var s in subset)
                {
                    var firstName = (s.PatientPrenom ?? "").Trim();
                    var tarif = (s.TarifLibelle ?? "Séance").Trim();
                    var label = string.IsNullOrEmpty(firstName)
                        ? tarif
                        : $"Séance de prise en charge de {firstName} — {tarif}";
                    cn.Execute(@"
INSERT INTO invoice_lines (invoice_id,label,qty,unit_price_cents,total_cents,patient_part_cents,mutuelle_part_cents,date_iso,created_at)
VALUES (@iid,@label,1,@unit,@total,@pp,@pm,@date,datetime('now'));",
                        new
                        {
                            iid = invoiceId,
                            label,
                            unit = s.PartPatient + s.PartMutuelle,
                            total = s.PartPatient + s.PartMutuelle,
                            pp = s.PartPatient,
                            pm = s.PartMutuelle,
                            date = s.DateIso
                        });
                }

                var inv = GetById(invoiceId);
                if (inv is null) continue;
                var dbLines = GetLines(invoiceId);
                var lines = dbLines
                    .Select(l => new InvoiceLineRow(l.DateIso ?? "", l.Label ?? "", l.PatientPartCents))
                    .ToList();
                var totalPatientCentsForPdf = dbLines.Sum(l => l.PatientPartCents);

                var path = pdf.BuildPatientInvoicePdfPath(inv);
                var recipientAddress = GetPatientAddressLines(inv.PatientId);
                var patientNames = string.Join(", ",
                    subset.Select(s => (s.PatientPrenom ?? "").Trim())
                        .Where(n => n.Length > 0)
                        .Distinct());
                pdf.GeneratePatientInvoicePdf(inv, lines, path, recipientAddress, patientNames, isAcquittedCash, DateTime.Today, totalPatientCentsForPdf);

                pdfs.Add(path);
                created++;
            }
        }

        // Backup réimportable : met à jour les CSV patients/mutuelles dans Documents\PARAFACTO_Native
        TryExportInvoiceLogsCsv();

        return (created, outFolder, pdfs);
    }

    /// <param name="invoiceDateIso">Date à porter sur les états récap (YYYY-MM-DD). Souvent le 1er du mois suivant la période.</param>
    public (int created, string folder, List<string> pdfs) GenerateMonthlyMutualRecaps(string periodYYYYMM, string invoiceDateIso, bool deleteExisting, InvoicePdfService pdf)
    {
        if (string.IsNullOrWhiteSpace(periodYYYYMM)) return (0, WorkspacePaths.FACTURES_MENSUELLES_MUTUELLES_ROOT(), new());
        periodYYYYMM = periodYYYYMM.Trim();
        if (string.IsNullOrWhiteSpace(invoiceDateIso)) invoiceDateIso = periodYYYYMM + "-01";

        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");

        if (deleteExisting)
            cn.Execute("DELETE FROM invoices WHERE kind='mutuelle' AND period=@p;", new { p = periodYYYYMM });

        // Mutuelles present in seances for period (en excluant les tarifs d'annulation)
        var mutuals = cn.Query<string>(@"
SELECT DISTINCT COALESCE(p.mutuelle,'')
FROM seances s
JOIN patients p ON p.id=s.patient_id
JOIN tarifs t ON t.id=s.tarif_id
WHERE substr(s.date_iso,1,7)=@p
  AND COALESCE(p.mutuelle,'') <> ''
  AND LOWER(COALESCE(t.libelle,'')) NOT IN ('frais d''annulation', 'frais d''annulation 20')
ORDER BY COALESCE(p.mutuelle,'');", new { p = periodYYYYMM }).ToList();


// Detect BIM column (schema may vary depending on migration/import)
var patientCols = cn.Query<(int cid, string name, string type, int notnull, string dflt_value, int pk)>("PRAGMA table_info(patients);")
                    .Select(x => x.name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

string bimSelect;
string bimGroup;

if (patientCols.Contains("is_bim"))
{
    bimSelect = "COALESCE(p.is_bim,0)";
    bimGroup  = "p.is_bim";
}
else if (patientCols.Contains("bim"))
{
    bimSelect = "COALESCE(p.bim,0)";
    bimGroup  = "p.bim";
}
else
{
    bimSelect = "0";
    bimGroup  = "";
}

        var outFolder = WorkspacePaths.MutualMonthFolder(periodYYYYMM);
        var pdfs = new List<string>();
        var created = 0;

        foreach (var mut in mutuals)
        {
            // rows grouped by patient

            var groupExtra = string.IsNullOrWhiteSpace(bimGroup) ? "" : ", " + bimGroup;

            // Ne garder que les patients avec part mutuelle > 0 (tarif plein / cash = rien à facturer à la mutuelle)
            var sqlRows = $@"
SELECT 
  (p.nom || ' ' || p.prenom) AS PatientName,
  COALESCE(p.niss,'') AS Niss,
  {bimSelect} AS IsBim,
  SUM(s.part_mutuelle) AS AoCents,
  SUM(s.part_patient) AS TicketCents
FROM seances s
JOIN patients p ON p.id=s.patient_id
JOIN tarifs t ON t.id=s.tarif_id
WHERE substr(s.date_iso,1,7)=@period
  AND COALESCE(p.mutuelle,'') = @mut
  AND LOWER(COALESCE(t.libelle,'')) NOT IN ('frais d''annulation', 'frais d''annulation 20')
GROUP BY p.id, p.nom, p.prenom, p.niss{groupExtra}
HAVING SUM(s.part_mutuelle) > 0
ORDER BY p.nom, p.prenom;";

            var rows = cn.Query<MutualRow>(sqlRows, new { period = periodYYYYMM, mut }).ToList();

            if (rows.Count == 0) continue;

            var totalCents = rows.Sum(r => r.AoCents);

            // store "invoice" record for tracking in list (no visible number on PDF)
            var invNo = $"ETAT-{WorkspacePaths.ToMMYYYY(periodYYYYMM)}-{mut.ToUpperInvariant()}";
            var invoiceDate = DateTime.TryParseExact(invoiceDateIso, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : DateTime.Today;
            cn.Execute(@"
INSERT INTO invoices (invoice_no, kind, patient_id, mutuelle, date_iso, total_cents, paid_cents, status, ref_invoice_id, reason, ref_doc, recipient, period)
VALUES (@no,'mutuelle',NULL,@mut,@date,@total,0,'unpaid',NULL,NULL,NULL,@recipient,@period);",
                new
                {
                    no = invNo,
                    mut,
                    date = invoiceDateIso,
                    total = totalCents,
                    recipient = mut,
                    period = periodYYYYMM
                });

            var meta = new MutualRecapMeta("", "", "", invoiceDate);

            var path = pdf.BuildMutualRecapPdfPath(mut, periodYYYYMM);
            pdf.GenerateMutualRecapPdf(mut, periodYYYYMM, rows, meta, path, isModification: false, modifiedTotalAoCents: null);

            pdfs.Add(path);
            created++;
        }

        // Backup réimportable : met à jour les CSV patients/mutuelles dans Documents\PARAFACTO_Native
        TryExportInvoiceLogsCsv();

        return (created, outFolder, pdfs);
    }

    private sealed record InvoiceLogRow(
        string InvoiceNo,
        string Kind,
        string? DateIso,
        int TotalCents,
        string? Recipient,
        string? Mutuelle,
        string? Period,
        string? RefDoc);

    /// <summary>
    /// Exporte une copie "vintage" des factures en CSV (patients_invoices_log.csv / mutuelles_invoices_log.csv)
    /// dans le workspace (Documents\PARAFACTO_Native). Sert de sauvegarde réimportable en cas de crash.
    /// </summary>
    private static void TryExportInvoiceLogsCsv()
    {
        try
        {
            var root = WorkspacePaths.TryFindWorkspaceRoot();
            Directory.CreateDirectory(root);

            using var cn = Db.Open();
            cn.Execute("PRAGMA foreign_keys = ON;");

            var rows = cn.Query<InvoiceLogRow>(@"
SELECT
  invoice_no  AS InvoiceNo,
  kind        AS Kind,
  date_iso    AS DateIso,
  total_cents AS TotalCents,
  recipient   AS Recipient,
  mutuelle    AS Mutuelle,
  period      AS Period,
  ref_doc     AS RefDoc
FROM invoices
WHERE kind IN ('patient','mutuelle','credit_note')
ORDER BY date_iso, invoice_no;
").ToList();

            var patientCsv = Path.Combine(root, "patients_invoices_log.csv");
            var mutualCsv = Path.Combine(root, "mutuelles_invoices_log.csv");

            WriteInvoicesCsv(patientCsv,
                rows.Where(r => string.Equals(r.Kind, "patient", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(r.Kind, "credit_note", StringComparison.OrdinalIgnoreCase))
                    .ToList(),
                kind: "patient",
                root);

            WriteInvoicesCsv(mutualCsv,
                rows.Where(r => string.Equals(r.Kind, "mutuelle", StringComparison.OrdinalIgnoreCase)).ToList(),
                kind: "mutuelle",
                root);
        }
        catch
        {
            // Ne pas bloquer la génération de factures si l'export CSV échoue (OneDrive verrouillé, etc.)
        }
    }

    private static void WriteInvoicesCsv(string path, List<InvoiceLogRow> rows, string kind, string root)
    {
        // Format simple compatible import : séparateur ';' + en-têtes reconnus par ImportService
        // Colonnes utiles : NumeroFacture, DateFacturation, Destinataire, Periode, Montant, PdfPath
        using var stream = File.Open(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var sw = new StreamWriter(stream, Encoding.GetEncoding(1252));

        sw.WriteLine("NumeroFacture;DateFacturation;Destinataire;Periode;Montant;PdfPath");

        foreach (var r in rows)
        {
            var invoiceNo = r.InvoiceNo?.Trim() ?? "";
            var dateIso = (r.DateIso ?? "").Trim();
            var period = (r.Period ?? "").Trim();
            var destin = kind == "mutuelle"
                ? (r.Mutuelle ?? r.Recipient ?? "").Trim()
                : (r.Recipient ?? "").Trim();

            // Montant: format "123,45" (fr) - compatible MoneyToCents (remplace ',' par '.')
            var montant = (r.TotalCents / 100m).ToString("0.00", CultureInfo.GetCultureInfo("fr-BE"));

            // PdfPath: stocker un chemin relatif au workspace si possible
            var pdfPath = "";
            if (!string.IsNullOrWhiteSpace(r.RefDoc))
                pdfPath = WorkspacePaths.MakeRelativeToRoot(root, r.RefDoc.Trim());

            sw.WriteLine(string.Join(";",
                Csv(invoiceNo),
                Csv(dateIso),
                Csv(destin),
                Csv(period),
                Csv(montant),
                Csv(pdfPath)));
        }
    }

    private static string Csv(string s)
    {
        s ??= "";
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        if (s.Contains(';') || s.Contains('"'))
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }

}