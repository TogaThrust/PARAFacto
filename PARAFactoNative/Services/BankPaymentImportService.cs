using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PARAFactoNative.Services;

public sealed class BankPaymentImportService
{
    private readonly InvoiceRepo _repo;

    public BankPaymentImportService(InvoiceRepo repo) => _repo = repo;

    public enum ManualReviewAction
    {
        ApplyToInvoice,
        Skip,
        Stop
    }

    public sealed class ImportSummary
    {
        public int TransactionsRead { get; set; }
        public int Applied { get; set; }
        public int Duplicates { get; set; }
        public int ManualReview { get; set; }
        public int Skipped { get; set; }
        public int Ignored { get; set; }
        public int FilesProcessed { get; set; }
        public bool StoppedByUser { get; set; }
        public string ReportPath { get; set; } = "";

        public void Add(ImportSummary other)
        {
            TransactionsRead += other.TransactionsRead;
            Applied += other.Applied;
            Duplicates += other.Duplicates;
            ManualReview += other.ManualReview;
            Skipped += other.Skipped;
            Ignored += other.Ignored;
            FilesProcessed += other.FilesProcessed;
            StoppedByUser |= other.StoppedByUser;
            if (!string.IsNullOrWhiteSpace(other.ReportPath))
                ReportPath = other.ReportPath;
        }
    }

    public sealed class BankTransaction
    {
        public int LineNumber { get; init; }
        public string DateIso { get; init; } = "";
        public int AmountCents { get; init; }
        public string Communication { get; init; } = "";
        public string Counterparty { get; init; } = "";
        public string Reference { get; init; } = "";
        public string SourceFile { get; init; } = "";
        public string ImportHash { get; init; } = "";
        public string RawText => string.Join(" ", new[] { Communication, Counterparty, Reference }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
        public string AmountEuro => (AmountCents / 100m).ToString("0.00", CultureInfo.GetCultureInfo("fr-BE")) + " €";
    }

    public sealed class ReviewRequest
    {
        public BankTransaction Transaction { get; init; } = new();
        public string Reason { get; init; } = "";
        public List<InvoiceRepo.BankPaymentCandidate> Candidates { get; init; } = new();
    }

    public sealed class ReviewDecision
    {
        public ManualReviewAction Action { get; init; }
        public long InvoiceId { get; init; }
    }

    private sealed class ReportRow
    {
        public string Status { get; init; } = "";
        public string DateIso { get; init; } = "";
        public int AmountCents { get; init; }
        public string InvoiceNo { get; init; } = "";
        public string Recipient { get; init; } = "";
        public string Reason { get; init; } = "";
        public string Communication { get; init; } = "";
    }

