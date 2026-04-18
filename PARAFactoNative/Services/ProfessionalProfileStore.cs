using System;
using System.IO;
using System.Text.Json;
using PARAFactoNative.Models;

namespace PARAFactoNative.Services;

/// <summary>
/// Persistance des données professionnelles (JSON + logo dans <c>%LocalAppData%\PARAFactoNative\branding\</c>).
/// Le chemin ne dépend pas de la version de l’application : une mise à jour conserve le fichier et le logo.
/// </summary>
public static class ProfessionalProfileStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Fichier copié à côté de l’exécutable (voir csproj) pour amorcer le logo au premier lancement.</summary>
    public const string BundledDefaultLogoFileName = "default_professional_logo.png";

    public static event Action? ProfileChanged;

    private static string JsonPath => Path.Combine(AppPaths.AppDataRoot, "professional_profile.json");

    public static string BrandingDirectory
    {
        get
        {
            var d = Path.Combine(AppPaths.AppDataRoot, "branding");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    /// <summary>
    /// Premier lancement : si aucun <c>professional_profile.json</c>, enregistre les valeurs par défaut
    /// et copie le logo fourni avec l’app vers <c>branding/logo.png</c>.
    /// </summary>
    public static void EnsureFirstRunDefaults()
    {
        var created = false;
        lock (Gate)
        {
            try
            {
                if (File.Exists(JsonPath))
                    return;

                Directory.CreateDirectory(AppPaths.AppDataRoot);
                var profile = ProfessionalProfile.CreateDefault();
                var bundled = Path.Combine(AppContext.BaseDirectory, "Assets", BundledDefaultLogoFileName);
                if (!File.Exists(bundled))
                    bundled = Path.Combine(AppContext.BaseDirectory, BundledDefaultLogoFileName);
                if (File.Exists(bundled))
                {
                    var branding = BrandingDirectory;
                    var destName = "logo.png";
                    var dest = Path.Combine(branding, destName);
                    File.Copy(bundled, dest, overwrite: true);
                    profile.LogoRelativeFileName = destName;
                }

                File.WriteAllText(JsonPath, JsonSerializer.Serialize(profile, JsonOptions));
                created = true;
            }
            catch
            {
                // Ne pas bloquer le démarrage (fichier verrouillé, etc.).
            }
        }

        if (created)
            ProfileChanged?.Invoke();
    }

    public static ProfessionalProfile Load()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(JsonPath))
                    return ProfessionalProfile.CreateDefault();
                var json = File.ReadAllText(JsonPath);
                var loaded = JsonSerializer.Deserialize<ProfessionalProfile>(json);
                return loaded ?? ProfessionalProfile.CreateDefault();
            }
            catch
            {
                return ProfessionalProfile.CreateDefault();
            }
        }
    }

    /// <summary>Enregistre le profil. Si <paramref name="logoSourcePath"/> est renseigné, copie le fichier dans branding/.</summary>
    public static void Save(ProfessionalProfile profile, string? logoSourcePath = null)
    {
        if (profile is null) throw new ArgumentNullException(nameof(profile));

        lock (Gate)
        {
            Directory.CreateDirectory(AppPaths.AppDataRoot);
            var branding = BrandingDirectory;

            if (!string.IsNullOrWhiteSpace(logoSourcePath) && File.Exists(logoSourcePath))
            {
                var ext = Path.GetExtension(logoSourcePath);
                if (string.IsNullOrWhiteSpace(ext) || ext.Length > 10)
                    ext = ".png";
                var destName = "logo" + ext.ToLowerInvariant();
                var dest = Path.Combine(branding, destName);
                File.Copy(logoSourcePath, dest, overwrite: true);
                profile.LogoRelativeFileName = destName;
            }

            var json = JsonSerializer.Serialize(profile, JsonOptions);
            File.WriteAllText(JsonPath, json);
        }

        ProfileChanged?.Invoke();
    }

    /// <summary>Logo pour PDF et console : fichier branding, sinon workspace <c>assets/LOGO.jpg</c>, sinon Documents.</summary>
    public static string? ResolveLogoPath()
    {
        try
        {
            var p = Load();
            if (!string.IsNullOrWhiteSpace(p.LogoRelativeFileName))
            {
                var branded = Path.Combine(BrandingDirectory, p.LogoRelativeFileName.Trim());
                if (File.Exists(branded))
                    return branded;
            }

            var root = WorkspacePaths.TryFindWorkspaceRoot();
            if (!string.IsNullOrEmpty(root))
            {
                var w = Path.Combine(root, "assets", "LOGO.jpg");
                if (File.Exists(w)) return w;
            }

            var fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "PARAFACTO_Native", "assets", "LOGO.jpg");
            return File.Exists(fallback) ? fallback : null;
        }
        catch
        {
            return null;
        }
    }

    public static void ClearLogoFile()
    {
        lock (Gate)
        {
            var p = Load();
            if (string.IsNullOrWhiteSpace(p.LogoRelativeFileName))
            {
                ProfileChanged?.Invoke();
                return;
            }

            try
            {
                var full = Path.Combine(BrandingDirectory, p.LogoRelativeFileName.Trim());
                if (File.Exists(full))
                    File.Delete(full);
            }
            catch { /* ignore */ }

            p.LogoRelativeFileName = null;
            try
            {
                Directory.CreateDirectory(AppPaths.AppDataRoot);
                File.WriteAllText(JsonPath, JsonSerializer.Serialize(p, JsonOptions));
            }
            catch { /* ignore */ }
        }

        ProfileChanged?.Invoke();
    }
}
