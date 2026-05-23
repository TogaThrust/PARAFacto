using System;
using System.IO;
using Microsoft.Win32;

namespace PARAFactoNative.Services;

/// <summary>Détection locale Acrobat Reader et navigateur compatible Gmail.</summary>
public static class DesktopPrerequisiteAdvisor
{
    public const string GmailHelpUrl = "https://mail.google.com/";
    public const string ChromeDownloadUrl = "https://www.google.com/intl/fr/chrome/";

    public const string AdobeReaderDownloadUrl = "https://www.adobe.com/be_fr/acrobat/pdf-reader.html";

    public static bool IsAcrobatReaderInstalled()
    {
        return AppPathExeExists("AcroRd32.exe")
               || AppPathExeExists("AcroRd64.exe")
               || AppPathExeExists("Acrobat.exe")
               || KnownAdobeExeExists();
    }

    public static bool IsGmailBrowserAvailable()
    {
        try
        {
            return AppPathExeExists("chrome.exe")
                   || AppPathExeExists("msedge.exe")
                   || AppPathExeExists("firefox.exe")
                   || AppPathExeExists("brave.exe");
        }
        catch
        {
            return false;
        }
    }

    public static string BuildPrerequisiteMessage(bool readerOk, bool gmailBrowserOk)
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
        var gmailLine = gmailBrowserOk
            ? T(
                "• Gmail web / navigateur : navigateur détecté.",
                "• Gmail web / browser: browser detected.",
                "• Gmail web / browser: browser gedetecteerd.")
            : T(
                "• Gmail web / navigateur : aucun navigateur compatible détecté. Installez Google Chrome ou connectez-vous à Gmail dans votre navigateur.",
                "• Gmail web / browser: no compatible browser detected. Install Google Chrome or sign in to Gmail in your browser.",
                "• Gmail web / browser: geen compatibele browser gedetecteerd. Installeer Google Chrome of meld u aan bij Gmail in uw browser.");

        return
            T(
                "PARAFacto s’appuie sur ces logiciels pour les PDF et prépare les rappels e-mail dans Gmail web.",
                "PARAFacto relies on these applications for PDFs and prepares email reminders in Gmail web.",
                "PARAFacto gebruikt deze software voor pdf's en bereidt e-mailherinneringen voor in Gmail web.")
            + "\n\n" +
            readerLine + "\n" +
            gmailLine +
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
