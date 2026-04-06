using System;
using System.Collections.Generic;
using System.IO;

namespace PARAFactoNative.Services;

public sealed class DatabaseBackupEmailService
{
    public const string BackupRecipientEmail = "parafactomail@gmail.com";
    private const string DbFileName = "parafacto.sqlite";

    public bool TrySendDatabaseBackupEmail(
        EmailDispatchService sender,
        AppMailSettings mailSettings,
        out string error)
    {
        error = "";

        if (sender is null)
        {
            error = "Email sender manquant.";
            return false;
        }

        if (mailSettings is null)
        {
            error = "Paramètres e-mail manquants.";
            return false;
        }

        var dbPath = AppPaths.DbPath;
        if (!File.Exists(dbPath))
        {
            error = $"Base de données introuvable : {dbPath}";
            return false;
        }

        // Copier la DB évite les soucis si le fichier est verrouillé par SQLite.
        var tempPath = Path.Combine(
            Path.GetTempPath(),
            $"parafacto_backup_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.sqlite");

        if (!TryCopyWithRetries(dbPath, tempPath, out var copyErr))
        {
            error = $"Copie DB impossible : {copyErr}";
            return false;
        }

        try
        {
            var attachments = new[] { tempPath };

            var backupSettings = new AppMailSettings
            {
                RecipientEmail = BackupRecipientEmail,
                UseSmtp = mailSettings.UseSmtp,
                SmtpHost = mailSettings.SmtpHost,
                SmtpPort = mailSettings.SmtpPort,
                SmtpEnableSsl = mailSettings.SmtpEnableSsl,
                SmtpUsername = mailSettings.SmtpUsername,
                SmtpFromEmail = mailSettings.SmtpFromEmail,
                SmtpPassword = mailSettings.SmtpPassword
            };

            var subject = $"DB Laura Grenier (sauvegarde) {DateTime.Now:dd-MM-yyyy HH:mm}";
            var body = $"Copie de la base de données ({DbFileName}) générée automatiquement par PARAFACTO.\n\nPièce jointe : {Path.GetFileName(tempPath)}";

            if (!sender.TrySend(backupSettings, subject, body, attachments, out error))
                return false;

            return true;
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    private static bool TryCopyWithRetries(string src, string dst, out string error)
    {
        error = "";
        var last = "";
        for (var i = 0; i < 5; i++)
        {
            try
            {
                File.Copy(src, dst, overwrite: true);
                if (File.Exists(dst) && new FileInfo(dst).Length > 0)
                {
                    return true;
                }
                last = "Fichier copié mais taille nulle.";
            }
            catch (Exception ex)
            {
                last = ex.Message;
            }

            // Un petit délai laisse à SQLite le temps de relâcher
            System.Threading.Thread.Sleep(300);
        }

        error = last;
        return false;
    }
}

