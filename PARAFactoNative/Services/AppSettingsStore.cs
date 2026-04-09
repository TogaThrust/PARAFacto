using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;

namespace PARAFactoNative.Services;

public sealed class AppSettingsStore
{
    private static readonly object Gate = new();

    private sealed class AppSettings
    {
        public string? RecipientEmail { get; set; }
        public bool UseSmtp { get; set; }
        public string? SmtpHost { get; set; }
        public int SmtpPort { get; set; } = 587;
        public bool SmtpEnableSsl { get; set; } = true;
        public string? SmtpUsername { get; set; }
        public string? SmtpFromEmail { get; set; }
        public string? SmtpPasswordProtectedBase64 { get; set; }
        /// <summary>Début de journée agenda (HH:mm), ex. 09:00.</summary>
        public string? AgendaWorkdayStart { get; set; }
        /// <summary>Fin de journée : dernière minute autorisée pour la <b>fin</b> d’un RDV (HH:mm), ex. 21:00.</summary>
        public string? AgendaDayClosing { get; set; }
        public bool AgendaLunchEnabled { get; set; }
        public string? AgendaLunchStart { get; set; }
        public string? AgendaLunchEnd { get; set; }
        /// <summary>Horodatage ISO 8601 (UTC) de l'acceptation de la politique de confidentialité.</summary>
        public string? PrivacyAcceptedAtUtc { get; set; }
        /// <summary>Horodatage ISO 8601 (UTC) de l'acceptation des CGU.</summary>
        public string? TermsAcceptedAtUtc { get; set; }
        public string? PrivacyDocVersionAccepted { get; set; }
        public string? TermsDocVersionAccepted { get; set; }
        public string? PrivacyContentSha256Accepted { get; set; }
        public string? TermsContentSha256Accepted { get; set; }
        public string? UiLanguage { get; set; }
    }

    private static string SettingsFolder
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PARAFactoNative");

    private static string SettingsPath
        => Path.Combine(SettingsFolder, "settings.json");

    private static string LegalAuditPath
        => Path.Combine(SettingsFolder, "legal_acceptance_audit.json");

    private static readonly JsonSerializerOptions AuditJsonOptions = new() { WriteIndented = true };

