using System.IO;

namespace PARAFactoNative.Services;

/// <summary>
/// Versions des documents juridiques livrés avec l'application.
/// À incrémenter à chaque modification substantielle du texte (nouvelle acceptation requise).
/// </summary>
public static class LegalDocuments
{
    public const string PrivacyPolicyVersion = "2026-04-08";
    public const string TermsOfServiceVersion = "2026-04-08";

    public const string PrivacyFileName = "PolitiqueConfidentialite.txt";
    public const string TermsFileName = "ConditionsUtilisation.txt";

    public static string PrivacyPath(string baseDirectory)
        => Path.Combine(baseDirectory, "Legal", PrivacyFileName);

    public static string TermsPath(string baseDirectory)
        => Path.Combine(baseDirectory, "Legal", TermsFileName);
}