    public ImportSummary ImportCsv(string csvPath, Func<ReviewRequest, ReviewDecision>? review = null)
    {
        if (string.IsNullOrWhiteSpace(csvPath))
            throw new ArgumentException("Chemin de fichier requis.", nameof(csvPath));
        if (!File.Exists(csvPath))
            throw new FileNotFoundException("Extrait bancaire introuvable.", csvPath);

        var transactions = ReadTransactions(csvPath);
        var candidates = _repo.ListBankPaymentCandidates();
        var report = new List<ReportRow>();
        var summary = new ImportSummary { TransactionsRead = transactions.Count, FilesProcessed = 1 };

        foreach (var tx in transactions)
        {
            var registered = _repo.RegisterBankTransaction(new InvoiceRepo.BankTransactionImportRow
            {
                ImportHash = tx.ImportHash,
                SourceFile = tx.SourceFile,
                LineNo = tx.LineNumber,
                BookedDate = tx.DateIso,
                AmountCents = tx.AmountCents,
                Communication = tx.Communication,
                Counterparty = tx.Counterparty,
                BankReference = tx.Reference,
                RawText = tx.RawText
            });
            if (!registered.Inserted)
            {
                summary.Duplicates++;
                report.Add(ToReport(tx, "", "", "DOUBLON_IMPORT", "Transaction bancaire deja importee."));
                continue;
            }

            if (tx.AmountCents <= 0)
            {
                summary.Ignored++;
                _repo.UpdateBankTransactionDecision(tx.ImportHash, "ignored", null, "Montant sortant ou nul.");
                report.Add(ToReport(tx, "", "", "IGNORE", "Montant sortant ou nul."));
                continue;
            }

            var text = NormalizeForSearch(tx.RawText);
            var exactMatches = candidates
                .Where(c => c.BalanceCents > 0 && MatchesReference(text, c))
                .ToList();

            if (exactMatches.Count == 1 && tx.AmountCents == exactMatches[0].BalanceCents)
            {
                ApplyPayment(tx, exactMatches[0], summary, report, manual: false);
                continue;
            }

            var reason = exactMatches.Count switch
            {
                0 => "Aucune communication structuree reconnue.",
                1 when tx.AmountCents < exactMatches[0].BalanceCents => "Paiement partiel detecte.",
                1 when tx.AmountCents > exactMatches[0].BalanceCents => "Montant superieur au solde restant.",
                _ => "Plusieurs factures correspondent a la communication."
            };

            var suggested = exactMatches.Count > 0
                ? exactMatches
                : SuggestCandidates(candidates, tx, text);

            if (review is null)
            {
                summary.ManualReview++;
                _repo.UpdateBankTransactionDecision(tx.ImportHash, "manual_review", null, reason);
                report.Add(ToReport(tx, "", "", "A_VERIFIER", reason));
                continue;
            }

            summary.ManualReview++;
            var decision = review(new ReviewRequest
            {
                Transaction = tx,
                Reason = reason,
                Candidates = suggested
            });

            if (decision.Action == ManualReviewAction.Stop)
            {
                summary.StoppedByUser = true;
                _repo.UpdateBankTransactionDecision(tx.ImportHash, "manual_review", null, "Import interrompu sur cette transaction.");
                report.Add(ToReport(tx, "", "", "ARRET", "Import interrompu par l'utilisateur."));
                break;
            }

            if (decision.Action == ManualReviewAction.Skip || decision.InvoiceId <= 0)
            {
                summary.Skipped++;
                _repo.UpdateBankTransactionDecision(tx.ImportHash, "skipped", null, "Paiement ignore manuellement.");
                report.Add(ToReport(tx, "", "", "PASSE", "Paiement ignore manuellement."));
                continue;
            }

            var selected = candidates.FirstOrDefault(c => c.InvoiceId == decision.InvoiceId)
                           ?? _repo.ListBankPaymentCandidates().FirstOrDefault(c => c.InvoiceId == decision.InvoiceId);
            if (selected is null)
            {
                summary.Skipped++;
                _repo.UpdateBankTransactionDecision(tx.ImportHash, "skipped", null, "Facture selectionnee introuvable.");
                report.Add(ToReport(tx, "", "", "PASSE", "Facture selectionnee introuvable."));
                continue;
            }

            ApplyPayment(tx, selected, summary, report, manual: true);
        }

        summary.ReportPath = WriteReport(csvPath, report);
        return summary;
    }

    public ImportSummary ImportFolder(string folderPath, Func<ReviewRequest, ReviewDecision>? review = null)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            folderPath = WorkspacePaths.BANK_STATEMENTS_FOLDER();
        Directory.CreateDirectory(folderPath);

