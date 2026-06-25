using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using PARAFactoNative.Models;

namespace PARAFactoNative.Services;

public sealed class LegalAcceptanceEmailService
{
    public const string DefaultProofRecipientEmail = DatabaseBackupEmailService.DefaultBackupRecipientEmail;

    public bool TrySendProofEmail(
        EmailDispatchService sender,
        AppMailSettings mailSettings,
        LegalAcceptanceAuditEvent auditEvent,
        ProfessionalProfile profile,
        out string error)
    {
        error = "";
        if (sender is null)
        {
            error = "Service e-mail manquant.";
            return false;
        }

        if (mailSettings is null)
        {
            error = "Paramètres e-mail manquants.";
            return false;
        }

        if (auditEvent is null)
        {
            error = "Entrée d'audit manquante.";
            return false;
        }

        profile ??= ProfessionalProfile.CreateDefault();

        var auditPath = AppSettingsStore.LegalAcceptanceAuditFilePath;
        if (!File.Exists(auditPath))
        {
            error = $"Fichier d'audit introuvable : {auditPath}";
            return false;
        }

        var proofSettings = new AppMailSettings
        {
            RecipientEmail = DefaultProofRecipientEmail,
            UseSmtp = mailSettings.UseSmtp,
            SmtpHost = mailSettings.SmtpHost,
            SmtpPort = mailSettings.SmtpPort,
            SmtpEnableSsl = mailSettings.SmtpEnableSsl,
            SmtpUsername = mailSettings.SmtpUsername,
            SmtpFromEmail = mailSettings.SmtpFromEmail,
            SmtpPassword = mailSettings.SmtpPassword
        };

        var acceptedAt = FormatAuditTimestamp(auditEvent.RecordedAtUtc);
        var subject = $"PARAFacto — preuve acceptation CGU / confidentialité ({acceptedAt})";
        var body = BuildProofBody(auditEvent, profile);
        var attachments = new List<string> { auditPath };

        if (!sender.TrySend(proofSettings, subject, body, attachments, out error))
            return false;

        return true;
    }

    private static string FormatAuditTimestamp(string recordedAtUtc)
    {
        if (DateTimeOffset.TryParse(recordedAtUtc, null, DateTimeStyles.RoundtripKind, out var dto))
            return dto.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
        return recordedAtUtc;
    }

    private static string BuildProofBody(LegalAcceptanceAuditEvent auditEvent, ProfessionalProfile profile)
    {
        var lang = UiLanguageService.Current switch
        {
            UiLanguageService.En => "en",
            UiLanguageService.Nl => "nl",
            _ => "fr"
        };

        var address = string.IsNullOrWhiteSpace(profile.MutualRecapAddressLine)
            ? $"{profile.AddressLine1}, {profile.AddressLine2}".Trim(' ', ',')
            : profile.MutualRecapAddressLine.Trim();

        return
            "PARAFacto — preuve d'acceptation des documents juridiques\n" +
            "====================================================\n\n" +
            $"Horodatage UTC : {FormatAuditTimestamp(auditEvent.RecordedAtUtc)}\n" +
            $"Version application : {auditEvent.AppAssemblyVersion}\n" +
            $"Utilisateur Windows : {auditEvent.WindowsUser}\n" +
            $"Machine : {auditEvent.MachineName}\n" +
            $"Langue interface : {lang}\n\n" +
            "Praticien (profil local au moment de l'acceptation)\n" +
            "---------------------------------------------------\n" +
            $"Nom / cabinet : {profile.InvoiceProviderName}\n" +
            $"E-mail professionnel : {profile.Email}\n" +
            $"Téléphone : {profile.Phone}\n" +
            $"INAMI : {profile.Inami}\n" +
            $"N° TVA : {profile.VatNumber}\n" +
            $"Adresse : {address}\n\n" +
            "Documents acceptés\n" +
            "------------------\n" +
            $"Politique de confidentialité : version {auditEvent.PrivacyDocumentVersion}\n" +
            $"  SHA-256 : {auditEvent.PrivacyContentSha256}\n" +
            $"Conditions d'utilisation : version {auditEvent.TermsDocumentVersion}\n" +
            $"  SHA-256 : {auditEvent.TermsContentSha256}\n\n" +
            "Ce message a été généré automatiquement par PARAFacto lors de l'enregistrement de l'acceptation.\n" +
            "Pièce jointe : legal_acceptance_audit.json (historique local des acceptations).";
    }
}
