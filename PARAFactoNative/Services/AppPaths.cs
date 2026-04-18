using System;
using System.IO;

namespace PARAFactoNative.Services;

public static class AppPaths
{
    /// <summary>Racine stable sous LocalAppData (même chemin après mise à jour de l’installateur) : profil pro, DB, exports.</summary>
    public static string AppDataRoot
    {
        get
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PARAFactoNative");

            Directory.CreateDirectory(root);
            return root;
        }
    }

    public static string DbPath => Path.Combine(AppDataRoot, "parafacto.sqlite");
    public static string ExportsRoot => Path.Combine(AppDataRoot, "Exports");

    public static void EnsureDataDir()
    {
        var dir = Path.GetDirectoryName(DbPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);
    }
}
