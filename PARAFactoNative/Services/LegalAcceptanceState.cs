using System.Security.Cryptography;
using System.Text;

namespace PARAFactoNative.Services;

public readonly struct LegalAcceptanceState
{
    public DateTimeOffset? PrivacyAcceptedAtUtc { get; init; }
    public DateTimeOffset? TermsAcceptedAtUtc { get; init; }
    public string? PrivacyDocVersionAccepted { get; init; }
    public string? TermsDocVersionAccepted { get; init; }
    public string? PrivacyContentSha256Accepted { get; init; }
    public string? TermsContentSha256Accepted { get; init; }

    public bool IsCompleteForCurrentDocuments(string privacyFullText, string termsFullText)
    {
        if (PrivacyAcceptedAtUtc is null || TermsAcceptedAtUtc is null)
            return false;

        // Compat: si hashes absents (ancien settings), on retombe sur la logique historique par version.
        if (string.IsNullOrWhiteSpace(PrivacyContentSha256Accepted) || string.IsNullOrWhiteSpace(TermsContentSha256Accepted))
        {
            return string.Equals(PrivacyDocVersionAccepted, LegalDocuments.PrivacyPolicyVersion, StringComparison.Ordinal)
                   && string.Equals(TermsDocVersionAccepted, LegalDocuments.TermsOfServiceVersion, StringComparison.Ordinal);
        }

        var privacyHashNow = Sha256HexUtf8(privacyFullText);
        var termsHashNow = Sha256HexUtf8(termsFullText);
        if (!string.Equals(PrivacyContentSha256Accepted, privacyHashNow, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.Equals(TermsContentSha256Accepted, termsHashNow, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    /// <summary>
    /// Une acceptation a déjà été enregistrée, mais les versions livrées dans l'application ont changé
    /// (constantes <see cref="LegalDocuments"/>). L'utilisateur doit accepter à nouveau, comme au premier lancement.
    /// </summary>
    public bool IsReacceptanceRequiredDueToNewLegalDocumentVersions(string privacyFullText, string termsFullText)
    {
        if (PrivacyAcceptedAtUtc is null || TermsAcceptedAtUtc is null)
            return false;
        return !IsCompleteForCurrentDocuments(privacyFullText, termsFullText);
    }

    private static string Sha256HexUtf8(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text ?? "");
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}
