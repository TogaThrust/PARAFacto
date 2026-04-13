namespace PARAFactoNative.Services;

public readonly struct LegalAcceptanceState
{
    public DateTimeOffset? PrivacyAcceptedAtUtc { get; init; }
    public DateTimeOffset? TermsAcceptedAtUtc { get; init; }
    public string? PrivacyDocVersionAccepted { get; init; }
    public string? TermsDocVersionAccepted { get; init; }
    public string? PrivacyContentSha256Accepted { get; init; }
    public string? TermsContentSha256Accepted { get; init; }

    /// <summary>
    /// Référence : numéros de version des documents livrés avec l'app (<see cref="LegalDocuments"/>).
    /// Le contenu affiché varie selon la langue (fichiers distincts) : comparer les empreintes du texte
    /// provoquait une nouvelle demande d'acceptation à chaque changement de langue.
    /// Les empreintes enregistrées restent dans settings.json / audit comme preuve du texte lu lors de l'acceptation.
    /// </summary>
    public bool IsCompleteForCurrentDocuments()
    {
        if (PrivacyAcceptedAtUtc is null || TermsAcceptedAtUtc is null)
            return false;

        return string.Equals(PrivacyDocVersionAccepted, LegalDocuments.PrivacyPolicyVersion, StringComparison.Ordinal)
               && string.Equals(TermsDocVersionAccepted, LegalDocuments.TermsOfServiceVersion, StringComparison.Ordinal);
    }

    /// <summary>
    /// Une acceptation a déjà été enregistrée, mais les versions livrées dans l'application ont changé
    /// (constantes <see cref="LegalDocuments"/>). L'utilisateur doit accepter à nouveau, comme au premier lancement.
    /// </summary>
    public bool IsReacceptanceRequiredDueToNewLegalDocumentVersions()
    {
        if (PrivacyAcceptedAtUtc is null || TermsAcceptedAtUtc is null)
            return false;
        return !IsCompleteForCurrentDocuments();
    }
}
