namespace PARAFactoNative.Services;

public readonly struct LegalAcceptanceState
{
    public DateTimeOffset? PrivacyAcceptedAtUtc { get; init; }
    public DateTimeOffset? TermsAcceptedAtUtc { get; init; }
    public string? PrivacyDocVersionAccepted { get; init; }
    public string? TermsDocVersionAccepted { get; init; }

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
