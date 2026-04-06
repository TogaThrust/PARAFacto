namespace PARAFactoNative.Models;

public sealed class Invoice
{
    public long Id { get; set; }
    public string InvoiceNo { get; set; } = "";
    public string Kind { get; set; } = ""; // patient | mutuelle | credit_note

    public long? PatientId { get; set; }
    public string? Mutuelle { get; set; }

    public string DateIso { get; set; } = ""; // YYYY-MM-DD

    public int TotalCents { get; set; }
    public int PaidCents { get; set; }
    public string Status { get; set; } = "unpaid";

    public long? RefInvoiceId { get; set; }
    public string? Reason { get; set; }
    public string? RefDoc { get; set; }
    public string? UserComment { get; set; }

    // Optional: YYYY-MM (used for monthly invoices)
    public string? Period { get; set; }

    /// <summary>Date du dernier paiement (YYYY-MM-DD), renseignée par la requête Search.</summary>
    public string? LastPaymentDateIso { get; set; }

    // UI helpers
    public int BalanceCents => TotalCents - PaidCents;
    public decimal TotalEuro => TotalCents / 100m;
    public decimal BalanceEuro => BalanceCents / 100m;

    public string Recipient { get; set; } = ""; // filled by query
}
