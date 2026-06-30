using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;
using PARAFactoNative.Models;

namespace PARAFactoNative.Services;

/// <summary>Archive locale des e-mails de factures patients envoyés (preuve / traçabilité).</summary>
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
        string? replyToEmail,
        string subject,
        string htmlBody,
        string pdfAttachmentPath,
        string? inlineLogoPath)
    {
        try
        {
            var folder = GetCopiesFolder(periodYYYYMM);
            var safeInvoice = MakeSafeFileToken(invoiceNo);
            var safeEmail = MakeSafeFileToken(toEmail);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var emlPath = Path.Combine(folder, $"{safeInvoice}_{stamp}_{safeEmail}.eml");

            using var msg = BuildMailMessage(fromEmail, replyToEmail, toEmail, subject, htmlBody, pdfAttachmentPath, inlineLogoPath);
            if (!TryWriteEmlViaPickupDirectory(msg, emlPath))
                return null;

            AppendJournalEntry(folder, periodYYYYMM, invoiceNo, patientDisplayName, toEmail, fromEmail, subject, emlPath);
            return emlPath;
        }
        catch
        {
            return null;
        }
    }

    private static MailMessage BuildMailMessage(
        string fromEmail,
        string? replyToEmail,
        string toEmail,
        string subject,
        string htmlBody,
        string pdfAttachmentPath,
        string? inlineLogoPath)
    {
        var msg = new MailMessage();
        msg.From = new MailAddress(fromEmail);
        if (!string.IsNullOrWhiteSpace(replyToEmail) &&
            !string.Equals(replyToEmail.Trim(), fromEmail.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            msg.ReplyToList.Add(new MailAddress(replyToEmail.Trim()));
        }

        msg.To.Add(new MailAddress(toEmail.Trim()));
        msg.Subject = subject ?? "";
        msg.Body = BuildPlainTextFallback(htmlBody);
        msg.IsBodyHtml = false;

        var plainView = AlternateView.CreateAlternateViewFromString(msg.Body, null, MediaTypeNames.Text.Plain);
        var htmlView = AlternateView.CreateAlternateViewFromString(htmlBody ?? "", null, MediaTypeNames.Text.Html);
        if (!string.IsNullOrWhiteSpace(inlineLogoPath) && File.Exists(inlineLogoPath))
        {
            var logo = new LinkedResource(inlineLogoPath)
            {
                ContentId = "parafacto-logo",
                TransferEncoding = TransferEncoding.Base64
            };
            logo.ContentType.MediaType = ResolveImageMimeType(inlineLogoPath);
            logo.ContentType.Name = Path.GetFileName(inlineLogoPath);
            htmlView.LinkedResources.Add(logo);
        }

        msg.AlternateViews.Add(plainView);
        msg.AlternateViews.Add(htmlView);

        if (File.Exists(pdfAttachmentPath))
            msg.Attachments.Add(new Attachment(pdfAttachmentPath));

        return msg;
    }

    private static bool TryWriteEmlViaPickupDirectory(MailMessage msg, string targetEmlPath)
    {
        var pickupDir = Path.Combine(Path.GetTempPath(), "parafacto_eml_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(pickupDir);
        try
        {
            using var client = new SmtpClient
            {
                DeliveryMethod = SmtpDeliveryMethod.SpecifiedPickupDirectory,
                PickupDirectoryLocation = pickupDir
            };
            client.Send(msg);

            var written = Directory.GetFiles(pickupDir, "*.eml", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(written))
                return false;

            Directory.CreateDirectory(Path.GetDirectoryName(targetEmlPath)!);
            if (File.Exists(targetEmlPath))
                File.Delete(targetEmlPath);
            File.Move(written, targetEmlPath);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { Directory.Delete(pickupDir, true); } catch { /* ignore */ }
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
        string emlPath)
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
            EscapeCsv(emlPath));

        using var stream = new FileStream(journalPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        if (headerNeeded)
            writer.WriteLine("date_heure;periode;facture;patient;destinataire;expediteur;objet;fichier_eml");
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
        return s.Length > 80 ? s[..80] : s;
    }

    private static string BuildPlainTextFallback(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return "";
        var s = html;
        s = System.Text.RegularExpressions.Regex.Replace(s, "<br\\s*/?>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, "</p>", "\n\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, "<[^>]+>", "");
        return System.Net.WebUtility.HtmlDecode(s).Trim();
    }

    private static string ResolveImageMimeType(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => "image/jpeg"
        };
    }
}
