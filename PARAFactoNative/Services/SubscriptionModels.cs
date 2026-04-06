using System.Text.Json.Serialization;

namespace PARAFactoNative.Services;

/// <summary>
/// Fichier à côté de l'exécutable (déployé par l'installateur) : URL de l'API et page de paiement.
/// </summary>
public sealed class SubscriptionConfigFile
{
    [JsonPropertyName("licenseCheckApiUrl")]
    public string? LicenseCheckApiUrl { get; set; }

    [JsonPropertyName("paymentPageUrl")]
    public string? PaymentPageUrl { get; set; }

    /// <summary>Si true, aucune vérification (développement / support).</summary>
    [JsonPropertyName("skipValidation")]
    public bool SkipValidation { get; set; }

    /// <summary>Jours avant la fin d'accès pour afficher le rappel de renouvellement (défaut 7).</summary>
    [JsonPropertyName("renewalWarningDays")]
    public int RenewalWarningDays { get; set; } = 7;
}

/// <summary>
/// État utilisateur dans %LocalAppData%\PARAFactoNative\subscription_account.json
/// </summary>
public sealed class SubscriptionAccountFile
{
    [JsonPropertyName("stripeCustomerId")]
    public string? StripeCustomerId { get; set; }

    /// <summary>Dernier accès connu (UTC ISO) renvoyé par le serveur.</summary>
    [JsonPropertyName("cachedAccessUntilUtc")]
    public string? CachedAccessUntilUtc { get; set; }

    [JsonPropertyName("lastSuccessfulCheckUtc")]
    public string? LastSuccessfulCheckUtc { get; set; }

    /// <summary>Date (yyyy-MM-dd, UTC) du dernier affichage du rappel renouvellement.</summary>
    [JsonPropertyName("lastRenewalWarningDayUtc")]
    public string? LastRenewalWarningDayUtc { get; set; }
}

public sealed class LicenseCheckResponseDto
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("accessUntil")]
    public string? AccessUntil { get; set; }

    [JsonPropertyName("hasActiveSubscription")]
    public bool HasActiveSubscription { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}
