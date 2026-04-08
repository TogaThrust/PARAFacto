using System.IO;
using System.Text;
using System.Windows;
using PARAFactoNative.Services;

namespace PARAFactoNative.ViewModels;

public sealed class LegalComplianceViewModel : NotifyBase
{
    /// <summary>Index de l'onglet « Données techniques » (doit rester aligné sur l’ordre des TabItem dans MainWindow.xaml).</summary>
    public const int TechnicalTabIndex = 6;

    private readonly AppSettingsStore _store;
    private readonly string _baseDir;
    private int _mainTabSelectedIndex;

    private string _privacyText = "";
    private string _termsText = "";
    private bool _acceptPrivacy;
    private bool _acceptTerms;
    private bool _showBlockingOverlay = true;
    private string _acceptanceSummary = "";
    private string _documentsLoadError = "";
    private string _legalGateMessage = "";

    public LegalComplianceViewModel(AppSettingsStore store, string applicationBaseDirectory)
    {
        _store = store ?? new AppSettingsStore();
        _baseDir = applicationBaseDirectory ?? "";

        OpenTechnicalTabCommand = new RelayCommand(() => RequestOpenTechnicalTab?.Invoke());
        SaveAcceptanceCommand = new RelayCommand(SaveAcceptance, CanSaveAcceptance);

        try
        {
            LoadDocuments();
            ReloadAcceptanceUi();
        }
        catch (Exception ex)
        {
            LogComplianceInitFailure(ex);
            ApplyComplianceInitFailure(ex);
        }
    }

    private static string LegalInitErrorLogPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PARAFactoNative", "legal_init_error.txt");

