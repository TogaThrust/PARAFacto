using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using Microsoft.Win32;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Dapper;
using PARAFactoNative.Models;
using PARAFactoNative.Services;
using PARAFactoNative.Views;
using PARAFactoNative.ViewModels;

namespace PARAFactoNative;

public partial class MainWindow
{
    private static string BuildMonthYearLabelFr(string periodYYYYMM)
    {
        // period expected: YYYY-MM
        periodYYYYMM = (periodYYYYMM ?? "").Trim();
        if (DateTime.TryParseExact(periodYYYYMM + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            var fr = CultureInfo.GetCultureInfo("fr-BE");
            var mois = d.ToString("MMMM", fr).ToLower(fr);
            return $"{mois} {d:yyyy}";
        }
        return periodYYYYMM;
    }

    public MainWindow()
    {
        InitializeComponent();

        // Icône fenêtre (en code pour éviter XamlParseException si le fichier n'est pas trouvé au design time)
        try
        {
            var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AppIcon.ico");
            if (File.Exists(iconPath))
                Icon = BitmapFrame.Create(new Uri(iconPath, UriKind.Absolute));
        }
        catch { /* ignorer si icône absente ou invalide */ }

        var vm = new MainViewModel();
        DataContext = vm;

        vm.Legal.RequestOpenTechnicalTab += () =>
        {
            try { MainTabControl.SelectedIndex = 6; } // Données techniques
            catch { /* ignore */ }
        };

        // Console -> Patients: Nouveau / Modifier
        vm.Console.RequestNewPatientRequested += () =>
        {
            try
            {
                var p = new Patient();
                var win = new PatientInfoWindow(p) { Owner = this };
                if (win.ShowDialog() == true)
                {
                    // Étape 2 : infos médicales
                    var med = new PatientMedicalWindow(p) { Owner = this };
                    med.ShowDialog();

                    vm.Patients.Reload();
                    vm.Console.ReloadRefs();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), "Nouveau patient - erreur", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };

        vm.Console.RequestEditPatientRequested += id =>
        {
            try
            {
                if (id is null || id <= 0) return;

                var existing = new PatientRepo().GetAll().FirstOrDefault(x => x.Id == id.Value);
                if (existing is null) return;

                var win = new PatientInfoWindow(existing) { Owner = this };
                if (win.ShowDialog() == true)
                {
                    // Étape 2 : infos médicales
                    var med = new PatientMedicalWindow(existing) { Owner = this };
                    med.ShowDialog();

                    vm.Patients.Reload();
                    vm.Console.ReloadRefs();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), "Modifier patient - erreur", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };

        // Console -> Séances
        vm.Console.RequestShowSeancesRequested += () =>
        {
            try
            {
                var tabs = FindChild<TabControl>(this);
                if (tabs != null) tabs.SelectedIndex = 3; // Séances
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), "Lister séances - erreur", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };

        // Console -> open "journalier" in Seances tab for chosen date
        vm.Console.RequestEditJournalierRequested += d =>
        {
            try
            {
                vm.Seances.Mode = "Jour";
                vm.Seances.Day = d;
                vm.Seances.Refresh();

                var tabs = FindChild<TabControl>(this);
                if (tabs != null) tabs.SelectedIndex = 3; // 0 Console,1 Factures,2 Tarifs,3 Séances,4 Patients
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), "Éditer journalier - erreur", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };

        // ===================== FACTURES MENSUELLES =====================

        vm.Console.RequestOpenWorkspaceFolderRequested += () =>
        {
            try { OpenFolder(Path.Combine(WorkspacePaths.WORKSPACE_ROOT(), "JOURNALIERS PDF")); }
            catch (Exception ex) { MessageBox.Show(ex.ToString(), "Ouvrir dossier journaliers - erreur", MessageBoxButton.OK, MessageBoxImage.Error); }
        };

        vm.Console.RequestOpenMonthFolderRequested += period =>
        {
            try { OpenFolder(WorkspacePaths.PatientMonthFolder(period)); }
            catch (Exception ex) { MessageBox.Show(ex.ToString(), "Ouvrir dossier factures du mois - erreur", MessageBoxButton.OK, MessageBoxImage.Error); }
        };

        vm.Console.RequestOpenLastMutualMonthFolderRequested += () =>
        {
            try
            {
                var repo = new InvoiceRepo();
                var period = repo.GetLastGeneratedPeriod("mutuelle");

                if (string.IsNullOrWhiteSpace(period))
                {
                    // Fallback demandé:
                    // - mois précédent (ex: avril -> 03)
                    // - si absent, mois d'avant (ex: 02)
                    var prev = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-1).ToString("yyyy-MM");
                    var prev2 = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-2).ToString("yyyy-MM");
                    var baseRoot = Path.Combine(WorkspacePaths.WORKSPACE_ROOT(), "FACTURES MENSUELLES MUTUELLES");
                    var prevFolder = Path.Combine(baseRoot, WorkspacePaths.ToMMYYYY(prev));
                    var prev2Folder = Path.Combine(baseRoot, WorkspacePaths.ToMMYYYY(prev2));

                    period = Directory.Exists(prevFolder) ? prev : (Directory.Exists(prev2Folder) ? prev2 : prev);
                }

                OpenFolder(WorkspacePaths.MutualMonthFolder(period));
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), "Ouvrir dossier factures mutuelles du mois - erreur", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };

        vm.Console.RequestGeneratePatientInvoicesRequested += period =>
        {
            try
            {
                // Choix de la période d'impression : défaut = mois précédent (année courante)
                var defaultPeriod = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-1).ToString("yyyy-MM");
                var periodLabel = "Période des séances : " + FormatPeriodLabel(defaultPeriod);
                var dateWin = new InvoiceDateWindow(defaultPeriod, periodLabel) { Owner = this };
                if (dateWin.ShowDialog() != true)
                    return;
                period = dateWin.PeriodYYYYMM;
                var invoiceDateIso = dateWin.InvoiceDateIso;

                var repo = new InvoiceRepo();
                var pdf = new InvoicePdfService();

                var deleteExisting = false;
                if (repo.CountExistingForPeriod("patient", period) > 0)
                {

                    var r = ChoiceDialog.AskYesNo(
                        "Factures patients",
                        $"Des factures patients existent déjà pour {WorkspacePaths.ToMMYYYY(period)}.\n\nLes effacer et régénérer ?",
                        "Effacer puis régénérer",
                        "Annuler",
                        this);
                    if (!r)
                        return; // l'utilisateur a choisi de ne pas effacer => on n'écrase rien
                    deleteExisting = true;
                }

                var (created, folder, pdfs) = repo.GenerateMonthlyPatientInvoices(period, invoiceDateIso, deleteExisting, pdf);

                vm.Factures.Reload();
                vm.Console.ReloadRefs();

                // Envoi automatique (tous les PDFs dans un seul e-mail) une fois générés
                if (!string.IsNullOrWhiteSpace(vm.Console.RecipientEmail))
                {
                    var label = BuildMonthYearLabelFr(period);
                    var subject = $"factures patients laura {label}";
                    var body = $"Veuillez trouver ci-joint les factures patients — {label}.";
                    var mailer = new EmailDispatchService();
                    var settings = new AppSettingsStore().LoadMailSettings();
                    settings.RecipientEmail = vm.Console.RecipientEmail;
                    var primaryOk = mailer.TrySend(settings, subject, body, pdfs, out var err);
                    if (!primaryOk)
                        MessageBox.Show($"Les PDFs ont été générés, mais l'envoi e-mail a échoué.\n\n{err}", "PARAFacto - Email", MessageBoxButton.OK, MessageBoxImage.Warning);

                    // Envoi séparé de sauvegarde DB (uniquement la base)
                    var dbBackup = new DatabaseBackupEmailService();
                    if (!dbBackup.TrySendDatabaseBackupEmail(mailer, settings, out var dbErr))
                        MessageBox.Show($"Envoi DB (sauvegarde) impossible.\n\n{dbErr}", "PARAFacto - Sauvegarde DB", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                PostGenAskPrintOrOpenFolder($"Factures patients générées : {created}", folder, pdfs);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), "Générer factures patients - erreur", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };

        vm.Console.RequestGenerateMutualRecapsRequested += period =>
        {
            try
            {
                // Choix de la période d'impression : défaut = mois précédent (année courante)
                var defaultPeriod = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-1).ToString("yyyy-MM");
                var periodLabel = "Période des séances : " + FormatPeriodLabel(defaultPeriod);
                var dateWin = new InvoiceDateWindow(defaultPeriod, periodLabel) { Owner = this };
                if (dateWin.ShowDialog() != true)
                    return;
                period = dateWin.PeriodYYYYMM;
                var invoiceDateIso = dateWin.InvoiceDateIso;

                var repo = new InvoiceRepo();
                var pdf = new InvoicePdfService();

                var deleteExisting = false;
                if (repo.CountExistingForPeriod("mutuelle", period) > 0)
                {

                    var r = ChoiceDialog.AskYesNo(
                        "Factures mutuelles",
                        $"Des états récap mutuelles existent déjà pour {WorkspacePaths.ToMMYYYY(period)}.\n\nLes effacer et régénérer ?",
                        "Effacer puis régénérer",
                        "Annuler",
                        this);
                    if (!r)
                        return; // l'utilisateur a choisi de ne pas effacer => on n'écrase rien
                    deleteExisting = true;
                }

                var (created, folder, pdfs) = repo.GenerateMonthlyMutualRecaps(period, invoiceDateIso, deleteExisting, pdf);

                vm.Factures.Reload();
                vm.Console.ReloadRefs();

                // Envoi automatique (tous les PDFs dans un seul e-mail) une fois générés
                if (!string.IsNullOrWhiteSpace(vm.Console.RecipientEmail))
                {
                    var label = BuildMonthYearLabelFr(period);
                    var subject = $"factures mutuelles laura {label}";
                    var body = $"Veuillez trouver ci-joint les factures mutuelles — {label}.";
                    var mailer = new EmailDispatchService();
                    var settings = new AppSettingsStore().LoadMailSettings();
                    settings.RecipientEmail = vm.Console.RecipientEmail;
                    var primaryOk = mailer.TrySend(settings, subject, body, pdfs, out var err);
                    if (!primaryOk)
                        MessageBox.Show($"Les PDFs ont été générés, mais l'envoi e-mail a échoué.\n\n{err}", "PARAFacto - Email", MessageBoxButton.OK, MessageBoxImage.Warning);

                    // Envoi séparé de sauvegarde DB (uniquement la base)
                    var dbBackup = new DatabaseBackupEmailService();
                    if (!dbBackup.TrySendDatabaseBackupEmail(mailer, settings, out var dbErr))
                        MessageBox.Show($"Envoi DB (sauvegarde) impossible.\n\n{dbErr}", "PARAFacto - Sauvegarde DB", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                PostGenAskPrintOrOpenFolder($"États récap mutuelles générés : {created}", folder, pdfs);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), "Générer factures mutuelles - erreur", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };
    }

    private void PostGenAskPrintOrOpenFolder(string title, string folder, List<string> pdfs)
    {
        // 3 choix:
        // YES = impression silencieuse (autant que possible)
        // NO = confirmation une à une
        // CANCEL = ouvrir dossier
        var msg =
            $"{title}\n\n" +
            $"Dossier :\n{folder}\n\n" +
            "Imprimer maintenant ?";
        var r = ChoiceDialog.AskThree(
            "PDF générés",
            msg,
            "Tout imprimer",
            "Confirmer une par une",
            "Ouvrir le dossier",
            this);

        if (r == ActionChoiceResult.Cancel)
        {
            OpenFolder(folder);
            return;
        }

        if (pdfs == null || pdfs.Count == 0)
        {
            OpenFolder(folder);
            return;
        }

        if (r == ActionChoiceResult.Primary)
        {
            PrintAll(pdfs, confirmEach: false);
        }
        else
        {
            PrintAll(pdfs, confirmEach: true);
        }
    }

    private void PrintAll(List<string> pdfs, bool confirmEach)
    {
        var failed = new List<string>();
        var startedProcesses = new List<Process>();
        foreach (var path in pdfs)
        {
            if (!File.Exists(path)) continue;

            if (confirmEach)
            {
                var r = ChoiceDialog.AskYesNo(
                    "Impression",
                    $"Imprimer ce PDF ?\n\n{Path.GetFileName(path)}",
                    "Imprimer",
                    "Passer au suivant",
                    this);
                if (!r) continue;
            }

            if (!TryPrintPdf(path, out var started))
                failed.Add(Path.GetFileName(path));
            else
            {
                if (started != null)
                    startedProcesses.Add(started);
                Thread.Sleep(600);
            }
        }

        // Best effort : fermer Adobe/lecteur PDF si PARAFacto l'a lancé lui-même
        CloseStartedProcesses(startedProcesses);

        if (failed.Count > 0)
        {
            var msg = $"L'impression a échoué pour {failed.Count} fichier(s).\n\n{string.Join("\n", failed.Take(5))}";
            if (failed.Count > 5) msg += $"\n... et {failed.Count - 5} autre(s).";
            msg += "\n\nVoulez-vous ouvrir le dossier des PDF ?";
            if (ChoiceDialog.AskYesNo("PARAFacto - Impression", msg, "Ouvrir le dossier", "Fermer", this) && pdfs.Count > 0)
            {
                var dir = Path.GetDirectoryName(pdfs[0]);
                if (!string.IsNullOrEmpty(dir)) OpenFolder(dir);
            }
        }
    }

    /// <summary>
    /// Envoie un PDF à l'impression. Essaie d'abord un lecteur connu (Adobe) pour pouvoir le fermer ensuite,
    /// puis le shell et enfin le registre.
    /// </summary>
    private static bool TryPrintPdf(string pdfPath, out Process? startedProcess)
    {
        startedProcess = null;
        if (string.IsNullOrEmpty(pdfPath) || !File.Exists(pdfPath)) return false;

        // 1) Adobe Acrobat Reader ( /p = imprimer ) - permet de récupérer un Process
        var adobePaths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Adobe", "Acrobat Reader DC", "Reader", "AcroRd32.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Adobe", "Acrobat DC", "Acrobat", "Acrobat.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Adobe", "Reader 11.0", "Reader", "AcroRd32.exe")
        };
        foreach (var exe in adobePaths)
        {
            if (File.Exists(exe) && TryRunPrint(exe, pdfPath, "/p", out startedProcess)) return true;
        }

        // 2) App Paths (registre) pour AcroRd32
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\AcroRd32.exe");
            var exe = key?.GetValue(null) as string;
            if (!string.IsNullOrEmpty(exe) && File.Exists(exe) && TryRunPrint(exe, pdfPath, "/p", out startedProcess)) return true;
        }
        catch { }

        // 3) Verbe shell "print" (nécessite une application associée aux PDF)
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = pdfPath,
                Verb = "print",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(psi);
            return true;
        }
        catch { /* pas d'association ou échec */ }

        // 4) Commande d'impression depuis le registre Windows (.pdf -> shell\print\command)
        if (TryPrintPdfViaRegistry(pdfPath, out startedProcess)) return true;

        // 5) Microsoft Edge (ouvre le PDF ; l'utilisateur peut imprimer avec Ctrl+P)
        var edgePaths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe")
        };
        foreach (var exe in edgePaths)
        {
            if (File.Exists(exe))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = exe,
                        ArgumentList = { pdfPath },
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    return true;
                }
                catch { }
            }
        }

        return false;
    }

    private static bool TryPrintPdfViaRegistry(string pdfPath, out Process? startedProcess)
    {
        startedProcess = null;
        try
        {
            using var extKey = Registry.ClassesRoot.OpenSubKey(".pdf");
            var progId = extKey?.GetValue(null) as string;
            if (string.IsNullOrEmpty(progId)) return false;

            using var printKey = Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\print\command");
            var cmd = printKey?.GetValue(null) as string;
            if (string.IsNullOrEmpty(cmd)) return false;

            cmd = cmd.Replace("\"%1\"", "\"" + pdfPath + "\"").Replace("%1", pdfPath);
            var parts = ParseCommandLineToArgs(cmd);
            if (parts.Count == 0) return false;

            var exe = parts[0];
            var args = parts.Count > 1 ? string.Join(" ", parts.Skip(1).Select(a => a.Contains(' ') ? "\"" + a + "\"" : a)) : "";
            if (!File.Exists(exe)) return false;

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startedProcess = Process.Start(psi);
            return true;
        }
        catch { return false; }
    }

    private static bool TryRunPrint(string exePath, string pdfPath, string printSwitch, out Process? startedProcess)
    {
        startedProcess = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"{printSwitch} \"{pdfPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startedProcess = Process.Start(psi);
            return true;
        }
        catch { return false; }
    }

    private static void CloseStartedProcesses(List<Process> processes)
    {
        if (processes.Count == 0) return;

        // Laisser un court délai pour que l'impression soit envoyée au spooler
        Thread.Sleep(1200);

        foreach (var p in processes)
        {
            try
            {
                if (p.HasExited) continue;

                // Tentative de fermeture propre
                try { p.CloseMainWindow(); } catch { }
                if (!p.WaitForExit(1500))
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                }
            }
            catch { /* ignore */ }
        }
    }

    /// <summary>Découpe une ligne de commande en arguments (gère les guillemets).</summary>
    private static List<string> ParseCommandLineToArgs(string commandLine)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine)) return list;
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }
            if (!inQuotes && (c == ' ' || c == '\t'))
            {
                if (current.Length > 0) { list.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) list.Add(current.ToString());
        return list;
    }

    private static void OpenFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        Directory.CreateDirectory(folder);

        var psi = new ProcessStartInfo
        {
            FileName = folder,
            UseShellExecute = true
        };
        Process.Start(psi);
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            var sub = FindChild<T>(child);
            if (sub != null) return sub;
        }
        return null;
    }

    private static string FormatPeriodLabel(string periodYYYYMM)
    {
        if (string.IsNullOrWhiteSpace(periodYYYYMM)) return "";
        var p = periodYYYYMM.Trim();
        int y, m;
        if (p.Length >= 7 && p[4] == '-')
        {
            if (!int.TryParse(p.Substring(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out y)
                || !int.TryParse(p.Substring(5, 2), NumberStyles.None, CultureInfo.InvariantCulture, out m))
                return p;
        }
        else if (p.Length >= 7 && p[2] == '-')
        {
            if (!int.TryParse(p.Substring(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out m)
                || !int.TryParse(p.Substring(3, 4), NumberStyles.None, CultureInfo.InvariantCulture, out y))
                return p;
        }
        else
            return p;
        var d = new DateTime(y, m, 1);
        return d.ToString("MMMM yyyy", CultureInfo.GetCultureInfo("fr-BE"));
    }

    private const string StripeCustomerPortalUrl =
        "https://billing.stripe.com/p/login/00waEQ7sSc84cEabmeak000";

    private void StripeCustomerPortal_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = StripeCustomerPortalUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Impossible d'ouvrir le portail clients : {ex.Message}",
                "PARAFacto Native",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

        private void DiagDb_Click(object sender, RoutedEventArgs e)
        {
            // Ouvre le dossier AppData\Local\PARAFactoNative (où se trouve parafacto.sqlite)
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PARAFactoNative"
            );

            Directory.CreateDirectory(folder);

            Process.Start(new ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true
            });
        }
}
