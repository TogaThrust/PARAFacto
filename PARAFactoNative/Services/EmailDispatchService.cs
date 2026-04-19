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
        out string error)
    {
        error = "";
        var smtpReady = IsSmtpComplete(settings);

        // Case cochée : l'utilisateur veut passer par SMTP (Outlook absent, Gmail, ou Outlook HS).
        if (settings.UseSmtp)
        {
            if (smtpReady)
            {
                if (_smtp.TrySendMailWithAttachments(settings, subject, body, attachments, out var smtpErr))
                    return true;

                if (_outlook.TrySendMailWithAttachments(settings.RecipientEmail, subject, body, attachments, out var outlookErr))
                    return true;

                error = $"SMTP: {smtpErr}\n\nOutlook: {outlookErr}";
                return false;
            }

            if (_outlook.TrySendMailWithAttachments(settings.RecipientEmail, subject, body, attachments, out var outlookOnlyErr))
                return true;

            error =
                "SMTP incomplet : renseignez le serveur (ex. smtp.gmail.com), l’identifiant et le mot de passe (mot de passe d’application Gmail).\n\n" +
                $"Outlook: {outlookOnlyErr}";
            return false;
        }

        // Par défaut : Outlook d’abord, puis SMTP si configuré (poste sans Outlook ou erreur COM).
        if (_outlook.TrySendMailWithAttachments(settings.RecipientEmail, subject, body, attachments, out var outlookErr2))
            return true;

        if (smtpReady)
        {
            if (_smtp.TrySendMailWithAttachments(settings, subject, body, attachments, out var smtpErr2))
                return true;

            error = $"Outlook: {outlookErr2}\n\nSMTP: {smtpErr2}";
            return false;
        }

        error = outlookErr2 +
                "\n\nAstuce : configurez SMTP (onglet Données techniques) ou cochez « Utiliser SMTP » avec un compte Gmail (mot de passe d’application).";
        return false;
    }
}

