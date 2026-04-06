using System.Windows;
using PARAFactoNative.Services;

namespace PARAFactoNative.Views;

public partial class SubscriptionGateWindow : Window
{
    private readonly Mode _mode;
    public bool SaveRequested { get; private set; }
    public bool RetryRequested { get; private set; }

    private readonly string? _paymentPageUrl;

    private enum Mode
    {
        Setup,
        Blocked,
    }

    private SubscriptionGateWindow(Mode mode, string title, string body, string? paymentPageUrl, string? existingCustomerId)
    {
        InitializeComponent();
        _mode = mode;
        _paymentPageUrl = paymentPageUrl;
        TitleText.Text = title;
        BodyText.Text = body;
        CustomerIdBox.Text = existingCustomerId ?? "";

        if (string.IsNullOrWhiteSpace(paymentPageUrl))
            PaymentButton.Visibility = Visibility.Collapsed;

        if (mode == Mode.Setup)
        {
            SaveButton.Content = "Enregistrer";
            QuitButton.Content = "Quitter";
        }
        else
        {
            SaveButton.Content = "Enregistrer et revérifier";
            QuitButton.Content = "Quitter";
        }
    }

    public static (bool SaveRequested, bool RetryRequested) ShowSetup(Window? owner, string? paymentPageUrl)
    {
        var w = new SubscriptionGateWindow(
            Mode.Setup,
            "Configurer l'abonnement",
            "Après souscription sur le site, vous recevez un identifiant client Stripe (commençant par cus_). " +
            "Collez-le ci-dessous pour activer l'application sur cet ordinateur.",
            paymentPageUrl,
            null);
        if (owner != null)
            w.Owner = owner;
        w.ShowDialog();
        return (w.SaveRequested, w.RetryRequested);
    }

    public static (bool RetryRequested, bool SaveRequested) ShowBlocked(
        Window? owner,
        string message,
        string? paymentPageUrl,
        string? existingCustomerId)
    {
        var w = new SubscriptionGateWindow(
            Mode.Blocked,
            "Abonnement requis",
            message,
            paymentPageUrl,
            existingCustomerId);
        if (owner != null)
            w.Owner = owner;
        w.ShowDialog();
        return (w.RetryRequested, w.SaveRequested);
    }

    private void Payment_OnClick(object sender, RoutedEventArgs e)
    {
        SubscriptionVerificationService.OpenPaymentPage(_paymentPageUrl);
    }

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        var id = CustomerIdBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(id) || !id.StartsWith("cus_", StringComparison.Ordinal))
        {
            MessageBox.Show(
                this,
                "L'identifiant doit commencer par cus_ (identifiant client Stripe).",
                "Abonnement",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var account = SubscriptionConfigLoader.LoadAccount();
        account.StripeCustomerId = id;
        SubscriptionConfigLoader.SaveAccount(account);

        SaveRequested = true;
        RetryRequested = true;
        DialogResult = true;
        Close();
    }

    private void Quit_OnClick(object sender, RoutedEventArgs e)
    {
        SaveRequested = false;
        RetryRequested = false;
        DialogResult = false;
        Close();
    }
}
