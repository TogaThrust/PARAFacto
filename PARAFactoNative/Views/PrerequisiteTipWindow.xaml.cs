using System.Diagnostics;
using System.Windows;
using PARAFactoNative.Services;

namespace PARAFactoNative.Views;

public partial class PrerequisiteTipWindow : Window
{
    public PrerequisiteTipWindow(Window owner, string bodyText)
    {
        InitializeComponent();
        Owner = owner;
        BodyText.Text = bodyText;
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
        DialogResult = true;
        Close();
    }
}
