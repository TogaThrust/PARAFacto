using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace PARAFactoNative.Services;

public sealed class OutlookEmailService
{
    public bool TrySendMailWithAttachments(
        string? to,
        string subject,
        string body,
        IEnumerable<string> attachments,
        out string error)
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
                error = "Outlook n'est pas installé (ProgID Outlook.Application introuvable).";
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
            SetComProperty(mail, "Body", body ?? "");

            var atts = GetComProperty(mail, "Attachments");
            if (atts is null)
            {
                error = "Impossible d'accéder aux pièces jointes Outlook.";
                return false;
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
}

