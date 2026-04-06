using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using PARAFactoNative.Views;

namespace PARAFactoNative.Services;

/// <summary>
/// Vérifie l'abonnement au démarrage (API Netlify + Stripe côté serveur).
/// </summary>
public static class SubscriptionVerificationService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(25),
    };

    /// <summary>
    /// Retourne false si l'application doit quitter (utilisateur ou abonnement invalide).
    /// </summary>
    public static bool RunStartupGate(Window? owner = null)
    {
        if (ShouldSkipValidation())
            return true;

        var config = SubscriptionConfigLoader.LoadMergedConfig();
        if (config.SkipValidation)
            return true;

        var apiUrl = config.LicenseCheckApiUrl?.Trim();
        if (string.IsNullOrEmpty(apiUrl))
        {
#if DEBUG
            return true;
#else
            MessageBox.Show(
                owner,
                "La configuration d'abonnement est incomplète (fichier subscription_config.json : licenseCheckApiUrl). Contactez votre distributeur.",
                "PARAFacto Native — Abonnement",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
#endif
        }

        var paymentUrl = string.IsNullOrWhiteSpace(config.PaymentPageUrl) ? null : config.PaymentPageUrl.Trim();
        var warningDays = config.RenewalWarningDays > 0 ? config.RenewalWarningDays : 7;

        while (true)
        {
            var account = SubscriptionConfigLoader.LoadAccount();
            var customerId = account.StripeCustomerId?.Trim();

            if (string.IsNullOrEmpty(customerId))
            {
                var setup = SubscriptionGateWindow.ShowSetup(owner, paymentUrl);
                if (!setup.SaveRequested)
                    return false;
                continue;
            }

            var result = VerifyOnlineAsync(apiUrl, customerId, account).GetAwaiter().GetResult();

            if (result.Status == VerifyStatus.Allowed)
            {
                MaybeShowRenewalWarning(owner, account, result.AccessUntilUtc, warningDays);
                return true;
            }

            if (result.Status == VerifyStatus.AllowedOfflineCache)
            {
                MaybeShowRenewalWarning(owner, account, result.AccessUntilUtc, warningDays);
                return true;
            }

            var gate = SubscriptionGateWindow.ShowBlocked(
                owner,
                result.UserMessage ?? "Votre abonnement n'est pas actif ou n'a pas été renouvelé.",
                paymentUrl,
                customerId);
            if (!gate.RetryRequested)
                return false;
        }
    }

    private static bool ShouldSkipValidation()
    {
        var env = Environment.GetEnvironmentVariable("PARAFACTO_SKIP_SUBSCRIPTION");
        if (string.Equals(env, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(env, "true", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    private static void MaybeShowRenewalWarning(
        Window? owner,
        SubscriptionAccountFile account,
        DateTime? accessUntilUtc,
        int warningDays)
    {
        if (accessUntilUtc == null)
            return;

        var now = DateTime.UtcNow;
        if (accessUntilUtc.Value <= now)
            return;

        var remaining = accessUntilUtc.Value - now;
        if (remaining.TotalDays > warningDays)
            return;

        var todayKey = now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (string.Equals(account.LastRenewalWarningDayUtc, todayKey, StringComparison.Ordinal))
            return;

        account.LastRenewalWarningDayUtc = todayKey;
        SubscriptionConfigLoader.SaveAccount(account);

        var endLocal = accessUntilUtc.Value.ToLocalTime();
        MessageBox.Show(
            owner,
            $"Votre période d'abonnement se termine le {endLocal:dd/MM/yyyy HH:mm}.\n\n" +
            "Pensez à valider le paiement du mois à venir (carte, portail client Stripe) pour éviter toute interruption.",
            "PARAFacto Native — Renouvellement",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private static async Task<VerifyOutcome> VerifyOnlineAsync(string apiUrl, string customerId, SubscriptionAccountFile account)
    {
        var deviceId = BuildDeviceId();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, apiUrl);
            req.Content = JsonContent.Create(new { customerId, deviceId });

            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            LicenseCheckResponseDto? dto = null;
            try
            {
                dto = System.Text.Json.JsonSerializer.Deserialize<LicenseCheckResponseDto>(
                    body,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch
            {
                // ignore parse error
            }

            if (dto == null)
            {
                return TryFallbackCache(account, "Réponse serveur invalide.");
            }

            DateTime? accessUntil = null;
            if (!string.IsNullOrWhiteSpace(dto.AccessUntil)
                && DateTime.TryParse(dto.AccessUntil, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var parsed))
            {
                accessUntil = parsed;
            }

            if (dto.Ok && accessUntil.HasValue && accessUntil.Value > DateTime.UtcNow)
            {
                account.CachedAccessUntilUtc = accessUntil.Value.ToString("O");
                account.LastSuccessfulCheckUtc = DateTime.UtcNow.ToString("O");
                SubscriptionConfigLoader.SaveAccount(account);
                return new VerifyOutcome(VerifyStatus.Allowed, accessUntil, null);
            }

            return new VerifyOutcome(
                VerifyStatus.Blocked,
                accessUntil,
                "L'abonnement n'est pas à jour ou aucune période payée n'est active. Régularisez le paiement pour continuer.");
        }
        catch (Exception)
        {
            return TryFallbackCache(account, null);
        }
    }

    private static VerifyOutcome TryFallbackCache(SubscriptionAccountFile account, string? serverHint)
    {
        if (string.IsNullOrWhiteSpace(account.CachedAccessUntilUtc))
        {
            return new VerifyOutcome(
                VerifyStatus.Blocked,
                null,
                serverHint ?? "Impossible de joindre le serveur de licence. Vérifiez votre connexion Internet.");
        }

        if (!DateTime.TryParse(
                account.CachedAccessUntilUtc,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var cachedEnd))
        {
            return new VerifyOutcome(
                VerifyStatus.Blocked,
                null,
                serverHint ?? "Erreur de cache local. Connexion requise.");
        }

        if (cachedEnd <= DateTime.UtcNow)
        {
            return new VerifyOutcome(
                VerifyStatus.Blocked,
                cachedEnd,
                "La période enregistrée est expirée et le serveur est injoignable. Connectez-vous à Internet puis réessayez.");
        }

        if (!DateTime.TryParse(
                account.LastSuccessfulCheckUtc,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var lastOk)
            || DateTime.UtcNow - lastOk > TimeSpan.FromHours(72))
        {
            return new VerifyOutcome(
                VerifyStatus.Blocked,
                cachedEnd,
                "Connexion au serveur de licence impossible depuis trop longtemps. Vérifiez Internet puis réessayez.");
        }

        return new VerifyOutcome(VerifyStatus.AllowedOfflineCache, cachedEnd, null);
    }

    private static string BuildDeviceId()
    {
        try
        {
            var raw = $"{Environment.MachineName}|{Environment.UserName}|{Environment.OSVersion.VersionString}";
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
            return Convert.ToHexString(bytes.AsSpan(0, 16));
        }
        catch
        {
            return "unknown";
        }
    }

    private enum VerifyStatus
    {
        Allowed,
        AllowedOfflineCache,
        Blocked,
    }

    private sealed record VerifyOutcome(VerifyStatus Status, DateTime? AccessUntilUtc, string? UserMessage);

    public static void OpenPaymentPage(string? paymentPageUrl)
    {
        var url = paymentPageUrl;
        if (string.IsNullOrWhiteSpace(url))
            url = SubscriptionConfigLoader.LoadMergedConfig().PaymentPageUrl;
        if (string.IsNullOrWhiteSpace(url))
            return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            // ignore
        }
    }
}