    public string? LoadRecipientEmail()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(SettingsPath)) return null;
                var json = File.ReadAllText(SettingsPath);
                var s = JsonSerializer.Deserialize<AppSettings>(json);
                return string.IsNullOrWhiteSpace(s?.RecipientEmail) ? null : s!.RecipientEmail!.Trim();
            }
            catch
            {
                return null;
            }
        }
    }

    public void SaveRecipientEmail(string? email)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(SettingsFolder);
                var s = LoadAllInternal() ?? new AppSettings();
                s.RecipientEmail = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
                var json = JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsPath, json);
            }
            catch
            {
                // Ne pas bloquer l'app si l'écriture échoue (OneDrive verrouillé, droits, etc.)
            }
        }
    }

    public AppMailSettings LoadMailSettings()
    {
        lock (Gate)
        {
            var s = LoadAllInternal() ?? new AppSettings();
            return new AppMailSettings
            {
                RecipientEmail = string.IsNullOrWhiteSpace(s.RecipientEmail) ? null : s.RecipientEmail!.Trim(),
                UseSmtp = s.UseSmtp,
                SmtpHost = string.IsNullOrWhiteSpace(s.SmtpHost) ? null : s.SmtpHost!.Trim(),
                SmtpPort = s.SmtpPort <= 0 ? 587 : s.SmtpPort,
                SmtpEnableSsl = s.SmtpEnableSsl,
                SmtpUsername = string.IsNullOrWhiteSpace(s.SmtpUsername) ? null : s.SmtpUsername!.Trim(),
                SmtpFromEmail = string.IsNullOrWhiteSpace(s.SmtpFromEmail) ? null : s.SmtpFromEmail!.Trim(),
                SmtpPassword = UnprotectStringOrNull(s.SmtpPasswordProtectedBase64)
            };
        }
    }

    public void SaveMailSettings(AppMailSettings mail)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(SettingsFolder);
                var s = LoadAllInternal() ?? new AppSettings();
                s.RecipientEmail = string.IsNullOrWhiteSpace(mail.RecipientEmail) ? null : mail.RecipientEmail!.Trim();
                s.UseSmtp = mail.UseSmtp;
                s.SmtpHost = string.IsNullOrWhiteSpace(mail.SmtpHost) ? null : mail.SmtpHost!.Trim();
                s.SmtpPort = mail.SmtpPort <= 0 ? 587 : mail.SmtpPort;
                s.SmtpEnableSsl = mail.SmtpEnableSsl;
                s.SmtpUsername = string.IsNullOrWhiteSpace(mail.SmtpUsername) ? null : mail.SmtpUsername!.Trim();
                s.SmtpFromEmail = string.IsNullOrWhiteSpace(mail.SmtpFromEmail) ? null : mail.SmtpFromEmail!.Trim();
                s.SmtpPasswordProtectedBase64 = ProtectStringOrNull(mail.SmtpPassword);

                var json = JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsPath, json);
            }
            catch
            {
                // ignore
            }
        }
    }

    private static AppSettings? LoadAllInternal()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return null;
            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryParseHhMmToMinutes(string? s, out int minutes)
    {
        minutes = 0;
        s = (s ?? "").Trim();
        if (s.Length == 0) return false;
        if (TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out var ts))
        {
            var m = (int)ts.TotalMinutes;
            if (m < 0 || m >= 24 * 60) return false;
            minutes = m;
            return true;
        }
        return DateTime.TryParseExact(s, new[] { "HH:mm", "H:mm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
               && (minutes = dt.Hour * 60 + dt.Minute) >= 0 && minutes < 24 * 60;
    }

    /// <summary>Début (minute) et fin de journée « fermeture » (dernière fin de RDV autorisée).</summary>
    public (int workdayStartMin, int closingEndMin) LoadAgendaWorkdayMinutes()
    {
        lock (Gate)
        {
            var s = LoadAllInternal();
            var start = TryParseHhMmToMinutes(s?.AgendaWorkdayStart, out var sm) ? sm : 9 * 60;
            var close = TryParseHhMmToMinutes(s?.AgendaDayClosing, out var cm) ? cm : 21 * 60;
            if (close <= start) close = Math.Min(24 * 60 - 1, start + 60);
            return (start, close);
        }
    }

    public void SaveAgendaWorkday(string startHhMm, string closingHhMm)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(SettingsFolder);
                var s = LoadAllInternal() ?? new AppSettings();
                s.AgendaWorkdayStart = (startHhMm ?? "").Trim();
                s.AgendaDayClosing = (closingHhMm ?? "").Trim();
                var json = JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsPath, json);
            }
            catch
            {
                /* ignore */
            }
        }
    }

    /// <summary>Lunch : activé ou non, plage en minutes et libellés HH:mm pour l’UI.</summary>
    public (bool enabled, string startDisplay, string endDisplay, int startMin, int endMin) LoadAgendaLunch()
    {
        lock (Gate)
        {
            var s = LoadAllInternal();
            var enabled = s?.AgendaLunchEnabled == true;
            var hasSt = TryParseHhMmToMinutes(s?.AgendaLunchStart, out var sm);
            var hasEt = TryParseHhMmToMinutes(s?.AgendaLunchEnd, out var em);
            if (!hasSt) sm = 12 * 60;
            if (!hasEt) em = 13 * 60;
            if (em <= sm) em = Math.Min(24 * 60 - 1, sm + 60);
            return (
                enabled,
                AppointmentScheduling.FormatMinutesAsHhMm(sm),
                AppointmentScheduling.FormatMinutesAsHhMm(em),
                sm,
                em);
        }
    }

    public void SaveAgendaLunch(bool enabled, string? startHhMm, string? endHhMm)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(SettingsFolder);
                var s = LoadAllInternal() ?? new AppSettings();
                s.AgendaLunchEnabled = enabled;
                if (!string.IsNullOrWhiteSpace(startHhMm))
                    s.AgendaLunchStart = startHhMm.Trim();
                if (!string.IsNullOrWhiteSpace(endHhMm))
                    s.AgendaLunchEnd = endHhMm.Trim();
                var json = JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsPath, json);
            }
            catch
            {
                /* ignore */
            }
        }
    }

    public string LoadUiLanguage()
    {
        lock (Gate)
        {
            var s = LoadAllInternal();
            return UiLanguageService.Normalize(s?.UiLanguage);
        }
    }

    public void SaveUiLanguage(string? languageCode)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(SettingsFolder);
                var s = LoadAllInternal() ?? new AppSettings();
                s.UiLanguage = UiLanguageService.Normalize(languageCode);
                var json = JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsPath, json);
            }
            catch
            {
                /* ignore */
            }
        }
    }

    public LegalAcceptanceState LoadLegalAcceptance()
    {
        lock (Gate)
        {
            var s = LoadAllInternal();
            if (s is null)
                return default;

            DateTimeOffset? pAt = TryParseRoundtripUtc(s.PrivacyAcceptedAtUtc);
            DateTimeOffset? tAt = TryParseRoundtripUtc(s.TermsAcceptedAtUtc);

            return new LegalAcceptanceState
            {
                PrivacyAcceptedAtUtc = pAt,
                TermsAcceptedAtUtc = tAt,
                PrivacyDocVersionAccepted = string.IsNullOrWhiteSpace(s.PrivacyDocVersionAccepted) ? null : s.PrivacyDocVersionAccepted.Trim(),
                TermsDocVersionAccepted = string.IsNullOrWhiteSpace(s.TermsDocVersionAccepted) ? null : s.TermsDocVersionAccepted.Trim(),
                PrivacyContentSha256Accepted = string.IsNullOrWhiteSpace(s.PrivacyContentSha256Accepted) ? null : s.PrivacyContentSha256Accepted.Trim(),
                TermsContentSha256Accepted = string.IsNullOrWhiteSpace(s.TermsContentSha256Accepted) ? null : s.TermsContentSha256Accepted.Trim(),
            };
        }
    }

    /// <summary>
    /// Enregistre l'acceptation des deux documents, met à jour settings.json et ajoute une entrée d'audit
    /// (empreintes SHA-256 des textes acceptés, version d'app, utilisateur Windows).
    /// </summary>
    public void SaveLegalAcceptanceWithProof(string privacyFullText, string termsFullText)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(SettingsFolder);
                var now = DateTimeOffset.UtcNow;
                var nowIso = now.ToString("O", CultureInfo.InvariantCulture);

                var s = LoadAllInternal() ?? new AppSettings();
                s.PrivacyAcceptedAtUtc = nowIso;
                s.TermsAcceptedAtUtc = nowIso;
                s.PrivacyDocVersionAccepted = LegalDocuments.PrivacyPolicyVersion;
                s.TermsDocVersionAccepted = LegalDocuments.TermsOfServiceVersion;
                var settingsJson = JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true });
                var privacyHash = Sha256HexUtf8(privacyFullText);
                var termsHash = Sha256HexUtf8(termsFullText);
                s.PrivacyContentSha256Accepted = privacyHash;
                s.TermsContentSha256Accepted = termsHash;
                settingsJson = JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsPath, settingsJson);
                var appVer = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "";

                AppendLegalAuditEvent(new LegalAcceptanceAuditEvent
                {
                    RecordedAtUtc = nowIso,
                    PrivacyDocumentVersion = LegalDocuments.PrivacyPolicyVersion,
                    TermsDocumentVersion = LegalDocuments.TermsOfServiceVersion,
                    PrivacyContentSha256 = privacyHash,
                    TermsContentSha256 = termsHash,
                    AppAssemblyVersion = appVer,
                    WindowsUser = Environment.UserName,
                    MachineName = Environment.MachineName,
                });
            }
            catch
            {
                /* ignore */
            }
        }
    }

    private static void AppendLegalAuditEvent(LegalAcceptanceAuditEvent entry)
    {
        LegalAcceptanceAuditRoot root;
        try
        {
            if (File.Exists(LegalAuditPath))
            {
                var existing = File.ReadAllText(LegalAuditPath);
                root = JsonSerializer.Deserialize<LegalAcceptanceAuditRoot>(existing) ?? new LegalAcceptanceAuditRoot();
            }
            else
                root = new LegalAcceptanceAuditRoot();
        }
        catch
        {
            root = new LegalAcceptanceAuditRoot();
        }

        root.Events ??= new List<LegalAcceptanceAuditEvent>();
        root.Events.Add(entry);
        const int max = 40;
        if (root.Events.Count > max)
            root.Events = root.Events.Skip(root.Events.Count - max).ToList();

        var json = JsonSerializer.Serialize(root, AuditJsonOptions);
        File.WriteAllText(LegalAuditPath, json);
    }

    private static DateTimeOffset? TryParseRoundtripUtc(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return null;
        return DateTimeOffset.TryParse(iso, null, DateTimeStyles.RoundtripKind, out var dto)
            ? dto.ToUniversalTime()
            : null;
    }

    private static string Sha256HexUtf8(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text ?? "");
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private static string? ProtectStringOrNull(string? plain)
    {
        plain = (plain ?? "").Trim();
        if (plain.Length == 0) return null;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(plain);
            var prot = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(prot);
        }
        catch
        {
            return null;
        }
    }

    private static string? UnprotectStringOrNull(string? protectedBase64)
    {
        protectedBase64 = (protectedBase64 ?? "").Trim();
        if (protectedBase64.Length == 0) return null;
        try
        {
            var prot = Convert.FromBase64String(protectedBase64);
            var bytes = ProtectedData.Unprotect(prot, null, DataProtectionScope.CurrentUser);
            var plain = Encoding.UTF8.GetString(bytes);
            return string.IsNullOrWhiteSpace(plain) ? null : plain.Trim();
        }
        catch
        {
            return null;
        }
    }
}

public sealed class AppMailSettings
{
    public string? RecipientEmail { get; set; }
    public bool UseSmtp { get; set; }
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;
    public bool SmtpEnableSsl { get; set; } = true;
    public string? SmtpUsername { get; set; }
    public string? SmtpFromEmail { get; set; }
    public string? SmtpPassword { get; set; }
}

public sealed class LegalAcceptanceAuditRoot
{
    public List<LegalAcceptanceAuditEvent> Events { get; set; } = new();
}

/// <summary>Entrée d'audit locale (preuve d'acceptation : horodatage, versions, empreintes des textes).</summary>
public sealed class LegalAcceptanceAuditEvent
{
    public string RecordedAtUtc { get; set; } = "";
    public string PrivacyDocumentVersion { get; set; } = "";
    public string TermsDocumentVersion { get; set; } = "";
    public string PrivacyContentSha256 { get; set; } = "";
    public string TermsContentSha256 { get; set; } = "";
    public string AppAssemblyVersion { get; set; } = "";
    public string? WindowsUser { get; set; }
    public string? MachineName { get; set; }
}

