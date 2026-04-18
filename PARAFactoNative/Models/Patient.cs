namespace PARAFactoNative.Models;

public sealed class Patient
{
    // --- PK (DB = patients.id)
    public long Id { get; set; }

    // Compat (certains bouts de code utilisent PatientId)
    public long PatientId
    {
        get => Id;
        set => Id = value;
    }

    public string Code3 { get; set; } = "";
    public string? Niss { get; set; }

    // --- Noms (UI legacy)
    public string LastName { get; set; } = "";
    public string? FirstName { get; set; }

    // --- Alias FR
    public string Nom
    {
        get => LastName;
        set => LastName = value ?? "";
    }

    public string? Prenom
    {
        get => FirstName;
        set => FirstName = value;
    }

    // --- Adresse (structurée + champ concat)
    public string? Rue { get; set; }
    public string? Numero { get; set; }

    public string? Address1 { get; set; }   // ex: "Rue xxx 12"
    public string? Address2 { get; set; }
    public string? Zip { get; set; }
    public string? City { get; set; }
    public string? Country { get; set; }

    public string? Phone { get; set; }
    public string? Email { get; set; }

    // --- Tarif RDV par défaut (libellé tarif actif ; « NON BIM » reste défaut jusqu’à choix en fiche médicale)
    public string Statut { get; set; } = "NON BIM";

    // --- Mutuelle
    public string? MutualName { get; set; }
    public string? MutualCode { get; set; }

    // --- Autres infos (RéférentielClients)
    public string? Referend { get; set; }

    public string? PrescriberFirstName { get; set; }
    public string? PrescriberLastName { get; set; }
    public string? PrescriberCode { get; set; }

    public string? DatePrescription { get; set; }
    public string? DateAccord { get; set; }
    public string? PeriodeAccord { get; set; }
    public string? Nomenclature { get; set; }

    /// <summary>Commentaire propre au patient (distinct du commentaire de séance).</summary>
    public string? Commentaire { get; set; }

    // --- Aliases FR pour compat
    public string? Mutuelle
    {
        get => MutualName;
        set => MutualName = value;
    }

    public string? Adresse
    {
        get => Address1;
        set => Address1 = value;
    }

    public string? Cp
    {
        get => Zip;
        set => Zip = value;
    }

    public string? Ville
    {
        get => City;
        set => City = value;
    }

    public string Display => $"{Code3} — {LastName} {FirstName}".Trim();
}
