using System;
using System.Collections.Generic;

namespace PARAFactoNative.Services;

public sealed class EmailDispatchService
{
    private readonly OutlookEmailService _outlook = new();
    private readonly SmtpEmailService _smtp = new();

    public bool TrySend(
        AppMailSettings settings,
        string subject,
        string body,
        IEnumerable<string> attachments,
        out string error)
    {
        error = "";

        // 1) Essayer Outlook (si installé)
        if (_outlook.TrySendMailWithAttachments(settings.RecipientEmail, subject, body, attachments, out var outlookErr))
            return true;

        // 2) Fallback SMTP si activé
        if (settings.UseSmtp)
        {
            if (_smtp.TrySendMailWithAttachments(settings, subject, body, attachments, out var smtpErr))
                return true;

            error = $"Outlook: {outlookErr}\n\nSMTP: {smtpErr}";
            return false;
        }

        error = outlookErr;
        return false;
    }
}

