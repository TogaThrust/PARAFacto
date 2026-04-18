namespace PARAFactoNative.Models;

/// <summary>
/// Identité du praticien affichée sur les PDF (factures, états mutuelle, notes de crédit) et dans la console.
/// </summary>
public sealed class ProfessionalProfile
{
    /// <summary>Titre à côté du logo sur l’onglet Console (souvent en majuscules).</summary>
    public string ConsoleHeaderTitle { get; set; } = "Charles Edouard - LOGOPEDE";

    /// <summary>Nom / titre sur les factures patients et notes de crédit (ligne grasse sous l’en-tête).</summary>
    public string InvoiceProviderName { get; set; } = "Charles Edouard - Logopède";

    public string AddressLine1 { get; set; } = "1, Rue de la chaussée Pavée";
    public string AddressLine2 { get; set; } = "1004 Ougrée";

    public string Inami { get; set; } = "7-19456-12-123";
    public string VatNumber { get; set; } = "BE0123.456.789";

    public string Phone { get; set; } = "0471/11.11.11";
    public string Email { get; set; } = "fake@fake.com";

    public string Iban { get; set; } = "BE1234 5678 9875 20";
    public string IbanMutuelle { get; set; } = "BE1234 5678 9875 20";
    public string IbanCreditNote { get; set; } = "BE1234 5678 9875 20";
    public string Bic { get; set; } = "AAAABEBB";

    /// <summary>Partie après « *Nom et prénom du prestataire de soins : » sur l’état mutuelle (ex. DUPONT Jean).</summary>
    public string MutualRecapProviderName { get; set; } = "EDOUARD Charles";

    /// <summary>Ligne d’adresse unique pour l’état mutuelle ; si vide, <see cref="AddressLine1"/> + virgule + <see cref="AddressLine2"/>.</summary>
    public string? MutualRecapAddressLine { get; set; }

    /// <summary>Nom affiché pour rappels factures (WhatsApp / mail).</summary>
    public string ReminderSenderDisplayName { get; set; } = "Charles Edouard - LOGOPEDE";

    /// <summary>Fichier logo sous le dossier branding (ex. logo.png). Vide = pas de logo applicatif.</summary>
    public string? LogoRelativeFileName { get; set; }

    public static ProfessionalProfile CreateDefault() => new();
}
