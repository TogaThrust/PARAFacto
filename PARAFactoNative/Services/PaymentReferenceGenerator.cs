using System.Globalization;
using System.Text.RegularExpressions;

namespace PARAFactoNative.Services;

public static class PaymentReferenceGenerator
{
    /// <summary>
    /// Communication belge structurée +++ddd/dddd/ddddd+++.
    /// Le socle reste déterministe pour une facture donnée et garde un lien avec son numéro.
    /// </summary>
    public static string GenerateStructuredCommunication(string? invoiceNo, long invoiceId)
    {
        var digits = Regex.Replace(invoiceNo ?? "", @"\D", "");
        var seed = invoiceId > 0
            ? invoiceId.ToString(CultureInfo.InvariantCulture)
            : "0";
        var baseDigits = (digits + seed).PadLeft(10, '0');
        if (baseDigits.Length > 10)
            baseDigits = baseDigits[^10..];

        var baseNumber = long.Parse(baseDigits, CultureInfo.InvariantCulture);
        var checksum = (int)(baseNumber % 97);
        if (checksum == 0)
            checksum = 97;

        var full = baseDigits + checksum.ToString("00", CultureInfo.InvariantCulture);
        return $"+++{full[..3]}/{full.Substring(3, 4)}/{full.Substring(7, 5)}+++";
    }

    public static string BuildPdfCommunication(string? invoiceNo, long invoiceId)
    {
        var structured = GenerateStructuredCommunication(invoiceNo, invoiceId);
        var no = (invoiceNo ?? "").Trim();
        return no.Length == 0 ? structured : $"{structured} - Facture {no}";
    }
}
