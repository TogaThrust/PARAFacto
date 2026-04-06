using System;
using System.IO;

namespace PARAFactoNative.Services;

public static class WorkspacePaths
{
    // Root: Documents\PARAFACTO_Native
    public static string WORKSPACE_ROOT()
    {
        // Prefer OneDrive\Documents if present (common Windows setup)
        var oneDrive = Environment.GetEnvironmentVariable("OneDrive") 
                       ?? Environment.GetEnvironmentVariable("OneDriveConsumer")
                       ?? Environment.GetEnvironmentVariable("OneDriveCommercial");
        if (!string.IsNullOrWhiteSpace(oneDrive))
        {
            var odDocs = Path.Combine(oneDrive, "Documents");
            var odRoot = Path.Combine(odDocs, "PARAFACTO_Native");
            if (Directory.Exists(odRoot)) return odRoot;
        }

        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(docs, "PARAFACTO_Native");
    }

    // Legacy helpers (kept for compatibility with older code)
    public static string FACTURES_PDF_FOLDER()
        => EnsureFolder(Path.Combine(WORKSPACE_ROOT(), "FACTURES_PDF"));

    public static string JOURNALIERS_PDF_FOLDER()
        => EnsureFolder(Path.Combine(WORKSPACE_ROOT(), "JOURNALIERS PDF"));

    // New structured folders
    public static string FACTURES_MENSUELLES_PATIENTS_ROOT()
        => EnsureFolder(Path.Combine(WORKSPACE_ROOT(), "FACTURES MENSUELLES PATIENTS"));

    /// <summary>Dossier des factures patients déjà payées en entier (ex. cash).</summary>
    public static string FACTURES_MENSUELLES_PATIENTS_ACQUITTEES_ROOT()
        => EnsureFolder(Path.Combine(WORKSPACE_ROOT(), "FACTURES MENSUELLES PATIENTS ACQUITTEES"));

    public static string FACTURES_MENSUELLES_MUTUELLES_ROOT()
        => EnsureFolder(Path.Combine(WORKSPACE_ROOT(), "FACTURES MENSUELLES MUTUELLES"));

    public static string NOTES_CREDIT_PATIENTS_ROOT()
        => EnsureFolder(Path.Combine(WORKSPACE_ROOT(), "NOTES DE CREDIT PATIENTS"));

    public static string NOTES_CREDIT_MUTUELLES_ROOT()
        => EnsureFolder(Path.Combine(WORKSPACE_ROOT(), "NOTES DE CREDIT MUTUELLES"));

    public static string PatientMonthFolder(string periodYYYYMM)
        => EnsureFolder(Path.Combine(FACTURES_MENSUELLES_PATIENTS_ROOT(), ToMMYYYY(periodYYYYMM)));

    /// <summary>Dossier du mois pour les factures patients acquittées (payées en entier).</summary>
    public static string PatientAcquittedMonthFolder(string periodYYYYMM)
        => EnsureFolder(Path.Combine(FACTURES_MENSUELLES_PATIENTS_ACQUITTEES_ROOT(), ToMMYYYY(periodYYYYMM)));

    /// <summary>Sous-dossier "FACTURES ACQUITTEES" sous le dossier du mois (factures réglées au cabinet / cash).</summary>
    public static string PatientMonthAcquittedSubfolder(string periodYYYYMM)
        => EnsureFolder(Path.Combine(PatientMonthFolder(periodYYYYMM), "FACTURES ACQUITTEES"));

    /// <summary>Sous-dossier "NC" (notes de crédit) sous le dossier du mois patients.</summary>
    public static string PatientMonthFolderNc(string periodYYYYMM)
        => EnsureFolder(Path.Combine(PatientMonthFolder(periodYYYYMM), "NC"));

    /// <summary>Sous-dossier "NC" (notes de crédit) sous le dossier du mois mutuelles.</summary>
    public static string MutualMonthFolderNc(string periodYYYYMM)
        => EnsureFolder(Path.Combine(MutualMonthFolder(periodYYYYMM), "NC"));

    public static string MutualMonthFolder(string periodYYYYMM)
        => EnsureFolder(Path.Combine(FACTURES_MENSUELLES_MUTUELLES_ROOT(), ToMMYYYY(periodYYYYMM)));

    public static string MutualMonthFolder(string periodYYYYMM, string mutualName)
        => EnsureFolder(Path.Combine(MutualMonthFolder(periodYYYYMM), MakeSafeFolderName(mutualName)));

    public static string CreditNotePatientsMonthFolder(string periodYYYYMM)
        => EnsureFolder(Path.Combine(NOTES_CREDIT_PATIENTS_ROOT(), ToMMYYYY(periodYYYYMM)));

    public static string CreditNoteMutuellesMonthFolder(string periodYYYYMM)
        => EnsureFolder(Path.Combine(NOTES_CREDIT_MUTUELLES_ROOT(), ToMMYYYY(periodYYYYMM)));

    public static string ToMMYYYY(string periodYYYYMM)
    {
        // Accepts "YYYY-MM" or "MM-YYYY"
        if (string.IsNullOrWhiteSpace(periodYYYYMM)) return "00-0000";
        var p = periodYYYYMM.Trim();

        if (p.Length == 7 && p[4] == '-') // YYYY-MM
        {
            var yyyy = p.Substring(0, 4);
            var mm = p.Substring(5, 2);
            return $"{mm}-{yyyy}";
        }

        if (p.Length == 7 && p[2] == '-') // MM-YYYY
            return p;

        return p.Replace('/', '-').Replace('\\', '-');
    }

    // --- Root discovery / path helpers (used by ViewModels) ---
    public static string? GetRootOrNull()
    {
        var root = WORKSPACE_ROOT();
        return Directory.Exists(root) ? root : null;
    }

    /// <summary>
    /// Returns the workspace root and ensures it exists.
    /// Kept as a string-returning method because several ViewModels expect that signature.
    /// </summary>
    public static string TryFindWorkspaceRoot()
        => EnsureFolder(WORKSPACE_ROOT());

    /// <summary>
    /// Resolves a relative path (stored in DB) to an absolute path under the workspace root.
    /// If the input is already an absolute path, it is returned as-is.
    /// </summary>
    public static string ResolvePath(string root, string? relativeOrAbsolute)
    {
        if (string.IsNullOrWhiteSpace(root))
            root = TryFindWorkspaceRoot();

        if (string.IsNullOrWhiteSpace(relativeOrAbsolute))
            return root;

        if (Path.IsPathRooted(relativeOrAbsolute))
            return relativeOrAbsolute;

        return Path.GetFullPath(Path.Combine(root, relativeOrAbsolute));
    }

    public static string ResolvePath(string? relativeOrAbsolute)
        => ResolvePath(TryFindWorkspaceRoot(), relativeOrAbsolute);

    /// <summary>
    /// Returns a relative path from the root to the given absolute path (for DB storage).
    /// If it cannot be made relative, returns the input unchanged.
    /// </summary>
    public static string MakeRelativeToRoot(string root, string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(root))
            root = TryFindWorkspaceRoot();

        if (string.IsNullOrWhiteSpace(absolutePath))
            return string.Empty;

        try
        {
            return Path.GetRelativePath(root, absolutePath);
        }
        catch
        {
            return absolutePath;
        }
    }

    private static string EnsureFolder(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    private static string MakeSafeFolderName(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "SANS_NOM";
        foreach (var ch in Path.GetInvalidFileNameChars())
            s = s.Replace(ch, '_');
        return s.Trim();
    }
}