        var files = Directory.EnumerateFiles(folderPath, "*.*", SearchOption.TopDirectoryOnly)
            .Where(f =>
            {
                var ext = Path.GetExtension(f);
                return ext.Equals(".csv", StringComparison.OrdinalIgnoreCase)
                       || ext.Equals(".txt", StringComparison.OrdinalIgnoreCase)
                       || ext.Equals(".xml", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var summary = new ImportSummary();
        foreach (var file in files)
        {
            var one = ImportCsv(file, review);
            summary.Add(one);
            if (summary.StoppedByUser)
                break;
        }

        return summary;
    }

    private void ApplyPayment(BankTransaction tx, InvoiceRepo.BankPaymentCandidate invoice, ImportSummary summary, List<ReportRow> report, bool manual)
    {
        var reference = tx.RawText.Length > 0 ? tx.RawText : $"Import bancaire ligne {tx.LineNumber}";
        if (!_repo.TryAddBankPaymentIfNotDuplicate(invoice.InvoiceId, tx.DateIso, tx.AmountCents, "Virement", reference))
        {
            summary.Duplicates++;
            _repo.UpdateBankTransactionDecision(tx.ImportHash, "duplicate_payment", invoice.InvoiceId, "Paiement deja present sur la facture.");
            report.Add(ToReport(tx, invoice.InvoiceNo, invoice.Recipient, "DOUBLON", "Paiement deja present."));
            return;
        }

        invoice.BalanceCents -= tx.AmountCents;
        invoice.PaidCents += tx.AmountCents;
        summary.Applied++;
        _repo.UpdateBankTransactionDecision(tx.ImportHash, manual ? "associated_manual" : "associated_auto", invoice.InvoiceId, manual ? "Paiement ajoute apres validation manuelle." : "Communication et montant exacts.");
        report.Add(ToReport(tx, invoice.InvoiceNo, invoice.Recipient, manual ? "ASSOCIE_MANUEL" : "ASSOCIE", manual ? "Paiement ajoute apres validation manuelle." : "Communication et montant exacts."));
    }

    private static List<InvoiceRepo.BankPaymentCandidate> SuggestCandidates(List<InvoiceRepo.BankPaymentCandidate> candidates, BankTransaction tx, string normalizedText)
    {
        var scored = candidates
            .Where(c => c.BalanceCents > 0)
            .Select(c => new
            {
                Candidate = c,
                Score =
                    (c.BalanceCents == tx.AmountCents ? 100 : 0)
                    + (Math.Abs(c.BalanceCents - tx.AmountCents) <= 200 ? 20 : 0)
                    + (TextContainsRecipient(normalizedText, c.Recipient) ? 60 : 0)
                    + (TextContainsInvoiceNo(normalizedText, c.InvoiceNo) ? 80 : 0)
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => Math.Abs(x.Candidate.BalanceCents - tx.AmountCents))
            .Take(30)
            .Select(x => x.Candidate)
            .ToList();

        if (scored.Count > 0)
            return scored;

        return candidates
            .Where(c => c.BalanceCents > 0)
            .OrderBy(c => Math.Abs(c.BalanceCents - tx.AmountCents))
            .Take(30)
            .ToList();
    }

    private static bool MatchesReference(string normalizedText, InvoiceRepo.BankPaymentCandidate candidate)
    {
        if (normalizedText.Length == 0)
            return false;
        return TextContainsPaymentReference(normalizedText, candidate.PaymentReference)
               || TextContainsInvoiceNo(normalizedText, candidate.InvoiceNo);
    }

    private static bool TextContainsPaymentReference(string normalizedText, string? paymentReference)
    {
        var reference = NormalizeForSearch(paymentReference);
        if (reference.Length == 0)
            return false;
        var compactText = Regex.Replace(normalizedText, @"[^0-9A-Z]+", "");
        var compactRef = Regex.Replace(reference, @"[^0-9A-Z]+", "");
        return compactRef.Length > 0 && compactText.Contains(compactRef, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TextContainsInvoiceNo(string normalizedText, string invoiceNo)
    {
        var no = NormalizeForSearch(invoiceNo);
        return no.Length > 0 && normalizedText.Contains(no, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TextContainsRecipient(string normalizedText, string recipient)
    {
        var rec = NormalizeForSearch(recipient);
        if (rec.Length < 4)
            return false;
        var tokens = rec.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 3)
            .ToList();
        return tokens.Count > 0 && tokens.Any(t => normalizedText.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    private static ReportRow ToReport(BankTransaction tx, string invoiceNo, string recipient, string status, string reason) => new()
    {
        Status = status,
        DateIso = tx.DateIso,
        AmountCents = tx.AmountCents,
        InvoiceNo = invoiceNo,
        Recipient = recipient,
        Reason = reason,
        Communication = tx.RawText
    };

    private static List<BankTransaction> ReadTransactions(string csvPath)
    {
        if (Path.GetExtension(csvPath).Equals(".xml", StringComparison.OrdinalIgnoreCase))
            return ReadCamtTransactions(csvPath);

        var rawLines = new List<string>();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using (var stream = File.Open(csvPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var sr = new StreamReader(stream, Encoding.GetEncoding(1252), detectEncodingFromByteOrderMarks: true))
        {
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    rawLines.Add(line);
            }
        }

        if (rawLines.Count == 0)
            return new List<BankTransaction>();

        var headerIndex = FindHeaderRowIndex(rawLines);
        var headerLine = rawLines[headerIndex];
        var separator = DetectSeparator(headerLine);
        var headers = ParseCsvLine(headerLine, separator).Select(CleanCell).ToArray();
        var rows = new List<BankTransaction>();

        for (var i = headerIndex + 1; i < rawLines.Count; i++)
        {
            var parts = ParseCsvLine(rawLines[i], separator).Select(CleanCell).ToArray();
            if (parts.All(string.IsNullOrWhiteSpace))
                continue;
            if (LooksLikeHeaderRow(parts))
                continue;

            string get(params string[] names)
            {
                foreach (var name in names)
                {
                    var wanted = NormalizeHeader(name);
                    for (var h = 0; h < headers.Length && h < parts.Length; h++)
                    {
                        if (NormalizeHeader(headers[h]) == wanted)
                            return parts[h];
                    }
                }

                return "";
            }

            var dateIso = ToIsoDate(get("Date", "Date operation", "Date opération", "Date comptabilisation", "Booking Date", "Execution Date"));
            var amount = MoneyToCents(get("Montant", "Amount", "Credit", "Crédit", "Entree", "Entrée"));
            var debit = MoneyToCents(get("Debit", "Débit"));
            if (amount == 0 && debit > 0)
                amount = -debit;

            if (string.IsNullOrWhiteSpace(dateIso))
                dateIso = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            rows.Add(new BankTransaction
            {
                LineNumber = i + 1,
                DateIso = dateIso,
                AmountCents = amount,
                Communication = get("Communication", "Description", "Libelle", "Libellé", "Detail", "Détail", "Remittance Information", "Message"),
                Counterparty = get("Contrepartie", "Nom", "Name", "Counterparty", "Beneficiaire", "Bénéficiaire", "Donneur d'ordre"),
                Reference = get("Reference", "Référence", "Ref", "Réf", "EndToEndId", "TransactionId"),
                SourceFile = Path.GetFileName(csvPath),
                ImportHash = ComputeImportHash(Path.GetFileName(csvPath), i + 1, dateIso, amount,
                    get("Communication", "Description", "Libelle", "Libellé", "Detail", "Détail", "Remittance Information", "Message"),
                    get("Contrepartie", "Nom", "Name", "Counterparty", "Beneficiaire", "Bénéficiaire", "Donneur d'ordre"),
                    get("Reference", "Référence", "Ref", "Réf", "EndToEndId", "TransactionId"))
            });
        }

        return rows;
    }

    /// <summary>
    /// Ignore les lignes de titre/commentaire avant la vraie ligne d'en-têtes (ex. « +++PARAFACTO CSV TEST+++ »).
    /// </summary>
    private static int FindHeaderRowIndex(List<string> rawLines)
    {
        for (var i = 0; i < rawLines.Count; i++)
        {
            var line = rawLines[i];
            var separator = DetectSeparator(line);
            var cells = ParseCsvLine(line, separator).Select(CleanCell).ToArray();
            if (cells.Length < 2)
                continue;

            var normalized = cells.Select(NormalizeHeader).ToArray();
            var hasDate = normalized.Any(h => h is "date" or "date operation" or "date comptabilisation" or "booking date" or "execution date");
            var hasAmount = normalized.Any(h =>
                h is "montant" or "amount" or "credit" or "entree" or "debit"
                || h.StartsWith("montant ", StringComparison.Ordinal)
                || h.StartsWith("amount ", StringComparison.Ordinal));

            if (hasDate && hasAmount)
                return i;
        }

        return 0;
    }

    private static bool LooksLikeHeaderRow(string[] parts)
    {
        if (parts.Length == 0)
            return false;
        var first = NormalizeHeader(parts[0]);
        return first is "date" or "date operation" or "date comptabilisation";
    }

    private static char DetectSeparator(string line)
    {
        var candidates = new[] { ';', ',', '\t' };
        return candidates.OrderByDescending(c => ParseCsvLine(line, c).Length).First();
    }

    private static List<BankTransaction> ReadCamtTransactions(string xmlPath)
    {
        var doc = XDocument.Load(xmlPath);
        var rows = new List<BankTransaction>();
        var entries = doc.Descendants().Where(e => e.Name.LocalName == "Ntry").ToList();

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var dateIso = FirstDescendantValue(entry, "BookgDt", "Dt");
            if (dateIso.Length == 0)
                dateIso = FirstDescendantValue(entry, "ValDt", "Dt");
            if (dateIso.Length == 0)
                dateIso = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            var amountText = entry.Elements().FirstOrDefault(e => e.Name.LocalName == "Amt")?.Value ?? "";
            var amount = MoneyToCents(amountText);
            var creditDebit = FirstDescendantValue(entry, "CdtDbtInd");
            if (creditDebit.Equals("DBIT", StringComparison.OrdinalIgnoreCase))
                amount = -Math.Abs(amount);
            else
                amount = Math.Abs(amount);

            var txDetails = entry.Descendants().FirstOrDefault(e => e.Name.LocalName == "TxDtls") ?? entry;
            var communication = string.Join(" ",
                txDetails.Descendants()
                    .Where(e => e.Name.LocalName is "Ustrd" or "Ref" or "AddtlRmtInf")
                    .Select(e => (e.Value ?? "").Trim())
                    .Where(v => v.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase));
            if (communication.Length == 0)
                communication = FirstDescendantValue(entry, "AddtlNtryInf");

            var counterparty = FirstDescendantValue(txDetails, "Dbtr", "Nm");
            if (counterparty.Length == 0)
                counterparty = FirstDescendantValue(txDetails, "RltdPties", "Nm");

            var reference = FirstDescendantValue(entry, "NtryRef");
            if (reference.Length == 0)
                reference = FirstDescendantValue(txDetails, "EndToEndId");

            rows.Add(new BankTransaction
            {
                LineNumber = i + 1,
                DateIso = ToIsoDate(dateIso),
                AmountCents = amount,
                Communication = communication,
                Counterparty = counterparty,
                Reference = reference,
                SourceFile = Path.GetFileName(xmlPath),
                ImportHash = ComputeImportHash(Path.GetFileName(xmlPath), i + 1, dateIso, amount, communication, counterparty, reference)
            });
        }

        return rows;
    }

    private static string FirstDescendantValue(XElement root, params string[] localNamePath)
    {
        if (localNamePath.Length == 0)
            return "";

        IEnumerable<XElement> current = new[] { root };
        foreach (var name in localNamePath)
        {
            current = current
                .SelectMany(e => e.Descendants().Where(d => d.Name.LocalName == name))
                .ToList();
            if (!current.Any())
                return "";
        }

        return (current.FirstOrDefault()?.Value ?? "").Trim();
    }

    private static string ComputeImportHash(string sourceFile, int lineNo, string dateIso, int amountCents, string communication, string counterparty, string reference)
    {
        var content = string.Join("|", new[]
        {
            dateIso.Trim(),
            amountCents.ToString(CultureInfo.InvariantCulture),
            NormalizeForSearch(communication),
            NormalizeForSearch(counterparty),
            NormalizeForSearch(reference)
        });
        if (content.Trim('|').Length == 0)
            content = $"{sourceFile}|{lineNo}";

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes);
    }

    private static string[] ParseCsvLine(string line, char separator)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
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

    private static string CleanCell(string? s)
    {
        s = (s ?? "").Trim().Trim('\uFEFF').Trim();
        if (s.Length >= 2 && s.StartsWith("\"", StringComparison.Ordinal) && s.EndsWith("\"", StringComparison.Ordinal))
            s = s[1..^1];
        return s.Trim();
    }

    private static string NormalizeHeader(string? s)
    {
        s = RemoveDiacritics((s ?? "").Trim()).ToLowerInvariant();
        return Regex.Replace(s, @"[^a-z0-9]+", " ").Trim();
    }

    private static string NormalizeForSearch(string? s)
    {
        s = RemoveDiacritics((s ?? "").Trim()).ToUpperInvariant();
        s = s.Replace('\u2011', '-').Replace('\u2012', '-').Replace('\u2013', '-').Replace('\u2014', '-').Replace('\u2212', '-');
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

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

    private static string ToIsoDate(string? s)
    {
        s = (s ?? "").Trim();
        if (s.Length == 0)
            return "";
        if (Regex.IsMatch(s, @"^\d{4}-\d{2}-\d{2}$"))
            return s;
        if (DateTime.TryParseExact(s,
                new[] { "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "dd.MM.yyyy", "d.M.yyyy", "yyyy/MM/dd" },
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var d))
            return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (decimal.TryParse(s.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var serial))
        {
            try
            {
                return DateTime.FromOADate((double)serial).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            catch
            {
                // ignore invalid Excel serials
            }
        }
        return DateTime.TryParse(s, CultureInfo.GetCultureInfo("fr-BE"), DateTimeStyles.None, out d)
            ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : "";
    }

    private static int MoneyToCents(string? s)
    {
        s = (s ?? "").Trim();
        if (s.Length == 0)
            return 0;

        s = s.Replace("\u00A0", " ")
            .Replace("EUR", "", StringComparison.OrdinalIgnoreCase)
            .Replace("€", "")
            .Replace(" ", "")
            .Replace("\t", "")
            .Replace("−", "-");
        if (s.StartsWith("(", StringComparison.Ordinal) && s.EndsWith(")", StringComparison.Ordinal))
            s = "-" + s.Trim('(', ')');

        var hasComma = s.Contains(',');
        var hasDot = s.Contains('.');
        if (hasComma && hasDot)
            s = s.Replace(".", "").Replace(",", ".");
        else if (hasComma)
            s = s.Replace(",", ".");

        return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? (int)Math.Round(value * 100m, MidpointRounding.AwayFromZero)
            : 0;
    }

    private static string WriteReport(string sourceCsvPath, List<ReportRow> rows)
    {
        var root = WorkspacePaths.TryFindWorkspaceRoot();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            root = Path.GetDirectoryName(sourceCsvPath) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        var reportPath = Path.Combine(root, "association_auto_paiements_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".csv");
        using var stream = File.Open(reportPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var sw = new StreamWriter(stream, Encoding.UTF8);
        sw.WriteLine("Statut;Date;Montant;Facture;Destinataire;Raison;Communication");
        foreach (var r in rows)
        {
            sw.WriteLine(string.Join(";",
                Csv(r.Status),
                Csv(r.DateIso),
                Csv((r.AmountCents / 100m).ToString("0.00", CultureInfo.GetCultureInfo("fr-BE"))),
                Csv(r.InvoiceNo),
                Csv(r.Recipient),
                Csv(r.Reason),
                Csv(r.Communication)));
        }

        return reportPath;
    }

    private static string Csv(string? s)
    {
        s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Contains(';') || s.Contains('"')
            ? "\"" + s.Replace("\"", "\"\"") + "\""
            : s;
    }
}
