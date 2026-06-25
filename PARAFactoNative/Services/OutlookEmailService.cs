using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace PARAFactoNative.Services;

public sealed class OutlookEmailService
{
    /// <summary>Message affiché quand Outlook.Application n’est pas enregistré pour ce processus (nouvel Outlook, 32/64 bits, etc.).</summary>
    public const string OutlookComProgIdMissingHint =
        "Automation COM introuvable (ProgID Outlook.Application). L’application Outlook peut pourtant s’ouvrir à la main.\n\n" +
        "Vérifications courantes :\n" +
        "• Si vous utilisez le « Nouvel Outlook » (bascule en haut à droite), repassez sur l’Outlook classique : le nouvel Outlook n’expose pas la même automation COM.\n" +
        "• Office 32 bits + application 64 bits (ou l’inverse) : installez Office en 64 bits, ou contactez le support pour une version PARAFacto adaptée.\n" +
        "• Réparation rapide : Applications Windows → Microsoft 365 → Modifier → Réparation rapide.";

    public bool TrySendMailWithAttachments(
        string? to,
        string subject,
        string body,
        IEnumerable<string> attachments,
        out string error,
        bool isHtml = false,
        string? inlineLogoPath = null,
        string? senderEmail = null)
    {
        error = "";
        to = (to ?? "").Trim();
        if (string.IsNullOrWhiteSpace(to))
        {
            error = "Aucune adresse e-mail destinataire n'est renseignée.";
            return false;
        }

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

        Type? outlookType = null;
        object? outlook = null;
        object? mail = null;
        try
        {
            outlookType = Type.GetTypeFromProgID("Outlook.Application");
            if (outlookType is null)
            {
                error = OutlookComProgIdMissingHint;
                return false;
            }

            outlook = Activator.CreateInstance(outlookType);
            if (outlook is null)
            {
                error = "Impossible de démarrer Outlook.";
                return false;
            }

            // 0 = olMailItem
            mail = outlookType.InvokeMember("CreateItem",
                System.Reflection.BindingFlags.InvokeMethod,
                null,
                outlook,
                new object[] { 0 });

            if (mail is null)
            {
                error = "Impossible de créer un e-mail Outlook.";
                return false;
            }

            SetComProperty(mail, "To", to);
            SetComProperty(mail, "Subject", subject ?? "");
            SetComProperty(mail, isHtml ? "HTMLBody" : "Body", body ?? "");
            TryForceSender(outlook, mail, senderEmail);

            var atts = GetComProperty(mail, "Attachments");
            if (atts is null)
            {
                error = "Impossible d'accéder aux pièces jointes Outlook.";
                return false;
            }

            if (isHtml && !string.IsNullOrWhiteSpace(inlineLogoPath) && File.Exists(inlineLogoPath))
            {
                try
                {
                    var inlineLogo = atts.GetType().InvokeMember("Add",
                        System.Reflection.BindingFlags.InvokeMethod,
                        null,
                        atts,
                        new object[] { inlineLogoPath });
                    if (inlineLogo is null)
                        throw new InvalidOperationException("Logo inline Outlook introuvable.");
                    var propertyAccessor = GetComProperty(inlineLogo, "PropertyAccessor");
                    propertyAccessor?.GetType().InvokeMember("SetProperty",
                        System.Reflection.BindingFlags.InvokeMethod,
                        null,
                        propertyAccessor,
                        new object[] { "http://schemas.microsoft.com/mapi/proptag/0x3712001F", "parafacto-logo" });
                    propertyAccessor?.GetType().InvokeMember("SetProperty",
                        System.Reflection.BindingFlags.InvokeMethod,
                        null,
                        propertyAccessor,
                        new object[] { "http://schemas.microsoft.com/mapi/proptag/0x7FFE000B", true });
                }
                catch
                {
                    // Si l'attachement inline échoue, le mail part quand même sans logo.
                }
            }

            foreach (var path in att)
            {
                // Add(path)
                atts.GetType().InvokeMember("Add",
                    System.Reflection.BindingFlags.InvokeMethod,
                    null,
                    atts,
                    new object[] { path });
            }

            // Send()
            mail.GetType().InvokeMember("Send",
                System.Reflection.BindingFlags.InvokeMethod,
                null,
                mail,
                Array.Empty<object>());

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            try
            {
                if (mail != null && Marshal.IsComObject(mail)) Marshal.FinalReleaseComObject(mail);
            }
            catch { }
            try
            {
                if (outlook != null && Marshal.IsComObject(outlook)) Marshal.FinalReleaseComObject(outlook);
            }
            catch { }
        }
    }

    private static object? GetComProperty(object comObj, string name)
    {
        try
        {
            return comObj.GetType().InvokeMember(name,
                System.Reflection.BindingFlags.GetProperty,
                null,
                comObj,
                Array.Empty<object>());
        }
        catch
        {
            return null;
        }
    }

    private static void SetComProperty(object comObj, string name, object value)
    {
        comObj.GetType().InvokeMember(name,
            System.Reflection.BindingFlags.SetProperty,
            null,
            comObj,
            new[] { value });
    }

    private static void TryForceSender(object outlook, object mail, string? senderEmail)
    {
        senderEmail = (senderEmail ?? "").Trim();
        if (string.IsNullOrWhiteSpace(senderEmail))
            return;

        try
        {
            var session = GetComProperty(outlook, "Session");
            var accounts = session is null ? null : GetComProperty(session, "Accounts");
            var countObj = accounts is null ? null : GetComProperty(accounts, "Count");
            var count = Convert.ToInt32(countObj, CultureInfo.InvariantCulture);

            for (var i = 1; i <= count; i++)
            {
                var account = accounts!.GetType().InvokeMember(
                    "Item",
                    System.Reflection.BindingFlags.InvokeMethod,
                    null,
                    accounts,
                    new object[] { i });

                if (account is null)
                    continue;

                var smtpAddress = GetComProperty(account, "SmtpAddress")?.ToString();
                if (!string.Equals((smtpAddress ?? "").Trim(), senderEmail, StringComparison.OrdinalIgnoreCase))
                    continue;

                SetComProperty(mail, "SendUsingAccount", account);
                return;
            }
        }
        catch
        {
            // Selon le profil Outlook, l'accès aux comptes peut échouer; on tente alors le champ "De" délégué.
        }

        try
        {
            SetComProperty(mail, "SentOnBehalfOfName", senderEmail);
        }
        catch
        {
            // Outlook refusera à l'envoi si le compte ou la délégation n'est pas autorisé.
        }
    }
}

