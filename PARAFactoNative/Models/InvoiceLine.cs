namespace PARAFactoNative.Models;

public sealed class InvoiceLine
{
    public long Id { get; set; }
    public long InvoiceId { get; set; }
    public string? Label { get; set; }
    public int Qty { get; set; }
    public int UnitPriceCents { get; set; }
    public int TotalCents { get; set; }
    public int PatientPartCents { get; set; }
    public int MutuellePartCents { get; set; }
    public string? DateIso { get; set; }
    public string? CreatedAt { get; set; }
}
