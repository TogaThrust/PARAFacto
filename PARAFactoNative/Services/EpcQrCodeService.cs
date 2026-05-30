using System.Globalization;
using System.Text;
using QRCoder;

namespace PARAFactoNative.Services;

public static class EpcQrCodeService
{
    public static byte[] GeneratePaymentQrPng(
        string beneficiaryName,
        string iban,
        string bic,
        int amountCents,
        string communication)
    {
        var payload = BuildPayload(beneficiaryName, iban, bic, amountCents, communication);
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        var qr = new PngByteQRCode(data);
        return qr.GetGraphic(8);
    }

    private static string BuildPayload(string beneficiaryName, string iban, string bic, int amountCents, string communication)
    {
        var name = TrimForEpc(Clean(beneficiaryName), 70);
        var ibanClean = CleanIban(iban);
        var bicClean = Clean(bic).Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        var amount = "EUR" + (amountCents / 100m).ToString("0.00", CultureInfo.InvariantCulture);

        // EPC QR Code / SEPA Credit Transfer.
        // Belgian +++ structured communications are accepted by banking apps as unstructured remittance text.
        return string.Join("\n", new[]
        {
            "BCD",
            "002",
            "1",
            "SCT",
            bicClean,
            name,
            ibanClean,
            amount,
            "",
            "",
            TrimForEpc(Clean(communication), 140),
            ""
        });
    }

    private static string CleanIban(string iban)
        => Clean(iban).Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();

    private static string Clean(string value)
    {
        value = (value ?? "").Trim();
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch is '\r' or '\n' or '\t')
                sb.Append(' ');
            else
                sb.Append(ch);
        }

        return string.Join(" ", sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string TrimForEpc(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];
}
