using System;
using System.IO;
using Microsoft.Win32;

namespace PARAFactoNative.Services;

/// <summary>Détection locale Acrobat Reader et automation Outlook (alignée sur l’installateur Inno).</summary>
public static class DesktopPrerequisiteAdvisor
{
    public const string OutlookClassicHelpUrl =
        "https://support.microsoft.com/fr-fr/office/installer-ou-r%C3%A9installer-outlook-classique-sur-un-pc-windows-5c94902b-31a5-4274-abb0-b07f4661edf5";

    public const string AdobeReaderDownloadUrl = "https://www.adobe.com/be_fr/acrobat/pdf-reader.html";

    public static bool IsAcrobatReaderInstalled()
    {
        return AppPathExeExists("AcroRd32.exe") || AppPathExeExists("AcroRd64.exe");
    }

    public static bool IsOutlookAutomationAvailable()
    {
        try
        {
            return Type.GetTypeFromProgID("Outlook.Application") is not null;
        }
        catch
        {
            return false;
        }
    }

    public static string BuildPrerequisiteMessage(bool readerOk, bool outlookOk)
    {
        var readerLine = readerOk
            ? "• Adobe Acrobat Reader : détecté."
            : "• Adobe Acrobat Reader : non détecté (recommandé pour les PDF).";
        var outlookLine = outlookOk
            ? "• Microsoft Outlook (automation classique) : détecté."
            : "• Microsoft Outlook classique : absent ou automation COM indisponible. Le « Nouvel Outlook » ne suffit pas pour l’envoi automatique de mails depuis PARAFacto.";

        return
            "PARAFacto s’appuie sur ces logiciels pour les PDF et l’envoi de mails.\n\n" +
            readerLine + "\n" +
            outlookLine +
            "\n\nUtilisez les boutons ci-dessous pour ouvrir les pages de téléchargement ou d’aide officielles.\n\n" +
            "Ce rappel s’affiche une fois après chaque mise à jour de PARAFacto (ou première ouverture de cette version).";
    }

    private static bool AppPathExeExists(string exeFileName)
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view)
                    .OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exeFileName);
                var p = (k?.GetValue("") as string)?.Trim().Trim('"');
                if (!string.IsNullOrWhiteSpace(p) && File.Exists(p))
                    return true;
            }
            catch
            {
                // ignore
            }
        }

        return false;
    }
}
