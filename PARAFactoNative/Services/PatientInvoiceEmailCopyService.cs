using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace PARAFactoNative.Services;

/// <summary>Archive locale des factures patients envoyées par e-mail (copie PDF + journal).</summary>
public sealed class PatientInvoiceEmailCopyService
{
    private const string JournalFileName = "journal_envois.csv";

    public string GetCopiesFolder(string periodYYYYMM)
        => WorkspacePaths.PatientMonthEmailCopiesFolder(periodYYYYMM);

    public string? TrySaveSentEmailCopy(
        string periodYYYYMM,
        string invoiceNo,
        string patientDisplayName,
        string toEmail,
        string fromEmail,
        string subject,
        string pdfAttachmentPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(pdfAttachmentPath) || !File.Exists(pdfAttachmentPath))
                return null;

            var folder = GetCopiesFolder(periodYYYYMM);
            var safeInvoice = MakeSafeFileToken(invoiceNo);
            var safePatient = MakeSafeFileToken(patientDisplayName);
            var safeEmail = MakeSafeFileToken(toEmail);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var pdfCopyPath = Path.Combine(folder, $"{safeInvoice}_{safePatient}_{stamp}_{safeEmail}.pdf");

            Directory.CreateDirectory(folder);
            File.Copy(pdfAttachmentPath, pdfCopyPath, overwrite: false);

            AppendJournalEntry(folder, periodYYYYMM, invoiceNo, patientDisplayName, toEmail, fromEmail, subject, pdfCopyPath);
            return pdfCopyPath;
        }
        catch
        {
            return null;
        }
    }

    private static void AppendJournalEntry(
        string folder,
        string periodYYYYMM,
        string invoiceNo,
        string patientDisplayName,
        string toEmail,
        string fromEmail,
        string subject,
        string pdfPath)
    {
        var journalPath = Path.Combine(folder, JournalFileName);
        var headerNeeded = !File.Exists(journalPath);
        var line = string.Join(";",
            EscapeCsv(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
            EscapeCsv(periodYYYYMM),
            EscapeCsv(invoiceNo),
            EscapeCsv(patientDisplayName),
            EscapeCsv(toEmail),
            EscapeCsv(fromEmail),
            EscapeCsv(subject),
            EscapeCsv(pdfPath));

        using var stream = new FileStream(journalPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        if (headerNeeded)
            writer.WriteLine("date_heure;periode;facture;patient;destinataire;expediteur;objet;fichier_pdf");
        writer.WriteLine(line);
    }

    private static string EscapeCsv(string? value)
    {
        value ??= "";
        if (value.Contains('"') || value.Contains(';') || value.Contains('\n') || value.Contains('\r'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }

    private static string MakeSafeFileToken(string value)
    {
        var s = (value ?? "").Trim();
        if (s.Length == 0) return "SANS_NOM";
        foreach (var ch in Path.GetInvalidFileNameChars())
            s = s.Replace(ch, '_');
        return s.Length > 60 ? s[..60] : s;
    }
}
