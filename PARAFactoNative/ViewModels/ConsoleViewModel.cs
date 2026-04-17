using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.IO;
using System.Threading.Tasks;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Dapper;
using PARAFactoNative.Models;
using PARAFactoNative.Services;
using PARAFactoNative.Views;

namespace PARAFactoNative.ViewModels;

/// <summary>
/// ViewModel de l'onglet Console (MainWindow.xaml).
/// Doit rester cohérent avec les bindings XAML.
/// </summary>
public sealed class ConsoleViewModel : NotifyBase
{
    private const string VersionInfoUrl = "https://parafacto.netlify.app/app-version.json";
    /// <summary>Page « PARAFacto update » (instructions, OK, téléchargement installateur).</summary>
    private const string DefaultDownloadPageUrl = "https://parafactoupdate.netlify.app/";
    private static readonly HttpClient UpdateHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
    };
    /// <summary>URL directe du .exe (app-version.json) — utilisée par le site pour le bouton ; pas pour le clic dans l'app.</summary>
    private string? _installerDownloadUrlOverride;
    /// <summary>Page d'aide / téléchargement (optionnel dans app-version.json).</summary>
    private string? _downloadPageUrlOverride;
    private bool _isInitializing;
    // Navigation/events (consommés par MainWindow.xaml.cs)
    public event Action? RequestNewPatientRequested;
    public event Action<long?>? RequestEditPatientRequested;
#pragma warning disable CS0067 // Événement jamais utilisé (réservé pour usage futur)
    public event Action? RequestShowSeancesRequested;
    public event Action<DateTime>? RequestEditJournalierRequested;
