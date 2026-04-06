using System.Globalization;
using System.Linq;
using System.Text;
using PhoneNumbers;

namespace PARAFactoNative.Services;

/// <summary>
/// Affichage des téléphones : Belgique au format +32 (0)XXX/XX.XX.XX, autres pays via libphonenumber (format international).
/// </summary>
public static class InternationalPhoneFormatter
{
    private static readonly PhoneNumberUtil Util = PhoneNumberUtil.GetInstance();

    public static string FormatForDisplay(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var trimmed = raw.Trim();
        if (TryFormatWithLib(trimmed, out var libFormatted))
            return libFormatted;

        return FormatBelgianHeuristicDigits(trimmed);
    }

    /// <summary>
    /// Convertit un téléphone en format attendu par WhatsApp (chiffres uniquement avec indicatif, sans '+').
    /// Exemples: +32475123456 => 32475123456 ; 0475/12.34.56 => 32475123456.
    /// </summary>
    public static string? TryFormatForWhatsApp(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();

        if (TryParsePhoneNumber(trimmed, out var number) && (Util.IsValidNumber(number) || Util.IsPossibleNumber(number)))
        {
            return $"{number.CountryCode}{number.NationalNumber}";
        }

        var digits = new string(trimmed.Where(char.IsDigit).ToArray());
        if (digits.Length == 0) return null;

        // 00CC... => CC...
        if (digits.StartsWith("00", StringComparison.Ordinal) && digits.Length > 4)
            digits = digits[2..];

        // Belgique locale 0XXXXXXXXX => 32XXXXXXXXX
        if (digits.Length == 10 && digits[0] == '0')
            return "32" + digits[1..];

        // Déjà indicatif pays (ex: 32...)
        if (digits.Length >= 8)
            return digits;

        return null;
    }

    private static bool TryFormatWithLib(string input, out string formatted)
    {
        formatted = "";
        if (!TryParsePhoneNumber(input, out var number))
            return false;

        if (!Util.IsValidNumber(number) && !Util.IsPossibleNumber(number))
            return false;

        if (number.CountryCode == 32)
        {
            var be = TryFormatBelgianCustom(number);
            if (be is not null)
            {
                formatted = be;
                return true;
            }
        }

        formatted = Util.Format(number, PhoneNumberFormat.INTERNATIONAL);
        return true;
    }

    private static bool TryParsePhoneNumber(string input, out PhoneNumber number)
    {
        number = null!;
        try
        {
            number = Util.Parse(input, "BE");
            return true;
        }
        catch (NumberParseException)
        {
            // suite
        }

        var digits = new string(input.Where(char.IsDigit).ToArray());
        if (digits.Length == 0)
            return false;

        // E.164 sans « + » saisi
        if (digits.Length >= 8)
        {
            try
            {
                number = Util.Parse("+" + digits, "ZZ");
                return true;
            }
            catch (NumberParseException)
            {
                /* ignore */
            }
        }

        return false;
    }

    /// <summary>Style demandé pour la Belgique : +32 (0)475/96.44.56 (NSN mobile 9 chiffres).</summary>
    private static string? TryFormatBelgianCustom(PhoneNumber n)
    {
        var ns = n.NationalNumber.ToString(CultureInfo.InvariantCulture);
        if (ns.Length != 9)
            return null;
        return FormatBelgianTenDigits("0" + ns);
    }

    private static string FormatBelgianTenDigits(string tenDigitsStartingWith0)
    {
        var body = tenDigitsStartingWith0[1..];
        var a = body[..3];
        var b = body.Substring(3, 2);
        var c = body.Substring(5, 2);
        var d = body.Substring(7, 2);
        return $"+32 (0){a}/{b}.{c}.{d}";
    }

    /// <summary>Heuristique si libphonenumber n’a pas pu parser (ex. saisie très incomplète).</summary>
    private static string FormatBelgianHeuristicDigits(string raw)
    {
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        if (digits.Length == 0) return raw.Trim();

        if (digits.StartsWith("32", StringComparison.Ordinal) && digits.Length >= 10)
            digits = digits[2..];

        if (digits.Length == 9 && digits[0] != '0')
            digits = "0" + digits;

        if (digits.Length == 10 && digits[0] == '0')
        {
            var body = digits[1..];
            if (body.Length == 9)
                return FormatBelgianTenDigits(digits);
        }

        return FormatDigitsGrouped(digits);
    }

    private static string FormatDigitsGrouped(string digits)
    {
        if (digits.Length <= 3) return digits;
        var sb = new StringBuilder();
        for (var i = 0; i < digits.Length; i += 3)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(digits.AsSpan(i, Math.Min(3, digits.Length - i)));
        }

        return sb.ToString();
    }
}
