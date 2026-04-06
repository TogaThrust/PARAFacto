using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using PARAFactoNative.Services;

namespace PARAFactoNative;

public partial class App : Application
{
    private static string LogPath => Path.Combine(Services.AppPaths.AppDataRoot, "startup_error.txt");

    protected override void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogAndShow("Erreur non gérée", args.ExceptionObject as Exception);
        DispatcherUnhandledException += (_, args) =>
        {
            // Certains environnements .NET/WPF déclenchent une exception interne de télémétrie au shutdown
            // (assembly System.Diagnostics.Tracing non trouvé). Ce bruit ne doit pas bloquer l'utilisateur.
            if (args.Exception is FileNotFoundException fnf
                && (fnf.FileName?.Contains("System.Diagnostics.Tracing", StringComparison.OrdinalIgnoreCase) ?? false)
                && (fnf.StackTrace?.Contains("ControlsTraceLogger", StringComparison.OrdinalIgnoreCase) ?? false))
            {
                args.Handled = true;
                return;
            }

            LogAndShow("Erreur interface", args.Exception);
            args.Handled = true;
        };

        base.OnStartup(e);

        // Afficher le message Adobe uniquement si aucun lecteur PDF capable d'imprimer n'est détecté
        try
        {
            if (!HasPdfPrintAssociation())
            {
                var result = ChoiceDialog.AskYesNo(
                    "PARAFacto Native - Pré-requis impression PDF",
                    "Pour que l'impression automatique des documents (factures, journaux, etc.) fonctionne, PARAFacto Native a besoin d'un lecteur PDF capable d'imprimer les fichiers (par exemple Adobe Acrobat Reader) et configuré comme application par défaut pour les fichiers PDF.\n\n" +
                    "Souhaitez-vous ouvrir maintenant la page de téléchargement d'Adobe Acrobat Reader ?",
                    "Ouvrir la page",
                    "Fermer");

                if (result)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "https://get.adobe.com/fr/reader/",
                            UseShellExecute = true
                        });
                    }
                    catch
                    {
                        // Si l'ouverture du navigateur échoue, on ignore simplement.
                    }
                }
            }
        }
        catch
        {
            // On ne bloque pas le démarrage de l'application si le test échoue.
        }
        try
        {
            Services.DbBootstrapper.EnsureDatabase();
        }
        catch (Exception ex)
        {
            LogAndShow("Initialisation base de données", ex);
            Shutdown(-1);
        }

        try
        {
            Services.BelgianHolidayHelper.Initialize();
        }
        catch
        {
            // Catalogue fériés optionnel : ne bloque pas le démarrage.
        }

        try
        {
            if (!SubscriptionVerificationService.RunStartupGate())
            {
                Shutdown(0);
                return;
            }
        }
        catch (Exception ex)
        {
            LogAndShow("Vérification abonnement", ex);
            Shutdown(-1);
            return;
        }

        try
        {
            var main = new MainWindow();
            MainWindow = main;
            main.Show();
        }
        catch (Exception ex)
        {
            LogAndShow("Ouverture fenêtre principale", ex);
            Shutdown(-1);
        }
    }

    /// <summary>
    /// Vérifie si Windows a une application associée pour l'action "print" sur les fichiers PDF
    /// (ex. Adobe Acrobat Reader). Si oui, l'impression automatique depuis l'app fonctionnera.
    /// </summary>
    private static bool HasPdfPrintAssociation()
    {
        string tempPdf = Path.Combine(Path.GetTempPath(), "PARAFactoNative_test_print_" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            // Fichier PDF minimal valide (évite qu'un lecteur PDF rejette un fichier vide)
            byte[] minimalPdf =
            {
                0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x30, 0x0A, 0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A, 0x31,
                0x20, 0x30, 0x20, 0x6F, 0x62, 0x6A, 0x0A, 0x3C, 0x3C, 0x2F, 0x54, 0x79, 0x70, 0x65, 0x2F, 0x43,
                0x61, 0x74, 0x61, 0x6C, 0x6F, 0x67, 0x2F, 0x50, 0x61, 0x67, 0x65, 0x73, 0x20, 0x32, 0x20, 0x30,
                0x20, 0x52, 0x3E, 0x3E, 0x65, 0x6E, 0x64, 0x6F, 0x62, 0x6A, 0x0A, 0x32, 0x20, 0x30, 0x20, 0x6F,
                0x62, 0x6A, 0x0A, 0x3C, 0x3C, 0x2F, 0x54, 0x79, 0x70, 0x65, 0x2F, 0x50, 0x61, 0x67, 0x65, 0x73,
                0x2F, 0x4B, 0x69, 0x64, 0x73, 0x5B, 0x33, 0x20, 0x30, 0x20, 0x52, 0x5D, 0x2F, 0x43, 0x6F, 0x75,
                0x6E, 0x74, 0x20, 0x31, 0x3E, 0x3E, 0x65, 0x6E, 0x64, 0x6F, 0x62, 0x6A, 0x0A, 0x33, 0x20, 0x30,
                0x20, 0x6F, 0x62, 0x6A, 0x0A, 0x3C, 0x3C, 0x2F, 0x54, 0x79, 0x70, 0x65, 0x2F, 0x50, 0x61, 0x67,
                0x65, 0x2F, 0x4D, 0x65, 0x64, 0x69, 0x61, 0x42, 0x6F, 0x78, 0x5B, 0x30, 0x20, 0x30, 0x20, 0x36,
                0x31, 0x32, 0x20, 0x37, 0x39, 0x32, 0x5D, 0x2F, 0x50, 0x61, 0x72, 0x65, 0x6E, 0x74, 0x20, 0x32,
                0x20, 0x30, 0x20, 0x52, 0x3E, 0x3E, 0x65, 0x6E, 0x64, 0x6F, 0x62, 0x6A, 0x0A, 0x78, 0x72, 0x65,
                0x66, 0x0A, 0x30, 0x20, 0x34, 0x0A, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30,
                0x20, 0x36, 0x35, 0x35, 0x33, 0x35, 0x20, 0x66, 0x20, 0x0A, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30,
                0x30, 0x30, 0x30, 0x39, 0x20, 0x30, 0x30, 0x30, 0x30, 0x30, 0x20, 0x6E, 0x20, 0x0A, 0x30, 0x30,
                0x30, 0x30, 0x30, 0x30, 0x30, 0x35, 0x32, 0x20, 0x30, 0x30, 0x30, 0x30, 0x30, 0x20, 0x6E, 0x20,
                0x0A, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x31, 0x30, 0x31, 0x20, 0x30, 0x30, 0x30, 0x30, 0x30,
                0x20, 0x6E, 0x20, 0x0A, 0x74, 0x72, 0x61, 0x69, 0x6C, 0x65, 0x72, 0x0A, 0x3C, 0x3C, 0x2F, 0x53,
                0x69, 0x7A, 0x65, 0x20, 0x34, 0x2F, 0x52, 0x6F, 0x6F, 0x74, 0x20, 0x31, 0x20, 0x30, 0x20, 0x52,
                0x3E, 0x3E, 0x0A, 0x73, 0x74, 0x61, 0x72, 0x74, 0x78, 0x72, 0x65, 0x66, 0x0A, 0x31, 0x37, 0x38,
                0x0A, 0x25, 0x25, 0x45, 0x4F, 0x46
            };
            File.WriteAllBytes(tempPdf, minimalPdf);
            var psi = new ProcessStartInfo
            {
                FileName = tempPdf,
                Verb = "print",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            try
            {
                using var process = Process.Start(psi);
                if (process != null)
                    try { process.Kill(); } catch { } // Évite qu'une boîte d'impression reste ouverte
                return true;
            }
            catch (Win32Exception ex)
            {
                const int ERROR_NO_ASSOCIATION = 1155;
                return ex.NativeErrorCode != ERROR_NO_ASSOCIATION;
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            try { if (File.Exists(tempPdf)) File.Delete(tempPdf); } catch { }
        }
    }

    private static void LogAndShow(string context, Exception? ex)
    {
        var msg = ex?.ToString() ?? "?";
        try
        {
            Services.AppPaths.EnsureDataDir();
            File.WriteAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} — {context}\r\n\r\n{msg}");
        }
        catch { /* ignore */ }
        MessageBox.Show(
            $"{context} :\n\n{ex?.Message}\n\nDétails enregistrés dans :\n{LogPath}",
            "PARAFacto Native — Erreur",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}
