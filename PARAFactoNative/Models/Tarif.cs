namespace PARAFactoNative.Models;

public sealed class Tarif
{
    // PK DB = tarifs.id
    public long Id { get; set; }

    // Compat (certains bouts de code utilisent TarifId)
    public long TarifId
    {
        get => Id;
        set => Id = value;
    }

    public string Label { get; set; } = ""; // libelle

    public int PartPatientCents { get; set; }
    public int PartMutuelleCents { get; set; }

    public bool Active { get; set; } = true; // is_active

    // Helpers for UI bindings
    public decimal PartPatient => PartPatientCents / 100m;
    public decimal PartMutuelle => PartMutuelleCents / 100m;
    public string PartPatientEuro => PartPatient.ToString("0.00");
    public string PartMutuelleEuro => PartMutuelle.ToString("0.00");

    public string Display => $"{Label}  (P:{PartPatientCents / 100.0:0.00}€ / M:{PartMutuelleCents / 100.0:0.00}€)";
}
