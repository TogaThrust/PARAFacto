using System.IO;
using System.Text.Json;

namespace PARAFactoNative.Services;

public static class SubscriptionConfigLoader
{
    private const string ConfigFileName = "subscription_config.json";
    private const string AccountFileName = "subscription_account.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string AccountPath => Path.Combine(AppPaths.AppDataRoot, AccountFileName);

    public static SubscriptionConfigFile LoadMergedConfig()
    {
        var merged = new SubscriptionConfigFile();

        var baseDir = AppContext.BaseDirectory;
        var exeConfig = Path.Combine(baseDir, ConfigFileName);
        if (File.Exists(exeConfig))
        {
            try
            {
                var json = File.ReadAllText(exeConfig);
                var fromExe = JsonSerializer.Deserialize<SubscriptionConfigFile>(json, JsonOptions);
                if (fromExe != null)
                    Copy(fromExe, merged);
            }
            catch
            {
                // ignore invalid exe config
            }
        }

        var appDataConfig = Path.Combine(AppPaths.AppDataRoot, ConfigFileName);
        if (File.Exists(appDataConfig))
        {
            try
            {
                var json = File.ReadAllText(appDataConfig);
                var fromData = JsonSerializer.Deserialize<SubscriptionConfigFile>(json, JsonOptions);
                if (fromData != null)
                    Copy(fromData, merged);
            }
            catch
            {
                // ignore
            }
        }

        return merged;
    }

    private static void Copy(SubscriptionConfigFile src, SubscriptionConfigFile dst)
    {
        if (!string.IsNullOrWhiteSpace(src.LicenseCheckApiUrl))
            dst.LicenseCheckApiUrl = src.LicenseCheckApiUrl.Trim();
        if (!string.IsNullOrWhiteSpace(src.PaymentPageUrl))
            dst.PaymentPageUrl = src.PaymentPageUrl.Trim();
        dst.SkipValidation |= src.SkipValidation;
        if (src.RenewalWarningDays > 0)
            dst.RenewalWarningDays = src.RenewalWarningDays;
    }

    public static SubscriptionAccountFile LoadAccount()
    {
        try
        {
            if (!File.Exists(AccountPath))
                return new SubscriptionAccountFile();
            var json = File.ReadAllText(AccountPath);
            return JsonSerializer.Deserialize<SubscriptionAccountFile>(json, JsonOptions) ?? new SubscriptionAccountFile();
        }
        catch
        {
            return new SubscriptionAccountFile();
        }
    }

    public static void SaveAccount(SubscriptionAccountFile account)
    {
        Directory.CreateDirectory(AppPaths.AppDataRoot);
        var json = JsonSerializer.Serialize(account, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(AccountPath, json);
    }
}
