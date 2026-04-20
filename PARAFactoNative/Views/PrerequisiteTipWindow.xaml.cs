using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows;
using PARAFactoNative.Services;

namespace PARAFactoNative.Views;

public partial class PrerequisiteTipWindow : Window
{
    private readonly AppSettingsStore _appSettings = new();
    private readonly bool _readerOk;
    private readonly bool _outlookOk;

    public bool DontShowOnNextUpdates { get; private set; }

    public PrerequisiteTipWindow(Window owner, bool readerOk, bool outlookOk)
    {
        InitializeComponent();
        Owner = owner;
        _readerOk = readerOk;
        _outlookOk = outlookOk;
        ApplyLanguage();
        UiLanguageService.LanguageChanged += OnUiLanguageChanged;
        Closed += (_, _) => UiLanguageService.LanguageChanged -= OnUiLanguageChanged;
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
            // ignore
        }
    }

    private void AdobeButton_OnClick(object sender, RoutedEventArgs e)
        => OpenUrl(DesktopPrerequisiteAdvisor.AdobeReaderDownloadUrl);

    private void OutlookButton_OnClick(object sender, RoutedEventArgs e)
        => OpenUrl(DesktopPrerequisiteAdvisor.OutlookClassicHelpUrl);

    private void OkButton_OnClick(object sender, RoutedEventArgs e)
    {
        DontShowOnNextUpdates = DontShowAgainCheckBox.IsChecked == true;
        DialogResult = true;
        Close();
    }

    private void OnUiLanguageChanged(string _)
        => Dispatcher.Invoke(ApplyLanguage);

    private void LangFrButton_OnClick(object sender, RoutedEventArgs e)
        => SetLanguage(UiLanguageService.Fr);

    private void LangEnButton_OnClick(object sender, RoutedEventArgs e)
        => SetLanguage(UiLanguageService.En);

    private void LangNlButton_OnClick(object sender, RoutedEventArgs e)
        => SetLanguage(UiLanguageService.Nl);

    private void SetLanguage(string code)
    {
        UiLanguageService.SetLanguage(code);
        _appSettings.SaveUiLanguage(code);
    }

    private void ApplyLanguage()
    {
        static string T(string fr, string en, string nl)
            => UiLanguageService.Current switch
            {
                UiLanguageService.En => en,
                UiLanguageService.Nl => nl,
                _ => fr
            };

        Title = T("PARAFacto — Logiciels recommandés", "PARAFacto — Recommended software", "PARAFacto — Aanbevolen software");
        LanguageLabel.Text = T("Langue :", "Language:", "Taal:");
        OpenInBrowserLabel.Text = T("Ouvrir dans le navigateur :", "Open in browser:", "Openen in browser:");
        AdobeButton.Content = "Acrobat Reader (Adobe)";
        OutlookButton.Content = T("Outlook classique (Microsoft)", "Outlook classic (Microsoft)", "Outlook klassiek (Microsoft)");
        DontShowAgainCheckBox.Content = T(
            "Ne plus afficher pour les prochaines mises à jour",
            "Do not show again for future updates",
            "Niet meer tonen bij volgende updates");
        BodyText.Text = DesktopPrerequisiteAdvisor.BuildPrerequisiteMessage(_readerOk, _outlookOk);

        UpdateLanguageButtonsVisual();
    }

    private void UpdateLanguageButtonsVisual()
    {
        void Apply(Button button, bool selected)
        {
            button.Opacity = selected ? 1.0 : 0.75;
            button.FontWeight = selected ? FontWeights.Bold : FontWeights.Normal;
            button.BorderBrush = selected ? Brushes.DodgerBlue : Brushes.LightGray;
            button.BorderThickness = selected ? new Thickness(2) : new Thickness(1);
            button.Background = selected ? new SolidColorBrush(Color.FromRgb(239, 246, 255)) : Brushes.White;
        }

        Apply(LangFrButton, UiLanguageService.Current == UiLanguageService.Fr);
        Apply(LangEnButton, UiLanguageService.Current == UiLanguageService.En);
        Apply(LangNlButton, UiLanguageService.Current == UiLanguageService.Nl);
    }
}
