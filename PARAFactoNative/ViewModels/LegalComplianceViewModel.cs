using System.IO;
using System.Windows;
using PARAFactoNative.Services;

namespace PARAFactoNative.ViewModels;

public sealed class LegalComplianceViewModel : NotifyBase
{
    private readonly AppSettingsStore _store;
    private readonly string _baseDir;

    private string _privacyText = "";
    private string _termsText = "";
    private bool _acceptPrivacy;
    private bool _acceptTerms;
    private bool _showBlockingOverlay = true;
    private string _acceptanceSummary = "";
    private string _documentsLoadError = "";

    public LegalComplianceViewModel(AppSettingsStore store, string applicationBaseDirectory)
    {
        _store = store;
        _baseDir = applicationBaseDirectory;

        OpenTechnicalTabCommand = new RelayCommand(() => RequestOpenTechnicalTab?.Invoke());
        SaveAcceptanceCommand = new RelayCommand(SaveAcceptance, CanSaveAcceptance);

        LoadDocuments();
        ReloadAcceptanceUi();
    }

    public event Action? RequestOpenTechnicalTab;

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

    private void ReloadAcceptanceUi()
    {
        var state = _store.LoadLegalAcceptance();
        var ok = state.IsCompleteForCurrentDocuments();
        ShowBlockingOverlay = !ok;
        AcceptPrivacy = false;
        AcceptTerms = false;

        if (ok && state.PrivacyAcceptedAtUtc is { } p && state.TermsAcceptedAtUtc is { } t)
        {
            AcceptanceSummary =
                $"Politique de confidentialité (version {LegalDocuments.PrivacyPolicyVersion}) et conditions d'utilisation (version {LegalDocuments.TermsOfServiceVersion}) acceptées le {p:yyyy-MM-dd HH:mm} UTC. " +
                $"Une trace horodatée avec empreinte des textes est enregistrée dans : {AuditFilePath}";
        }
        else
        {
            AcceptanceSummary =
                "Veuillez lire les documents ci-dessous, cocher les deux cases puis enregistrer votre acceptation.";
        }

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
