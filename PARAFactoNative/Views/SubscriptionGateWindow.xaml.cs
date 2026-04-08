using System.Windows;
using PARAFactoNative.Services;

namespace PARAFactoNative.Views;

public partial class SubscriptionGateWindow : Window
{
    public bool SaveRequested { get; private set; }
    public bool RetryRequested { get; private set; }

    private readonly string? _paymentPageUrl;

    private SubscriptionGateWindow(string title, string body, string? paymentPageUrl, string? existingCustomerId)
    {
        InitializeComponent();
        _paymentPageUrl = paymentPageUrl;
        TitleText.Text = title;
        BodyText.Text = body;
        if (!string.IsNullOrWhiteSpace(existingCustomerId))
            CustomerIdPasswordBox.Password = existingCustomerId.Trim();

        if (string.IsNullOrWhiteSpace(paymentPageUrl))
            PaymentButton.Visibility = Visibility.Collapsed;

        SaveButton.Content = "Enregistrer et vérifier";
        QuitButton.Content = "Retour";
    }

    public static (bool SaveRequested, bool RetryRequested) ShowSetup(
        Window? owner,
        string? paymentPageUrl,
        string? prefilledCustomerId = null,
        string? titleOverride = null,
        string? bodyOverride = null)
    {
        var w = new SubscriptionGateWindow(
            titleOverride ?? "Configurer l'abonnement",
            bodyOverride ?? (
                "Après souscription sur le site, vous recevez un identifiant (cus_…). Collez-le dans le champ masqué ci-dessous. " +
                "Chaque abonnement n'est utilisable que sur un seul ordinateur ; le lien avec cet appareil est enregistré côté serveur PARAFacto / Stripe."),
            paymentPageUrl,
            prefilledCustomerId);
        if (owner != null)
            w.Owner = owner;
        w.ShowDialog();
        return (w.SaveRequested, w.RetryRequested);
    }

    private void Payment_OnClick(object sender, RoutedEventArgs e)
    {
        SubscriptionVerificationService.OpenPaymentPage(_paymentPageUrl);
    }

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        var id = CustomerIdPasswordBox.Password?.Trim() ?? "";
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
