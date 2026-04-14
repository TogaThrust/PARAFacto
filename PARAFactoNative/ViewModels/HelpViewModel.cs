using System;
using System.Diagnostics;
using System.Windows;
using PARAFactoNative.Services;

namespace PARAFactoNative.ViewModels;

/// <summary>
/// Actions de l’onglet Aide (e-mail, liens externes).
/// </summary>
public sealed class HelpViewModel
{
    public const string HelpdeskEmail = "helpdesk@parafacto.be";

    /// <summary>Site PARAFacto (tutoriels / pages publiques). À remplacer par une URL /videos si elle existe.</summary>
    public const string HelpVideosBaseUrl = "https://parafacto.be/";

    public RelayCommand OpenHelpdeskEmailCommand { get; }
    public RelayCommand OpenHelpVideosCommand { get; }

    public HelpViewModel()
    {
        OpenHelpdeskEmailCommand = new RelayCommand(OpenHelpdeskEmail);
        OpenHelpVideosCommand = new RelayCommand(OpenHelpVideos);
    }

    private static void OpenHelpVideos()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = HelpVideosBaseUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "PARAFacto", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void OpenHelpdeskEmail()
    {
        var (subject, body) = UiLanguageService.Current switch
        {
            UiLanguageService.En => (
                "PARAFacto Native — Support request",
                "Hello,\n\n"),
            UiLanguageService.Nl => (
                "PARAFacto Native — Ondersteuningsverzoek",
                "Dag,\n\n"),
            _ => (
                "PARAFacto Native — Demande d’assistance",
                "Bonjour,\n\n")
        };

        var mailto =
            $"mailto:{HelpdeskEmail}?subject={Uri.EscapeDataString(subject)}&body={Uri.EscapeDataString(body)}";

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = mailto,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "PARAFacto", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
