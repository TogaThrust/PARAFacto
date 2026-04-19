using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using PARAFactoNative.Services;

namespace PARAFactoNative.Views;

public partial class SubscriptionGateWindow : Window
{
    public bool SaveRequested { get; private set; }
    public bool RetryRequested { get; private set; }

    private readonly string? _paymentPageUrl;

    private static void SetDefaultBodyInlines(TextBlock tb)
    {
        tb.Inlines.Clear();
        tb.Text = null;
        tb.Inlines.Add(new Run("Après souscription sur le site, vous recevrez un identifiant client (cus_…) "));
        var parEmail = new Run("PAR E-MAIL ");
        parEmail.FontWeight = FontWeights.Bold;
        tb.Inlines.Add(parEmail);
        tb.Inlines.Add(new Run("(consultez votre boîte de réception et vos spams). "));
        var restart = new Run("Fermez entièrement l'application puis rouvrez-la");
        restart.FontWeight = FontWeights.Bold;
        tb.Inlines.Add(restart);
        tb.Inlines.Add(new Run(
            ", puis collez l'identifiant dans le champ masqué ci-dessous. Chaque abonnement n'est utilisable que sur un seul ordinateur ; le lien avec cet appareil est enregistré côté serveur PARAFacto / Stripe."));
    }

    private SubscriptionGateWindow(string title, string? body, string? paymentPageUrl, string? existingCustomerId)
    {
        InitializeComponent();
        _paymentPageUrl = paymentPageUrl;
        TitleText.Text = title;
        if (!string.IsNullOrWhiteSpace(body))
            BodyText.Text = body.Trim();
        else
            SetDefaultBodyInlines(BodyText);
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
            bodyOverride,
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