#pragma warning restore CS0067

    public event Action<string>? RequestGeneratePatientInvoicesRequested;
    public event Action<string>? RequestGenerateMutualRecapsRequested;
    public event Action? RequestOpenWorkspaceFolderRequested;
    public event Action<string>? RequestOpenMonthFolderRequested;
    public event Action? RequestOpenLastMutualMonthFolderRequested;

    /// <summary>RDV agenda supprimé depuis la console : rafraîchir le calendrier.</summary>
    public event Action? LinkedAgendaDataChanged;

    private readonly SeanceService _seances = new();
    private readonly AppointmentRepo _appointments = new();
    private readonly UnavailabilityRepo _unavailabilities = new();
    private readonly PatientRepo _patientRepo = new();
    private readonly JournalierPdfService _journalierPdf = new();
    private readonly AppSettingsStore _settings = new();
    private readonly EmailDispatchService _mail = new();
    private readonly DatabaseBackupEmailService _dbBackup = new();

    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-BE");
    private static string EuroFromCents(long cents) => (cents / 100m).ToString("0.00", Fr) + " €";

    // Logo factures (LOGO.jpg) — chargé en C# pour éviter XamlParseException (format pixel non supporté)
    public ImageSource? InvoiceLogoSource => LoadImageSourceSafe(InvoiceLogoPathInternal);
    private static string? InvoiceLogoPathInternal
    {
        get
        {
            try
            {
                var root = Services.WorkspacePaths.GetRootOrNull();
                if (!string.IsNullOrWhiteSpace(root))
                {
                    var p = Path.Combine(root, "assets", "LOGO.jpg");
                    if (File.Exists(p)) return p;
                }
                var fallback = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "PARAFACTO_Native", "assets", "LOGO.jpg");
                return File.Exists(fallback) ? fallback : null;
            }
            catch { return null; }
        }
    }

    // Logo "By TogaThrust" — chargé en C# pour éviter XamlParseException
    public ImageSource? LogoSource => LoadImageSourceSafe(LogoPathInternal);
    private static string? LogoPathInternal
    {
        get
        {
            try
            {
                var root = Services.WorkspacePaths.GetRootOrNull();
                if (!string.IsNullOrWhiteSpace(root))
                {
                    var p = Path.Combine(root, "assets", "byTT.png");
                    if (File.Exists(p)) return p;
                }
                var fallback = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "PARAFACTO_Native", "assets", "byTT.png");
                return File.Exists(fallback) ? fallback : null;
            }
            catch { /* ignore */ }
            return null;
        }
    }

    /// <summary>Charge une image depuis un chemin ; retourne null si fichier absent ou format non supporté (ex. CMYK).</summary>
    private static ImageSource? LoadImageSourceSafe(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath)) return null;
            var uri = new Uri(fullPath, UriKind.Absolute);
            return BitmapFrame.Create(uri);
        }
        catch
        {
            return null;
        }
    }

    // ===================== PATIENTS =====================
    private string? _patientSearch;
    public string? PatientSearch
    {
        get => _patientSearch;
        set
        {
            if (_patientSearch == value) return;
            _patientSearch = value;
            OnPropertyChanged();
            ApplyPatientFilter();
        }
    }

    public ObservableCollection<PatientItem> PatientResults { get; } = new();
    private readonly ObservableCollection<PatientItem> _allPatients = new();

    private PatientItem? _selectedPatient;
    public PatientItem? SelectedPatient
    {
        get => _selectedPatient;
        set
        {
            if (_selectedPatient == value) return;
            _selectedPatient = value;
            OnPropertyChanged();
            AddSeanceCommand.RaiseCanExecuteChanged();
            UpdateSeanceCommand.RaiseCanExecuteChanged();
            EditPatientCommand.RaiseCanExecuteChanged();
        }
    }

    // ===================== TARIFS =====================
    public ObservableCollection<TarifItem> Tarifs { get; } = new();

    private TarifItem? _selectedTarif;
    public TarifItem? SelectedTarif
    {
        get => _selectedTarif;
        set
        {
            if (_selectedTarif == value) return;
            _selectedTarif = value;
            OnPropertyChanged();
            AddSeanceCommand.RaiseCanExecuteChanged();
            UpdateSeanceCommand.RaiseCanExecuteChanged();
        }
    }

    // ===================== ENCODAGE SEANCE =====================
    private DateTime _seanceDate = DateTime.Today;
    public DateTime SeanceDate
    {
        get => _seanceDate;
        set
        {
            var d = value.Date;
            if (_seanceDate.Date == d) return;
            _seanceDate = d;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CurrentPeriod));
            RefreshTodaySeances();
        }
    }

    public ObservableCollection<string> SeanceTimeSlots { get; } = new();

    private string _seanceStartTime = "09:00";
    public string SeanceStartTime
    {
        get => _seanceStartTime;
        set
        {
            var v = (value ?? "").Trim();
            if (string.Equals(_seanceStartTime, v, StringComparison.Ordinal)) return;
            _seanceStartTime = v;
            OnPropertyChanged();
        }
    }

    private bool _isCash;
    public bool IsCash
    {
        get => _isCash;
        set { if (_isCash == value) return; _isCash = value; OnPropertyChanged(); }
    }

    private string? _seanceNotes;
    public string? SeanceNotes
    {
        get => _seanceNotes;
        set { if (_seanceNotes == value) return; _seanceNotes = value; OnPropertyChanged(); }
    }

    // ===================== SEANCES DU JOUR =====================
    public ObservableCollection<SeanceLineVm> TodaySeances { get; } = new();

    private SeanceLineVm? _selectedTodaySeance;
    public SeanceLineVm? SelectedTodaySeance
    {
        get => _selectedTodaySeance;
        set
        {
            if (_selectedTodaySeance == value) return;
            _selectedTodaySeance = value;
            OnPropertyChanged();
            DeleteSeanceCommand.RaiseCanExecuteChanged();
            UpdateSeanceCommand.RaiseCanExecuteChanged();
            ApplyEncodingFromSelectedSeance();
        }
    }

    private string _cashPatientText = "0,00 €";
    public string CashPatientText
    {
        get => _cashPatientText;
        private set { _cashPatientText = value; OnPropertyChanged(); }
    }

    private string _facturedPatientText = "0,00 €";
    public string FacturedPatientText
    {
        get => _facturedPatientText;
        private set { _facturedPatientText = value; OnPropertyChanged(); }
    }

    private string _totalTiersPayantText = "0,00 €";
    public string TotalTiersPayantText
    {
        get => _totalTiersPayantText;
        private set { _totalTiersPayantText = value; OnPropertyChanged(); }
    }

    private string _facturedMutuelleText = "0,00 €";
    public string FacturedMutuelleText
    {
        get => _facturedMutuelleText;
        private set { _facturedMutuelleText = value; OnPropertyChanged(); }
    }

    private string _totalPartPatientText = "0,00 €";
    public string TotalPartPatientText
    {
        get => _totalPartPatientText;
        private set { _totalPartPatientText = value; OnPropertyChanged(); }
    }

    private string _totalPartMutuelleText = "0,00 €";
    public string TotalPartMutuelleText
    {
        get => _totalPartMutuelleText;
        private set { _totalPartMutuelleText = value; OnPropertyChanged(); }
    }

    private string _totalDayText = "0,00 €";
    public string TotalDayText
    {
        get => _totalDayText;
        private set { _totalDayText = value; OnPropertyChanged(); }
    }

    // ===================== FACTURES MENSUELLES =====================
    // Période utilisée par défaut pour les actions "mensuelles" (factures / dossiers) :
    // mois précédent par rapport à la date du jour.
    public string CurrentPeriod => new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-1).ToString("yyyy-MM");

    // ===================== EMAIL (envoi auto des PDF) =====================
    private string? _recipientEmail;
    public string? RecipientEmail
    {
        get => _recipientEmail;
        set
        {
            var v = (value ?? "").Trim();
            if (v.Length == 0) v = null;
            if (_recipientEmail == v) return;
            _recipientEmail = v;
            OnPropertyChanged();
            if (!_isInitializing) PersistMailSettings();
        }
    }

    private bool _useSmtp;
    public bool UseSmtp
    {
        get => _useSmtp;
        set
        {
            if (_useSmtp == value) return;
            _useSmtp = value;
            OnPropertyChanged();
            if (!_isInitializing) PersistMailSettings();
        }
    }

    private string? _smtpHost = "smtp.gmail.com";
    public string? SmtpHost
    {
        get => _smtpHost;
        set { if (_smtpHost == value) return; _smtpHost = value; OnPropertyChanged(); if (!_isInitializing) PersistMailSettings(); }
    }

    private int _smtpPort = 587;
    public int SmtpPort
    {
        get => _smtpPort;
        set { if (_smtpPort == value) return; _smtpPort = value; OnPropertyChanged(); if (!_isInitializing) PersistMailSettings(); }
    }

    private bool _smtpEnableSsl = true;
    public bool SmtpEnableSsl
    {
        get => _smtpEnableSsl;
        set { if (_smtpEnableSsl == value) return; _smtpEnableSsl = value; OnPropertyChanged(); if (!_isInitializing) PersistMailSettings(); }
    }

    private string? _smtpUsername;
    public string? SmtpUsername
    {
        get => _smtpUsername;
        set { if (_smtpUsername == value) return; _smtpUsername = value; OnPropertyChanged(); if (!_isInitializing) PersistMailSettings(); }
    }

    private string? _smtpFromEmail;
    public string? SmtpFromEmail
    {
        get => _smtpFromEmail;
        set { if (_smtpFromEmail == value) return; _smtpFromEmail = value; OnPropertyChanged(); if (!_isInitializing) PersistMailSettings(); }
    }

    private string? _smtpPassword;
    public string? SmtpPassword
    {
        get => _smtpPassword;
        set { if (_smtpPassword == value) return; _smtpPassword = value; OnPropertyChanged(); if (!_isInitializing) PersistMailSettings(); }
    }

    // ===================== BUSY + UNDO =====================
    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy == value) return;
            _isBusy = value;
            OnPropertyChanged();
            AddSeanceCommand.RaiseCanExecuteChanged();
            UpdateSeanceCommand.RaiseCanExecuteChanged();
            DeleteSeanceCommand.RaiseCanExecuteChanged();
            UndoCommand.RaiseCanExecuteChanged();
            ExportJournalierCommand.RaiseCanExecuteChanged();
            NewPatientCommand.RaiseCanExecuteChanged();
            EditPatientCommand.RaiseCanExecuteChanged();
        }
    }

    private interface IUndoAction { void Undo(); }

    private sealed class UndoDelete : IUndoAction
    {
        private readonly SeanceService _svc;
        private readonly Seance _s;
        public UndoDelete(SeanceService svc, Seance s) { _svc = svc; _s = s; }
        public void Undo() => _svc.InsertSeanceWithId(_s);
    }

    private sealed class UndoUpdate : IUndoAction
    {
        private readonly SeanceService _svc;
        private readonly Seance _before;
        public UndoUpdate(SeanceService svc, Seance before) { _svc = svc; _before = before; }
        public void Undo() => _svc.UpdateSeanceRaw(_before);
    }

    private readonly Stack<IUndoAction> _undo = new();
    public bool CanUndo => _undo.Count > 0;

    // ===================== COMMANDS =====================
    public RelayCommand NewPatientCommand { get; }
    public RelayCommand EditPatientCommand { get; }
    public RelayCommand AddSeanceCommand { get; }
    public RelayCommand UpdateSeanceCommand { get; }
    public RelayCommand DeleteSeanceCommand { get; }
    public RelayCommand UndoCommand { get; }
    public RelayCommand ExportJournalierCommand { get; }

    public RelayCommand GeneratePatientInvoicesCommand { get; }
    public RelayCommand GenerateMutualRecapsCommand { get; }
    public RelayCommand OpenWorkspaceFolderCommand { get; }
    public RelayCommand OpenMonthFolderCommand { get; }
    public RelayCommand OpenLastMutualMonthFolderCommand { get; }
    public RelayCommand ImportAgendaCommand { get; }
    public RelayCommand OpenInstallerDownloadCommand { get; }

    private bool _isUpdateAvailable;
    public bool IsUpdateAvailable
    {
        get => _isUpdateAvailable;
        private set => Set(ref _isUpdateAvailable, value);
    }

    public ConsoleViewModel()
    {
        _isInitializing = true;
        // RelayCommand attendu ici : Action (sans paramètre) + Func<bool> (sans paramètre)
        // => ne pas utiliser de lambda avec paramètre (_ => ...)
        NewPatientCommand = new RelayCommand(() => RequestNewPatientRequested?.Invoke(), () => !IsBusy);
        EditPatientCommand = new RelayCommand(() => RequestEditPatientRequested?.Invoke(SelectedPatient?.Id), () => !IsBusy && SelectedPatient is not null);

        AddSeanceCommand = new RelayCommand(() => AddSeance(), () => !IsBusy && SelectedPatient is not null && SelectedTarif is not null);
        UpdateSeanceCommand = new RelayCommand(() => UpdateSelectedSeance(), () => !IsBusy && SelectedTodaySeance is not null && SelectedPatient is not null && SelectedTarif is not null);
        DeleteSeanceCommand = new RelayCommand(() => DeleteSelectedSeance(), () => !IsBusy && SelectedTodaySeance is not null);
        UndoCommand = new RelayCommand(() => UndoLast(), () => !IsBusy && CanUndo);

        ExportJournalierCommand = new RelayCommand(() => ExportJournalierPdf(), () => !IsBusy);

GeneratePatientInvoicesCommand = new RelayCommand(() => RequestGeneratePatientInvoicesRequested?.Invoke(CurrentPeriod), () => !IsBusy);
GenerateMutualRecapsCommand = new RelayCommand(() => RequestGenerateMutualRecapsRequested?.Invoke(CurrentPeriod), () => !IsBusy);
OpenWorkspaceFolderCommand = new RelayCommand(() => RequestOpenWorkspaceFolderRequested?.Invoke(), () => !IsBusy);
OpenMonthFolderCommand = new RelayCommand(() => RequestOpenMonthFolderRequested?.Invoke(CurrentPeriod), () => !IsBusy);
OpenLastMutualMonthFolderCommand = new RelayCommand(() => RequestOpenLastMutualMonthFolderRequested?.Invoke(), () => !IsBusy);
        ImportAgendaCommand = new RelayCommand(ImportAgendaForSelectedDay, () => !IsBusy);
        // Toujours cliquable : la mise à jour ne doit pas être bloquée par IsBusy ; le clic doit ouvrir le navigateur de façon fiable.
        OpenInstallerDownloadCommand = new RelayCommand(OpenInstallerDownloadPage, () => true);

        var ms = _settings.LoadMailSettings();
        _recipientEmail = ms.RecipientEmail;
        _useSmtp = ms.UseSmtp;
        _smtpHost = string.IsNullOrWhiteSpace(ms.SmtpHost) ? _smtpHost : ms.SmtpHost;
        _smtpPort = ms.SmtpPort;
        _smtpEnableSsl = ms.SmtpEnableSsl;
        _smtpUsername = ms.SmtpUsername;
        _smtpFromEmail = ms.SmtpFromEmail;
        _smtpPassword = ms.SmtpPassword;

        OnPropertyChanged(nameof(RecipientEmail));

        _isInitializing = false;

        // Valeur par défaut (modifiable) si aucun email n'est encore configuré
        if (string.IsNullOrWhiteSpace(RecipientEmail))
        {
            RecipientEmail = "grenier.family.7324@gmail.com";
            PersistMailSettings();
        }

        OnPropertyChanged(nameof(UseSmtp));
        OnPropertyChanged(nameof(SmtpHost));
        OnPropertyChanged(nameof(SmtpPort));
        OnPropertyChanged(nameof(SmtpEnableSsl));
        OnPropertyChanged(nameof(SmtpUsername));
        OnPropertyChanged(nameof(SmtpFromEmail));
        OnPropertyChanged(nameof(SmtpPassword));

        BuildSeanceTimeSlots();
        SeanceStartTime = RoundToQuarter(DateTime.Now.Hour * 60 + DateTime.Now.Minute);

        ReloadRefs();
        _ = CheckForUpdateAsync();
    }

    private void BuildSeanceTimeSlots()
    {
        SeanceTimeSlots.Clear();
        for (var t = 0; t < 24 * 60; t += 15)
            SeanceTimeSlots.Add(AppointmentScheduling.FormatMinutesAsHhMm(t));
    }

    private static string RoundToQuarter(int minutesSinceMidnight)
    {
        var m = Math.Clamp(((minutesSinceMidnight + 14) / 15) * 15, 0, 23 * 60 + 45);
        return AppointmentScheduling.FormatMinutesAsHhMm(m);
    }

    private async Task CheckForUpdateAsync()
    {
        try
        {
            var localVersion = Assembly.GetExecutingAssembly().GetName().Version;
            if (localVersion is null) return;

            // Éviter une réponse obsolète (CDN / cache intermédiaire) : requête non cacheable + query unique.
            var url = $"{VersionInfoUrl}?_={DateTime.UtcNow.Ticks}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
            request.Headers.TryAddWithoutValidation("Pragma", "no-cache");

            using var response = await UpdateHttpClient.SendAsync(request).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("latestVersion", out var latestProp)) return;

            var latestRaw = latestProp.GetString();
            if (string.IsNullOrWhiteSpace(latestRaw)) return;
            latestRaw = latestRaw.Trim();
            if (latestRaw.Length > 0 && (latestRaw[0] == 'v' || latestRaw[0] == 'V'))
                latestRaw = latestRaw[1..].TrimStart();
            if (!Version.TryParse(latestRaw, out var latestVersion)) return;

            string? installerOverride = null;
            if (doc.RootElement.TryGetProperty("installerUrl", out var instEl))
            {
                var u = (instEl.GetString() ?? "").Trim();
                if (u.Length > 0 && Uri.TryCreate(u, UriKind.Absolute, out var uri)
                    && (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
                    installerOverride = uri.ToString();
            }

            string? downloadPageOverride = null;
            if (doc.RootElement.TryGetProperty("downloadPageUrl", out var pageEl))
            {
                var p = (pageEl.GetString() ?? "").Trim();
                if (p.Length > 0 && Uri.TryCreate(p, UriKind.Absolute, out var pageUri)
                    && (string.Equals(pageUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(pageUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
                    downloadPageOverride = pageUri.ToString();
            }

            // Retour UI thread pour notifier WPF proprement.
            Application.Current?.Dispatcher.Invoke(() =>
            {
                _installerDownloadUrlOverride = installerOverride;
                _downloadPageUrlOverride = downloadPageOverride;
                IsUpdateAvailable = latestVersion > localVersion;
                OpenInstallerDownloadCommand.RaiseCanExecuteChanged();
            });
        }
        catch
        {
            // Vérification silencieuse : en cas d'échec réseau, on n'affiche pas de faux positif.
        }
    }

    private void PersistMailSettings()
    {
        _settings.SaveMailSettings(new AppMailSettings
        {
            RecipientEmail = RecipientEmail,
            UseSmtp = UseSmtp,
            SmtpHost = SmtpHost,
            SmtpPort = SmtpPort,
            SmtpEnableSsl = SmtpEnableSsl,
            SmtpUsername = SmtpUsername,
            SmtpFromEmail = SmtpFromEmail,
            SmtpPassword = SmtpPassword
        });
    }

    public void ReloadRefs()
    {
        try
        {
            IsBusy = true;
            LoadPatients();
            LoadTarifs();
            ApplyPatientFilter();

            SelectedTarif = Tarifs.FirstOrDefault(t => string.Equals(t.Label, "BIM CABINET 30 MIN", StringComparison.OrdinalIgnoreCase))
                            ?? Tarifs.FirstOrDefault();
            SelectedPatient ??= PatientResults.FirstOrDefault();

            RefreshTodaySeances();

            OnPropertyChanged(nameof(InvoiceLogoSource));
            OnPropertyChanged(nameof(LogoSource));
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "PARAFacto", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void LoadPatients()
    {
        _allPatients.Clear();
        using var cn = Db.Open();
        var cols = cn.Query<string>("SELECT name FROM pragma_table_info('patients');").ToHashSet(StringComparer.OrdinalIgnoreCase);

        var idCol = cols.Contains("id") ? "id" : (cols.Contains("patient_id") ? "patient_id" : "id");
        var codeCol = cols.Contains("code3") ? "code3" : (cols.Contains("code") ? "code" : "code3");
        var nomCol = cols.Contains("nom") ? "nom" : (cols.Contains("lastname") ? "lastname" : "nom");
        var prenomCol = cols.Contains("prenom") ? "prenom" : (cols.Contains("firstname") ? "firstname" : "prenom");
        var statutCol = cols.Contains("statut") ? "statut" : (cols.Contains("status") ? "status" : null);
        var mutCol = cols.Contains("mutuelle") ? "mutuelle" : (cols.Contains("mutualname") ? "mutualname" : null);

        var sql = $"SELECT {idCol} as Id, {codeCol} as Code3, {nomCol} as Nom, {prenomCol} as Prenom"
                  + (statutCol != null ? $", {statutCol} as Statut" : ", '' as Statut")
                  + (mutCol != null ? $", {mutCol} as Mutuelle" : ", '' as Mutuelle")
                  + $" FROM patients ORDER BY {nomCol}, {prenomCol};";

        foreach (var r in cn.Query(sql))
        {
            var id = (long)r.Id;
            var code = ((string?)r.Code3 ?? "").Trim();
            var nom = ((string?)r.Nom ?? "").Trim();
            var prenom = ((string?)r.Prenom ?? "").Trim();
            var statut = ((string?)r.Statut ?? "").Trim();
            var mut = ((string?)r.Mutuelle ?? "").Trim();

            _allPatients.Add(new PatientItem(id, code, nom, prenom, statut, mut));
        }
    }

    private void LoadTarifs()
    {
        Tarifs.Clear();
        using var cn = Db.Open();
        var cols = cn.Query<string>("SELECT name FROM pragma_table_info('tarifs');").ToHashSet(StringComparer.OrdinalIgnoreCase);

        var idCol = cols.Contains("id") ? "id" : (cols.Contains("tarif_id") ? "tarif_id" : "id");
        var libCol = cols.Contains("libelle") ? "libelle" : (cols.Contains("label") ? "label" : "libelle");

        // cents columns (fallback: assume stored in cents)
        // schéma courant: part_patient / part_mutuelle (en cents)
        var ppCol = cols.Contains("part_patient") ? "part_patient" : (cols.Contains("part_patient_cents") ? "part_patient_cents" : "part_patient");
        var pmCol = cols.Contains("part_mutuelle") ? "part_mutuelle" : (cols.Contains("part_mutuelle_cents") ? "part_mutuelle_cents" : "part_mutuelle");

        var sql = $"SELECT {idCol} as Id, {libCol} as Libelle, {ppCol} as PartPatientCents, {pmCol} as PartMutuelleCents FROM tarifs ORDER BY {libCol};";

        foreach (var r in cn.Query(sql))
        {
            var id = (long)r.Id;
            var lib = ((string?)r.Libelle ?? "").Trim();
            var pp = Convert.ToInt64(r.PartPatientCents);
            var pm = Convert.ToInt64(r.PartMutuelleCents);
            Tarifs.Add(new TarifItem(id, lib, pp, pm));
        }
    }

    /// <summary>Remplit patient / tarif / espèces depuis la ligne de séance sélectionnée (liste du jour).</summary>
    private void ApplyEncodingFromSelectedSeance()
    {
        if (SelectedTodaySeance is null || _isInitializing) return;
        var line = SelectedTodaySeance;
        PatientSearch = "";
        ApplyPatientFilter();
        var p = _allPatients.FirstOrDefault(x => x.Id == line.PatientId)
                ?? PatientResults.FirstOrDefault(x => x.Id == line.PatientId);
        if (p is not null)
            SelectedPatient = p;
        if (line.TarifId > 0)
        {
            var t = Tarifs.FirstOrDefault(x => x.Id == line.TarifId);
            if (t is not null)
                SelectedTarif = t;
        }
        IsCash = line.IsCash;
        if (SeanceRdvTimeHelper.TryGetRdvMarkerPrefix(line.Commentaire, out var px))
            SeanceNotes = SeanceRdvTimeHelper.GetUserCommentAfterMarker(line.Commentaire, px);
        else
            SeanceNotes = string.IsNullOrWhiteSpace(line.Commentaire) ? null : line.Commentaire;
    }

    private void ApplyPatientFilter()
    {
        PatientResults.Clear();

        var q = (PatientSearch ?? "").Trim();
        if (string.IsNullOrWhiteSpace(q))
        {
            foreach (var it in _allPatients)
                PatientResults.Add(it);
        }
        else
        {
            q = q.ToLowerInvariant();
            foreach (var it in _allPatients)
                if (it.DisplayLower.Contains(q))
                    PatientResults.Add(it);
        }

        if (SelectedPatient is null || !PatientResults.Contains(SelectedPatient))
            SelectedPatient = PatientResults.FirstOrDefault();

        EditPatientCommand.RaiseCanExecuteChanged();
    }

    private void ImportAgendaForSelectedDay()
    {
        try
        {
            IsBusy = true;
            SeanceDate = DateTime.Today;
            var day = DateTime.Today;
            var list = _appointments.ListForDay(day).OrderBy(a => a.StartTime).ToList();
            if (list.Count == 0)
            {
                MessageBox.Show(
                    $"Aucun rendez-vous dans l'agenda pour aujourd'hui ({day:dd/MM/yyyy}).",
                    "PARAFacto — Agenda",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var added = 0;
            var skipped = 0;
            var sb = new StringBuilder();
            foreach (var a in list)
            {
                if (_seances.SeanceExistsForRdvMarker(a.Id, day))
                {
                    skipped++;
                    continue;
                }

                var com = $"[RDV#{a.Id}] {a.StartTime}";
                _seances.AddSeance(a.PatientId, a.TarifId, day, false, com);
                added++;
            }

            RefreshTodaySeances(manageBusy: false);

            if (skipped > 0)
                sb.AppendLine($"{skipped} RDV déjà importé(s) (ignorés).");
            sb.AppendLine($"{added} séance(s) ajoutée(s) depuis l'agenda (jour en cours uniquement).");
            MessageBox.Show(sb.ToString().Trim(), "IMPORTER AGENDA", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "IMPORTER AGENDA", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Rafraîchit uniquement la liste des séances du jour (sans recharger patients/tarifs).</summary>
    public void RefreshTodaySeancesList() => RefreshTodaySeances();

    private void RefreshTodaySeances(bool manageBusy = true)
    {
        try
        {
            if (manageBusy) IsBusy = true;
            TodaySeances.Clear();

            var rows = _seances.GetSeancesForDay(SeanceDate);
            foreach (var r in rows)
                TodaySeances.Add(SeanceLineVm.From(r));

            SelectedTodaySeance = TodaySeances.FirstOrDefault();
            UpdateTotalsText();
        }
        finally
        {
            if (manageBusy) IsBusy = false;
        }
    }

    private void UpdateTotalsText()
    {
        var cashPatientCents = TodaySeances.Where(s => s.IsCash).Sum(s => s.PartPatientCents);
        var facturedPatientCents = TodaySeances.Where(s => !s.IsCash).Sum(s => s.PartPatientCents);
        var totalTiersPayantCents = cashPatientCents + facturedPatientCents;
        var facturedMutuelleCents = TodaySeances.Sum(s => s.PartMutuelleCents);
        var totalDayCents = totalTiersPayantCents + facturedMutuelleCents;

        CashPatientText = EuroFromCents(cashPatientCents);
        FacturedPatientText = EuroFromCents(facturedPatientCents);
        TotalTiersPayantText = EuroFromCents(totalTiersPayantCents);
        FacturedMutuelleText = EuroFromCents(facturedMutuelleCents);
        TotalPartPatientText = EuroFromCents(totalTiersPayantCents);
        TotalPartMutuelleText = EuroFromCents(facturedMutuelleCents);
        TotalDayText = EuroFromCents(totalDayCents);
    }

    private void AddSeance()
    {
        if (SelectedPatient is null || SelectedTarif is null) return;

        long? createdAppointmentId = null;
        try
        {
            IsBusy = true;
            var userNotes = string.IsNullOrWhiteSpace(SeanceNotes) ? null : SeanceNotes.Trim();
            var finalComment = userNotes;

            if (SeanceDate.Date == DateTime.Today)
            {
                if (TryCreateAgendaAppointmentForToday(SelectedPatient.Id, SelectedTarif.Id, SelectedTarif.Label, SeanceStartTime, out var newAppointmentId, out var markerPrefix))
                {
                    createdAppointmentId = newAppointmentId;
                    finalComment = SeanceRdvTimeHelper.MergeRdvMarkerWithUserInput(markerPrefix, userNotes);
                }
            }

            _seances.AddSeance(
                SelectedPatient.Id,
                SelectedTarif.Id,
                SeanceDate,
                IsCash,
                finalComment);

            if (createdAppointmentId is > 0)
                LinkedAgendaDataChanged?.Invoke();

            SeanceNotes = null;
            IsCash = false;

            RefreshTodaySeances();
        }
        catch (Exception ex)
        {
            if (createdAppointmentId is > 0)
            {
                try { _appointments.Delete(createdAppointmentId.Value); } catch { /* ignore rollback best effort */ }
            }
            MessageBox.Show(ex.Message, "PARAFacto", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool TryCreateAgendaAppointmentForToday(
        long patientId,
        long tarifId,
        string? tarifLabel,
        string? requestedStartHhMm,
        out long appointmentId,
        out string markerPrefix)
    {
        appointmentId = 0;
        markerPrefix = "";

        var day = DateTime.Today;
        var sameDay = _appointments.ListForDay(day);
        var unavailDay = _unavailabilities.ListForDay(day);
        var (workdayStartMin, closingEndMin) = _settings.LoadAgendaWorkdayMinutes();
        var lunch = _settings.LoadAgendaLunch();
        int? lunchStart = lunch.enabled ? lunch.startMin : null;
        int? lunchEnd = lunch.enabled ? lunch.endMin : null;

        int? earliestStart = null;
        var req = (requestedStartHhMm ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(req))
        {
            if (AppointmentScheduling.TryParseTimeToMinutes(req, out var reqMin))
                earliestStart = reqMin;
            else
                MessageBox.Show(
                    "Heure de séance invalide (format attendu HH:mm). Placement automatique sur le premier créneau disponible.",
                    "PARAFacto",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
        }
        if (!earliestStart.HasValue)
        {
            var now = DateTime.Now;
            earliestStart = now.Hour * 60 + now.Minute;
        }
        var durationMinutes = GuessDurationMinutesFromTarif(tarifLabel);

        var slot = AppointmentScheduling.FindFirstAvailableStart(
            sameDay,
            unavailDay,
            durationMinutes,
            excludeAppointmentId: null,
            workdayStartMin,
            closingEndMin,
            stepMinutes: 15,
            earliestStartMinInclusive: earliestStart.Value,
            lunchBlockStartMin: lunchStart,
            lunchBlockEndMin: lunchEnd);

        if (string.IsNullOrWhiteSpace(slot))
            return false;

        appointmentId = _appointments.Insert(
            patientId,
            tarifId,
            day.ToString("yyyy-MM-dd"),
            slot,
            durationMinutes);

        markerPrefix = $"[RDV#{appointmentId}] {slot}";
        return true;
    }

    private static int GuessDurationMinutesFromTarif(string? tarifLabel)
    {
        if (string.IsNullOrWhiteSpace(tarifLabel))
            return 30;

        var m = Regex.Match(tarifLabel, @"(\d{2,3})\s*MIN", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!m.Success)
            return 30;

        if (!int.TryParse(m.Groups[1].Value, out var parsed))
            return 30;

        if (parsed < 15 || parsed > 180)
            return 30;

        return parsed;
    }

    private void UpdateSelectedSeance()
    {
        if (SelectedTodaySeance is null || SelectedPatient is null || SelectedTarif is null) return;

        if (!ChoiceDialog.AskYesNo(
                "PARAFacto",
                "Modifier la séance sélectionnée avec les paramètres actuels ?",
                "Enregistrer les modifications",
                "Annuler"))
            return;

        try
        {
            IsBusy = true;
            var before = _seances.GetById(SelectedTodaySeance.SeanceId);
            var notes = string.IsNullOrWhiteSpace(SeanceNotes) ? null : SeanceNotes.Trim();
            var mergedCom = before is null
                ? notes
                : SeanceRdvTimeHelper.MergeRdvMarkerWithUserInput(before.Commentaire, notes);

            _seances.UpdateSeance(
                SelectedTodaySeance.SeanceId,
                SelectedPatient.Id,
                SelectedTarif.Id,
                SeanceDate,
                IsCash,
                mergedCom);

            if (before is not null)
            {
                _undo.Push(new UndoUpdate(_seances, before));
                OnPropertyChanged(nameof(CanUndo));
                UndoCommand.RaiseCanExecuteChanged();
            }

            RefreshTodaySeances();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "PARAFacto", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void DeleteSelectedSeance()
    {
        if (SelectedTodaySeance is null) return;

        if (!ChoiceDialog.AskYesNo(
                "PARAFacto",
                "Supprimer la séance sélectionnée ?",
                "Supprimer",
                "Annuler"))
            return;

        try
        {
            IsBusy = true;
            var before = _seances.GetById(SelectedTodaySeance.SeanceId);
            long? rdvToDelete = null;
            if (before?.Commentaire is { } com && SeanceRdvTimeHelper.TryParseRdvAppointmentId(com, out var aid))
                rdvToDelete = aid;

            _seances.DeleteSeance(SelectedTodaySeance.SeanceId);

            if (rdvToDelete is > 0)
            {
                try
                {
                    _appointments.Delete(rdvToDelete.Value);
                    if (before is not null)
                        AppointmentResponsibleNotify.ShowNotifyResponsibleDialog(_patientRepo, before.PatientId, "PARAFacto");
                }
                catch
                {
                    /* RDV déjà absent */
                }

                LinkedAgendaDataChanged?.Invoke();
            }

            if (before is not null)
            {
                _undo.Push(new UndoDelete(_seances, before));
                OnPropertyChanged(nameof(CanUndo));
                UndoCommand.RaiseCanExecuteChanged();
            }

            RefreshTodaySeances();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "PARAFacto", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void UndoLast()
    {
        if (!CanUndo) return;
        try
        {
            IsBusy = true;
            var a = _undo.Pop();
            a.Undo();
            RefreshTodaySeances();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "PARAFacto", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanUndo));
            UndoCommand.RaiseCanExecuteChanged();
        }
    }

    private void ExportJournalierPdf()
    {
        try
        {
            IsBusy = true;

            var rows = _seances.GetSeancesForDay(SeanceDate);

            var root = WorkspacePaths.GetRootOrNull();
            if (string.IsNullOrWhiteSpace(root))
            {
                MessageBox.Show("Workspace introuvable.", "PARAFacto", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Export directement dans le dossier workspace existant "JOURNALIERS PDF" (pas de sous-dossier par période)
            var outDir = System.IO.Path.Combine(root, "JOURNALIERS PDF");
            Directory.CreateDirectory(outDir);
            var outPath = System.IO.Path.Combine(outDir, $"ENCAISSEMENTS_{SeanceDate:yyyy-MM-dd}.pdf");
            _journalierPdf.Generate(SeanceDate, rows, outPath);

            // Envoi automatique par mail (si adresse renseignée)
            if (!string.IsNullOrWhiteSpace(RecipientEmail))
            {
                var fr = CultureInfo.GetCultureInfo("fr-BE");
                var subject = $"Journalier séances laura {SeanceDate.ToString("d MMMM yyyy", fr)}";
                var body = $"Veuillez trouver ci-joint le journalier des séances du {SeanceDate.ToString("d MMMM yyyy", fr)}.";
                var settings = _settings.LoadMailSettings();
                settings.RecipientEmail = RecipientEmail;
                if (!_mail.TrySend(settings, subject, body, new[] { outPath }, out var err))
                    MessageBox.Show($"Le PDF a été généré, mais l'envoi e-mail a échoué.\n\n{err}", "PARAFacto - Email", MessageBoxButton.OK, MessageBoxImage.Warning);

                // Envoi automatique de sauvegarde DB (un mail séparé, uniquement la base)
                if (_dbBackup.TrySendDatabaseBackupEmail(_mail, settings, out var dbErr) == false)
                {
                    MessageBox.Show(
                        "Le PDF a été généré, et l'envoi mail principal a été tenté.\n" +
                        "Cependant, l'envoi de sauvegarde DB a échoué.\n\n" +
                        dbErr,
                        "PARAFacto - Sauvegarde DB",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }

            var ask = ChoiceDialog.AskThree(
                "PARAFacto",
                $@"PDF généré :
{outPath}

Voulez-vous l'imprimer maintenant ?",
                "Imprimer",
                "Ouvrir le dossier",
                "Ne rien faire");

            if (ask == ActionChoiceResult.Primary)
                TryPrintPdf(outPath);
            else if (ask == ActionChoiceResult.Secondary)
                OpenFolder(outDir);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "PARAFacto", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static void OpenFolder(string folderPath)
    {
        try
        {
            if (!Directory.Exists(folderPath)) return;

            Process.Start(new ProcessStartInfo
            {
                FileName = folderPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($@"Le PDF a bien été généré, mais le dossier n'a pas pu être ouvert.

{ex.Message}", "PARAFacto", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void TryPrintPdf(string pdfPath)
    {
        try
        {
            if (!File.Exists(pdfPath)) return;

            var psi = new ProcessStartInfo
            {
                FileName = pdfPath,
                Verb = "print",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            Process.Start(psi);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Le PDF a bien été généré, mais l'impression n'a pas pu être lancée.\n\n{ex.Message}", "PARAFacto", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenInstallerDownloadPage()
    {
        try
        {
            var landing = string.IsNullOrWhiteSpace(_downloadPageUrlOverride)
                ? DefaultDownloadPageUrl
                : _downloadPageUrlOverride;

            Process.Start(new ProcessStartInfo
            {
                FileName = landing,
                UseShellExecute = true
            });
            // Laisser Windows lancer le navigateur avant de fermer l’app (sinon le shell peut ne pas afficher l’onglet).
            _ = ShutdownAfterShortDelayAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Impossible d'ouvrir la page de téléchargement : {ex.Message}",
                "PARAFacto",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private static async Task ShutdownAfterShortDelayAsync()
    {
        try
        {
            await Task.Delay(800).ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }

        try
        {
            Application.Current?.Dispatcher.Invoke(() => Application.Current.Shutdown(0));
        }
        catch
        {
            try { Environment.Exit(0); } catch { /* ignore */ }
        }
    }

    // ===================== Nested types =====================
    public sealed class PatientItem : NotifyBase
    {
        public long Id { get; }
        public string Code3 { get; }
        public string LastName { get; }
        public string FirstName { get; }
        public string Statut { get; }
        public string MutualName { get; }
        public string Display { get; }
        public string DisplayLower { get; }

        public PatientItem(long id, string code3, string lastName, string firstName, string statut, string mutual)
        {
            Id = id;
            Code3 = code3;
            LastName = lastName;
            FirstName = firstName;
            Statut = statut;
            MutualName = mutual;
            Display = $"{code3} — {lastName}  {firstName}".Trim();
            DisplayLower = Display.ToLowerInvariant();
        }
    }

    public sealed class TarifItem : NotifyBase
    {
        public long Id { get; }
        public string Label { get; }
        public long PartPatientCents { get; }
        public long PartMutuelleCents { get; }
        public string PartPatientEuro => EuroFromCents(PartPatientCents);
        public string PartMutuelleEuro => EuroFromCents(PartMutuelleCents);

        public TarifItem(long id, string label, long partPatientCents, long partMutuelleCents)
        {
            Id = id;
            Label = label;
            PartPatientCents = partPatientCents;
            PartMutuelleCents = partMutuelleCents;
        }
    }

    public sealed class SeanceLineVm : NotifyBase
    {
        private static readonly Regex AgendaImportMarkerOnlyRegex = new(
            @"^\[RDV#\d+\]\s*\d{1,2}:\d{2}\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public long SeanceId { get; init; }
        public long PatientId { get; init; }
        public long TarifId { get; init; }
        public string Code3 { get; init; } = "";
        public string PatientDisplay { get; init; } = "";
        public string TarifLabel { get; init; } = "";
        public bool IsCash { get; init; }
        public string Commentaire { get; init; } = "";
        public bool HasComment =>
            !string.IsNullOrWhiteSpace(Commentaire) && !AgendaImportMarkerOnlyRegex.IsMatch(Commentaire.Trim());
        public string HasCommentMark => HasComment ? "Oui" : "";
        public long PartPatientCents { get; init; }
        public long PartMutuelleCents { get; init; }
        public string PartPatientEuro => EuroFromCents(PartPatientCents);
        public string PartMutuelleEuro => EuroFromCents(PartMutuelleCents);

        public static SeanceLineVm From(SeanceRow r)
        {
            return new SeanceLineVm
            {
                SeanceId = r.SeanceId,
                PatientId = r.PatientId,
                TarifId = r.TarifId,
                Code3 = (r.Code3 ?? "").Trim(),
                PatientDisplay = (r.PatientDisplay ?? "").Trim(),
                TarifLabel = (r.TarifLabel ?? "").Trim(),
                IsCash = r.IsCash,
                Commentaire = (r.Commentaire ?? "").Trim(),
                PartPatientCents = r.PartPatientCents,
                PartMutuelleCents = r.PartMutuelleCents
            };
        }
    }
}
