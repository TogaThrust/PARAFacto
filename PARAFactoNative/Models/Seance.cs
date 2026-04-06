using System;

namespace PARAFactoNative.Models;

public sealed class Seance
{
    public long SeanceId { get; set; }
    public long PatientId { get; set; }
    public long TarifId { get; set; }

    // Date stockée au format ISO en DB (ou convertie)
    public DateTime Date { get; set; }

    public bool IsCash { get; set; }
    public string? Commentaire { get; set; }

    public int PartPatientCents { get; set; }
    public int PartMutuelleCents { get; set; }
}