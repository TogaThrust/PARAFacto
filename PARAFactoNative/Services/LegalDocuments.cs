using System.IO;

namespace PARAFactoNative.Services;

/// <summary>
/// Versions des documents juridiques livrés avec l'application.
/// À chaque modification des fichiers <see cref="PrivacyFileName"/> ou <see cref="TermsFileName"/>,
/// incrémenter la constante correspondante : l'app exigera une nouvelle acceptation (overlay bloquant)
/// tant que l'utilisateur n'aura pas validé les textes courants dans « Données techniques ».
/// </summary>
public static class LegalDocuments
{
    public const string PrivacyPolicyVersion = "2026-04-08-02";
    public const string TermsOfServiceVersion = "2026-04-08-02";

    public const string PrivacyFileName = "PolitiqueConfidentialite.txt";
    public const string TermsFileName = "ConditionsUtilisation.txt";

    public static string PrivacyPath(string baseDirectory)
        => Path.Combine(baseDirectory, "Legal", PrivacyFileName);

    public static string TermsPath(string baseDirectory)
        => Path.Combine(baseDirectory, "Legal", TermsFileName);
}
