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
        return AppPathExeExists("AcroRd32.exe")
               || AppPathExeExists("AcroRd64.exe")
               || AppPathExeExists("Acrobat.exe")
               || KnownAdobeExeExists();
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
        static string T(string fr, string en, string nl)
            => UiLanguageService.Current switch
            {
                UiLanguageService.En => en,
                UiLanguageService.Nl => nl,
                _ => fr
            };

        var readerLine = readerOk
            ? T(
                "• Adobe Acrobat / Reader : détecté.",
                "• Adobe Acrobat / Reader: detected.",
                "• Adobe Acrobat / Reader: gedetecteerd.")
            : T(
                "• Adobe Acrobat / Reader : non détecté (recommandé pour les PDF).",
                "• Adobe Acrobat / Reader: not detected (recommended for PDFs).",
                "• Adobe Acrobat / Reader: niet gedetecteerd (aanbevolen voor pdf's).");
        var outlookLine = outlookOk
            ? T(
                "• Microsoft Outlook (automation classique) : détecté.",
                "• Microsoft Outlook (classic automation): detected.",
                "• Microsoft Outlook (klassieke automatisering): gedetecteerd.")
            : T(
                "• Microsoft Outlook classique : absent ou automation COM indisponible. Le « Nouvel Outlook » ne suffit pas pour l’envoi automatique de mails depuis PARAFacto.",
                "• Microsoft Outlook classic: missing or COM automation unavailable. The New Outlook is not enough for automatic email sending from PARAFacto.",
                "• Microsoft Outlook klassiek: afwezig of COM-automatisering niet beschikbaar. De Nieuwe Outlook volstaat niet voor automatische e-mailverzending vanuit PARAFacto.");

        return
            T(
                "PARAFacto s’appuie sur ces logiciels pour les PDF et l’envoi de mails.",
                "PARAFacto relies on these applications for PDFs and email sending.",
                "PARAFacto gebruikt deze software voor pdf's en e-mailverzending.")
            + "\n\n" +
            readerLine + "\n" +
            outlookLine +
            "\n\n" +
            T(
                "Utilisez les boutons ci-dessous pour ouvrir les pages de téléchargement ou d’aide officielles.",
                "Use the buttons below to open the official download/help pages.",
                "Gebruik de knoppen hieronder om de officiele download-/hulppagina's te openen.")
            + "\n\n" +
            T(
                "Ce rappel s’affiche une fois après chaque mise à jour de PARAFacto (ou première ouverture de cette version).",
                "This reminder appears once after each PARAFacto update (or first launch of this version).",
                "Deze herinnering verschijnt een keer na elke PARAFacto-update (of bij de eerste start van deze versie).");
    }

    private static bool AppPathExeExists(string exeFileName)
    {
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var k = RegistryKey.OpenBaseKey(hive, view)
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
        }

        return false;
    }

    private static bool KnownAdobeExeExists()
    {
        try
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            return ProbeKnownAdobeRoot(pf) || ProbeKnownAdobeRoot(pfx86);
        }
        catch
        {
            return false;
        }
    }

    private static bool ProbeKnownAdobeRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return false;

        var readerDc = Path.Combine(root, "Adobe", "Acrobat DC", "Acrobat", "Acrobat.exe");
        var reader = Path.Combine(root, "Adobe", "Acrobat Reader", "Reader", "AcroRd32.exe");
        var readerDcLegacy = Path.Combine(root, "Adobe", "Acrobat Reader DC", "Reader", "AcroRd32.exe");
        return File.Exists(readerDc) || File.Exists(reader) || File.Exists(readerDcLegacy);
    }
}
