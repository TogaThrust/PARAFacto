using System;
using System.Collections.Generic;

namespace PARAFactoNative.Services;

public sealed class EmailDispatchService
{
    private readonly OutlookEmailService _outlook = new();
    private readonly SmtpEmailService _smtp = new();

    /// <summary>Serveur + identifiants renseignés (permet un secours SMTP si Outlook échoue ou est absent).</summary>
    private static bool IsSmtpComplete(AppMailSettings? s)
    {
        if (s is null) return false;
        if (string.IsNullOrWhiteSpace(s.SmtpHost)) return false;
        if (string.IsNullOrWhiteSpace(s.SmtpUsername)) return false;
        if (string.IsNullOrWhiteSpace(s.SmtpPassword)) return false;
        return true;
    }

    public bool TrySend(
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
        var smtpReady = IsSmtpComplete(settings);

        // Priorité SMTP (toujours activée) : SMTP d'abord, secours Outlook si besoin.
        if (smtpReady)
        {
            if (_smtp.TrySendMailWithAttachments(settings, subject, body, attachments, out var smtpErr, isHtml, inlineLogoPath, senderEmail))
                return true;

            if (_outlook.TrySendMailWithAttachments(settings.RecipientEmail, subject, body, attachments, out var outlookErr, isHtml, inlineLogoPath, senderEmail))
                return true;

            error = $"SMTP: {smtpErr}\n\nOutlook: {outlookErr}";
            return false;
        }

        if (_outlook.TrySendMailWithAttachments(settings.RecipientEmail, subject, body, attachments, out var outlookOnlyErr, isHtml, inlineLogoPath, senderEmail))
            return true;

        error =
            "SMTP incomplet : renseignez le serveur (ex. smtp.gmail.com), l’identifiant et le mot de passe (mot de passe d’application Gmail).\n\n" +
            $"Outlook: {outlookOnlyErr}";
        return false;
    }
}

