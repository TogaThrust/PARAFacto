using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using PARAFactoNative.Views;
using PARAFactoNative.Services;

namespace PARAFactoNative.ViewModels;

/// <summary>
/// Actions de l’onglet Aide (e-mail, liens externes).
/// </summary>
public sealed class HelpViewModel
{
    public const string HelpdeskEmail = "helpdesk@parafacto.be";

    private const string HelpVideosFolder = @"C:\Users\togat\Desktop\PARAFACTO\HELP VIDEO";
    private const string AccountVideoFileName = "ACCEDER A SON COMPTE CLIENT.mp4";
    private const string InstallVideoFileName = "INSTALLER UNE NOUVELLE VERSION DE L'APP.mp4";

    public string AccountVideoPath { get; } = Path.Combine(HelpVideosFolder, AccountVideoFileName);
    public string InstallVideoPath { get; } = Path.Combine(HelpVideosFolder, InstallVideoFileName);
    public ImageSource AccountVideoIcon { get; }
    public ImageSource InstallVideoIcon { get; }

    public RelayCommand OpenHelpdeskEmailCommand { get; }
    public RelayCommand OpenAccountVideoCommand { get; }
    public RelayCommand OpenInstallVideoCommand { get; }

    public HelpViewModel()
    {
        AccountVideoIcon = CreateVideoIcon("#1D4ED8");
        InstallVideoIcon = CreateVideoIcon("#7C3AED");

        OpenHelpdeskEmailCommand = new RelayCommand(OpenHelpdeskEmail);
        OpenAccountVideoCommand = new RelayCommand(() => OpenVideo(AccountVideoPath));
        OpenInstallVideoCommand = new RelayCommand(() => OpenVideo(InstallVideoPath));
    }

    private static ImageSource CreateVideoIcon(string accentColorHex)
    {
        var drawing = new GeometryDrawing(
            new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(accentColorHex)),
            null,
            Geometry.Parse("M 2,2 L 2,38 L 38,20 Z"));

        drawing.Freeze();
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    private static void OpenVideo(string videoPath)
    {
        if (!File.Exists(videoPath))
        {
            MessageBox.Show($"Vidéo introuvable :\n{videoPath}", "PARAFacto", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var player = new VideoPlayerWindow(videoPath)
            {
                Owner = Application.Current?.MainWindow
            };

            player.Show();
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
                "PARAFacto — Support request",
                "Hello,\n\n"),
            UiLanguageService.Nl => (
                "PARAFacto — Ondersteuningsverzoek",
                "Dag,\n\n"),
            _ => (
                "PARAFacto — Demande d’assistance",
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
