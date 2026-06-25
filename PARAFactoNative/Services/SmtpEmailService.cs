using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Text.RegularExpressions;

namespace PARAFactoNative.Services;

public sealed class SmtpEmailService
{
    public bool TrySendMail(
        AppMailSettings settings,
        string to,
        string from,
        string subject,
        string body,
        out string error)
    {
        error = "";

        to = (to ?? "").Trim();
        if (string.IsNullOrWhiteSpace(to))
        {
            error = "Aucune adresse e-mail destinataire n'est renseignée.";
            return false;
        }

        from = (from ?? "").Trim();
        if (string.IsNullOrWhiteSpace(from))
        {
            error = "Aucune adresse e-mail expéditeur n'est renseignée.";
            return false;
        }

        var host = (settings?.SmtpHost ?? "").Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            error = "SMTP: serveur (host) manquant.";
            return false;
        }

        var user = (settings?.SmtpUsername ?? "").Trim();
        // Gmail affiche le mot de passe d’application avec des espaces ; le serveur attend 16 caractères sans espaces.
        var pass = (settings?.SmtpPassword ?? "").Trim().Replace(" ", "");
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
        {
            error = "SMTP: identifiant ou mot de passe manquant.";
            return false;
        }

        try
        {
            using var msg = new MailMessage();
            msg.From = new MailAddress(from);
            msg.To.Add(new MailAddress(to));
            msg.Subject = subject ?? "";
            msg.Body = body ?? "";

            var port = settings?.SmtpPort ?? 587;
            if (port <= 0) port = 587;

            using var client = new SmtpClient(host, port)
            {
                EnableSsl = true,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                UseDefaultCredentials = false,
                Credentials = new NetworkCredential(user, pass)
            };

            client.Send(msg);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public bool TrySendMailWithAttachments(
        AppMailSettings settings,
        string subject,
        string body,
        IEnumerable<string> attachments,
        out string error,
        bool isHtml = false,
        string? inlineLogoPath = null,
        string? senderEmail = null)
    {
        error = "";

        var to = (settings?.RecipientEmail ?? "").Trim();
        if (string.IsNullOrWhiteSpace(to))
        {
            error = "Aucune adresse e-mail destinataire n'est renseignée.";
            return false;
        }

        var host = (settings?.SmtpHost ?? "").Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            error = "SMTP: serveur (host) manquant.";
            return false;
        }

        var user = (settings?.SmtpUsername ?? "").Trim();
        // Gmail affiche le mot de passe d’application avec des espaces ; le serveur attend 16 caractères sans espaces.
        var pass = (settings?.SmtpPassword ?? "").Trim().Replace(" ", "");
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
        {
            error = "SMTP: identifiant ou mot de passe manquant.";
            return false;
        }

        var preferredSender = (senderEmail ?? "").Trim();
        var configuredFrom = (settings?.SmtpFromEmail ?? "").Trim();
        var from = CanUseAsSmtpFrom(preferredSender, configuredFrom, user)
            ? preferredSender
            : configuredFrom;
        if (string.IsNullOrWhiteSpace(from))
            from = user; // défaut classique (Gmail)

        var att = (attachments ?? Array.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(File.Exists)
            .ToList();

        if (att.Count == 0)
        {
            error = "Aucun PDF à joindre (fichiers introuvables).";
            return false;
        }

        try
        {
            using var msg = new MailMessage();
            msg.From = new MailAddress(from);
            if (!string.IsNullOrWhiteSpace(preferredSender) &&
                !string.Equals(preferredSender, from, StringComparison.OrdinalIgnoreCase))
            {
                // Gmail et beaucoup de SMTP refusent un From arbitraire. Reply-To garde l'adresse du praticien utile au patient.
                msg.ReplyToList.Add(new MailAddress(preferredSender));
            }
            msg.To.Add(new MailAddress(to));
            msg.Subject = subject ?? "";
            msg.Body = isHtml ? BuildPlainTextFallback(body) : (body ?? "");
            msg.IsBodyHtml = false;

            if (isHtml)
            {
                var plainView = AlternateView.CreateAlternateViewFromString(msg.Body, null, MediaTypeNames.Text.Plain);
                var htmlView = AlternateView.CreateAlternateViewFromString(body ?? "", null, MediaTypeNames.Text.Html);
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
            }

            foreach (var path in att)
                msg.Attachments.Add(new Attachment(path));

            var port = settings?.SmtpPort ?? 587;
            if (port <= 0) port = 587;

            using var client = new SmtpClient(host, port)
            {
                EnableSsl = true,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                UseDefaultCredentials = false,
                Credentials = new NetworkCredential(user, pass)
            };

            client.Send(msg);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string ResolveImageMimeType(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "jpg" or "jpeg" => MediaTypeNames.Image.Jpeg,
            "gif" => MediaTypeNames.Image.Gif,
            "bmp" => "image/bmp",
            _ => MediaTypeNames.Image.Png
        };
    }

    private static bool CanUseAsSmtpFrom(string preferredSender, string configuredFrom, string smtpUser)
    {
        if (string.IsNullOrWhiteSpace(preferredSender))
            return false;

        return SameAddress(preferredSender, configuredFrom)
               || SameAddress(preferredSender, smtpUser);
    }

    private static bool SameAddress(string? a, string? b)
    {
        try
        {
            var aa = string.IsNullOrWhiteSpace(a) ? "" : new MailAddress(a.Trim()).Address;
            var bb = string.IsNullOrWhiteSpace(b) ? "" : new MailAddress(b.Trim()).Address;
            return !string.IsNullOrWhiteSpace(aa) &&
                   string.Equals(aa, bb, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string BuildPlainTextFallback(string? html)
    {
        var text = html ?? "";
        text = Regex.Replace(text, @"<\s*br\s*/?\s*>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</\s*p\s*>", "\n\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", "");
        text = WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"[ \t]+\n", "\n");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }
}