    private static void LogComplianceInitFailure(Exception ex)
    {
        try
        {
            var dir = Path.GetDirectoryName(LegalInitErrorLogPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.AppendAllText(
                LegalInitErrorLogPath,
                $"{DateTimeOffset.Now:u} — Échec initialisation module conformité{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
            /* ignore */
        }
    }

    /// <summary>
    /// Ne pas bloquer toute l'application : la console (et la bannière « nouvelle version ») doit rester accessibles
    /// pour permettre une mise à jour même si le module légal est défaillant.
    /// </summary>
    private void ApplyComplianceInitFailure(Exception ex)
    {
        ShowBlockingOverlay = false;
        PrivacyText = "";
        TermsText = "";
        DocumentsLoadError =
            "Le module de conformité n'a pas pu s'initialiser. Vous pouvez continuer à utiliser la console ; installez la dernière version de PARAFacto pour corriger le problème. " +
            $"Détail : {ex.Message}";
        AcceptanceSummary =
            "Une erreur a empêché le chargement de la politique de confidentialité et des CGU. Utilisez le lien « nouvelle version » sur la console si disponible, ou téléchargez l'installateur depuis la page de publication. Après mise à jour, ouvrez à nouveau cet onglet pour accepter les documents.";
        AcceptPrivacy = false;
        AcceptTerms = false;
        SaveAcceptanceCommand?.RaiseCanExecuteChanged();
        LegalGateMessage = "";
    }

    /// <summary>Vrai si les textes légaux ne sont pas exploitables (fichiers manquants, vides, erreur de lecture).</summary>
    private bool DocumentsUnavailableForAcceptance =>
        HasDocumentsError || string.IsNullOrWhiteSpace(PrivacyText) || string.IsNullOrWhiteSpace(TermsText);

    public event Action? RequestOpenTechnicalTab;

    /// <summary>À appeler quand l’onglet principal change : l’overlay se retire sur « Données techniques » pour permettre de cocher / enregistrer.</summary>
    public void NotifyMainTabSelectionChanged(int selectedIndex)
    {
        _mainTabSelectedIndex = selectedIndex;
        UpdateBlockingOverlayVisibility();
    }

    public RelayCommand OpenTechnicalTabCommand { get; }
    public RelayCommand SaveAcceptanceCommand { get; }

    public string PrivacyText
    {
        get => _privacyText;
        private set => Set(ref _privacyText, value);
    }

    public string TermsText
    {
        get => _termsText;
        private set => Set(ref _termsText, value);
    }

    public string DocumentsLoadError
    {
        get => _documentsLoadError;
        private set
        {
            if (Set(ref _documentsLoadError, value))
                Raise(nameof(HasDocumentsError));
        }
    }

    public bool HasDocumentsError => !string.IsNullOrWhiteSpace(DocumentsLoadError);

    public bool AcceptPrivacy
    {
        get => _acceptPrivacy;
        set
        {
            if (Set(ref _acceptPrivacy, value))
                SaveAcceptanceCommand?.RaiseCanExecuteChanged();
        }
    }

    public bool AcceptTerms
    {
        get => _acceptTerms;
        set
        {
            if (Set(ref _acceptTerms, value))
                SaveAcceptanceCommand?.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Overlay plein écran tant que la version courante des documents n'est pas acceptée.</summary>
    public bool ShowBlockingOverlay
    {
        get => _showBlockingOverlay;
        private set => Set(ref _showBlockingOverlay, value);
    }

    public string AcceptanceSummary
    {
        get => _acceptanceSummary;
        private set => Set(ref _acceptanceSummary, value);
    }

    /// <summary>Texte de l'overlay bloquant (premier lancement ou nouvelle version des documents légaux).</summary>
    public string LegalGateMessage
    {
        get => _legalGateMessage;
        private set => Set(ref _legalGateMessage, value);
    }

    public string AuditFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PARAFactoNative", "legal_acceptance_audit.json");

    private void LoadDocuments()
    {
        try
        {
            var pPath = LegalDocuments.PrivacyPath(_baseDir);
            var tPath = LegalDocuments.TermsPath(_baseDir);
            if (!File.Exists(pPath) || !File.Exists(tPath))
            {
                DocumentsLoadError =
                    "Fichiers juridiques introuvables dans le dossier d'installation (sous-dossier Legal). Réinstallez l'application ou contactez le support.";
                PrivacyText = "";
                TermsText = "";
                return;
            }

            DocumentsLoadError = "";
            PrivacyText = File.ReadAllText(pPath);
            TermsText = File.ReadAllText(tPath);
        }
        catch (Exception ex)
        {
            DocumentsLoadError = "Impossible de charger les documents : " + ex.Message;
            PrivacyText = "";
            TermsText = "";
        }
    }

    /// <summary>
    /// Tant qu’une acceptation est requise : overlay sur les autres onglets ; pas d’overlay sur « Données techniques »
    /// pour pouvoir lire et cliquer sur Enregistrer (le voile bloquait l’UI derrière le message).
    /// </summary>
    private void UpdateBlockingOverlayVisibility()
    {
        var state = _store.LoadLegalAcceptance();
        var ok = state.IsCompleteForCurrentDocuments();
        var docsMissing = DocumentsUnavailableForAcceptance;
        if (ok || docsMissing)
        {
            ShowBlockingOverlay = false;
            return;
        }

        ShowBlockingOverlay = _mainTabSelectedIndex != TechnicalTabIndex;
    }

    private void ReloadAcceptanceUi()
    {
        var state = _store.LoadLegalAcceptance();
        var ok = state.IsCompleteForCurrentDocuments();
        var docsMissing = DocumentsUnavailableForAcceptance;
        var acceptancePending = !ok && !docsMissing;

        AcceptPrivacy = false;
        AcceptTerms = false;

        if (acceptancePending)
        {
            const string emailPdf =
                " Merci aussi d'ajouter une adresse e-mail d'envoi des copies de vos documents PDF si vous le désirez (section courriel du même onglet).";
            if (state.IsReacceptanceRequiredDueToNewLegalDocumentVersions())
            {
                LegalGateMessage =
                    "Une nouvelle version de la politique de confidentialité et/ou des conditions d'utilisation est fournie avec cette mise à jour. " +
                    "Ouvrez l'onglet « Données techniques » (bouton ci-dessous), lisez les textes et acceptez-les — vous ne pourrez pas utiliser les autres onglets tant que ce n'est pas fait." +
                    emailPdf;
            }
            else
            {
                LegalGateMessage =
                    "Ouvrez l'onglet « Données techniques » (bouton ci-dessous), lisez et acceptez la politique de confidentialité ainsi que les conditions d'utilisation." +
                    emailPdf;
            }
        }
        else
            LegalGateMessage = "";

        if (ok && state.PrivacyAcceptedAtUtc is { } p && state.TermsAcceptedAtUtc is { } t)
        {
            AcceptanceSummary =
                $"Politique de confidentialité (version {LegalDocuments.PrivacyPolicyVersion}) et conditions d'utilisation (version {LegalDocuments.TermsOfServiceVersion}) acceptées le {p:yyyy-MM-dd HH:mm} UTC. " +
                $"Une trace horodatée avec empreinte des textes est enregistrée dans : {AuditFilePath}";
        }
        else if (docsMissing)
        {
            AcceptanceSummary =
                "Les textes de la politique de confidentialité et/ou des conditions d'utilisation sont introuvables ou vides (installation incomplète ou version ancienne). " +
                "Vous pouvez utiliser la console : suivez la bannière « nouvelle version » ou téléchargez la dernière version du site, puis réinstallez. Ensuite, revenez ici pour lire et accepter les documents.";
            if (!string.IsNullOrWhiteSpace(DocumentsLoadError))
                AcceptanceSummary += " " + DocumentsLoadError;
        }
        else if (state.IsReacceptanceRequiredDueToNewLegalDocumentVersions())
        {
            AcceptanceSummary =
                "Les documents juridiques ont été mis à jour (nouvelle version publiée avec l'application). Lisez les textes ci-dessous, cochez les deux cases puis enregistrez votre acceptation — les autres onglets restent masqués par le message tant que ce n'est pas fait.";
        }
        else
        {
            AcceptanceSummary =
                "Veuillez lire les documents ci-dessous, cocher les deux cases puis enregistrer votre acceptation.";
        }

        UpdateBlockingOverlayVisibility();
        SaveAcceptanceCommand?.RaiseCanExecuteChanged();
    }

    private bool CanSaveAcceptance()
        => AcceptPrivacy && AcceptTerms && string.IsNullOrEmpty(DocumentsLoadError)
           && !string.IsNullOrEmpty(PrivacyText) && !string.IsNullOrEmpty(TermsText);

    private void SaveAcceptance()
    {
        if (!CanSaveAcceptance()) return;

        _store.SaveLegalAcceptanceWithProof(PrivacyText, TermsText);
        ReloadAcceptanceUi();
        MessageBox.Show(
            "Votre acceptation a été enregistrée. Une preuve locale (horodatage, versions des documents et empreintes des textes) a été ajoutée au fichier d'audit.",
            "PARAFacto — Conformité",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }
}
