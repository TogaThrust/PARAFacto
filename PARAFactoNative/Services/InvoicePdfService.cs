using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PARAFactoNative.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace PARAFactoNative.Services;

public sealed class InvoicePdfService
{
    private static readonly Regex CreditNotePublicNumberFormat = new(@"^\d{2}-\d{4}-\d+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public string EnsureFolder(string folder)
    {
        if (!Directory.Exists(folder))
            Directory.CreateDirectory(folder);
        return folder;
    }

    public string BuildPatientInvoicePdfPath(Invoice inv)
{
    // Factures acquittées (payées en entier / cash cabinet) → sous-dossier "FACTURES ACQUITTEES" du mois
    // Sinon → dossier du mois "FACTURES MENSUELLES PATIENTS\MM-YYYY"
    var period = !string.IsNullOrWhiteSpace(inv.Period) ? inv.Period : (inv.DateIso ?? "").Length >= 7 ? inv.DateIso![..7] : "";
    var isAcquittee = (string.Equals(inv.Status, "paid", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(inv.Status, "acquittee", StringComparison.OrdinalIgnoreCase))
                      && inv.PaidCents >= inv.TotalCents;
    var folder = isAcquittee && !string.IsNullOrWhiteSpace(period)
        ? WorkspacePaths.PatientMonthAcquittedSubfolder(period)
        : WorkspacePaths.PatientMonthFolder(period);

    var recipient = string.IsNullOrWhiteSpace(inv.Recipient) ? "Patient" : inv.Recipient;
    var safeRecipient = MakeSafeFileName(recipient);

    // Example: Facture_02-2025-02_Abrassart Mason.pdf
    var file = $"Facture_{inv.InvoiceNo}_{safeRecipient}.pdf";
    return Path.Combine(folder, file);
}


    public string BuildMutualRecapPdfPath(string mutualName, string period)
{
    // Folder: Documents\PARAFACTO_Native\FACTURES MENSUELLES MUTUELLES\MM-YYYY\<MUTUELLE>
    var folder = WorkspacePaths.MutualMonthFolder(period, mutualName);
    var mmYYYY = WorkspacePaths.ToMMYYYY(period);

    var safeMut = MakeSafeFileName((mutualName ?? "").Trim().ToUpperInvariant());
    var file = $"EtatRecap_{safeMut}_{mmYYYY}.pdf";
    return Path.Combine(folder, file);
}


    /// <summary>
    /// Même dossier que la facture mutuelle originale. Fichier court distinct de l’état normal : <c>EtatRecap_MUT_04-2026_MOD.pdf</c>.
    /// </summary>
    public string BuildMutualRecapModifPdfPath(string mutualName, string period)
    {
        var folder = WorkspacePaths.MutualMonthFolder(period, mutualName);
        var mmYYYY = WorkspacePaths.ToMMYYYY(period);
        var safe = MakeSafeFileName((mutualName ?? "").Trim().ToUpperInvariant());
        return Path.Combine(folder, $"EtatRecap_{safe}_{mmYYYY}_MOD.pdf");
    }

    private static readonly Regex EtatMutualInvoicePeriodRegex = new(
        @"(?i)ETAT[^\d]*(\d{2})[^\d]*(\d{4})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string NormalizeDashesForInvoiceParse(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s
            .Replace('\u2011', '-')
            .Replace('\u2012', '-')
            .Replace('\u2013', '-')
            .Replace('\u2014', '-')
            .Replace('\u2010', '-')
            .Replace('\u2212', '-');
    }

    /// <summary>
    /// Chemins absolus des PDF d’état <c>-MOD</c> existants à supprimer (construits + recherche dans le dossier mutuelle du mois).
    /// </summary>
    public IEnumerable<string> GetMutualModificationPdfPathsForDeletion(
        string? mutuelle, string? recipient, string? period, string? dateIso, string? invoiceNo)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void addIfExists(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                var full = Path.GetFullPath(path.Trim());
                if (File.Exists(full))
                    result.Add(full);
            }
            catch
            {
                /* chemin invalide */
            }
        }

        var mutualKeys = new List<string>();
        foreach (var s in new[] { mutuelle, recipient })
        {
            var t = (s ?? "").Trim();
            if (t.Length == 0) continue;
            if (mutualKeys.Exists(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase))) continue;
            mutualKeys.Add(t);
        }

        if (mutualKeys.Count == 0)
            return result;

        var periodHints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var p0 = (period ?? "").Trim();
        if (p0.Length > 0) periodHints.Add(p0);
        var di = (dateIso ?? "").Trim();
        if (di.Length >= 7) periodHints.Add(di[..7]);
        var noNorm = NormalizeDashesForInvoiceParse(invoiceNo);
        var m = EtatMutualInvoicePeriodRegex.Match(noNorm);
        if (m.Success)
        {
            var mm = m.Groups[1].Value;
            var yyyy = m.Groups[2].Value;
            periodHints.Add($"{yyyy}-{mm}");
            periodHints.Add($"{mm}-{yyyy}");
        }

        if (periodHints.Count == 0)
            return result;

        var no = (invoiceNo ?? "").Trim();
        var noUpperSafe = no.Length > 0 ? MakeSafeFileName(no.ToUpperInvariant()) : "";

        foreach (var mk in mutualKeys)
        {
            foreach (var ph in periodHints)
            {
                addIfExists(BuildMutualRecapModifPdfPath(mk, ph));

                var mmYYYY = WorkspacePaths.ToMMYYYY(ph);
                var safe = MakeSafeFileName(mk.Trim().ToUpperInvariant());
                var folder = WorkspacePaths.MutualMonthFolder(ph, mk);
                addIfExists(Path.Combine(folder, $"EtatRecap_{safe}_{mmYYYY}-MOD.pdf"));
                if (no.Length > 0 && noUpperSafe.Length > 0 &&
                    !string.Equals(noUpperSafe, "Document", StringComparison.OrdinalIgnoreCase))
                    addIfExists(Path.Combine(folder, $"EtatRecap_{safe}_{mmYYYY}_MOD_{noUpperSafe}.pdf"));

                try
                {
                    if (!Directory.Exists(folder)) continue;
                    var prefix = $"EtatRecap_{safe}_{mmYYYY}_";
                    foreach (var file in Directory.EnumerateFiles(folder, "*.pdf", SearchOption.TopDirectoryOnly))
                    {
                        var fn = Path.GetFileName(file);
                        if (!fn.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                        if (fn.IndexOf("MOD", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        addIfExists(file);
                    }
                }
                catch
                {
                    /* dossier inaccessible */
                }
            }
        }

        return result;
    }

    /// <summary>Chemin du PDF de note de crédit : dossier du mois (selon la facture de référence) + sous-dossier NC.</summary>
    /// <remarks>Fichier du type <c>NC_04-2026-01_Destinataire.pdf</c> : le n° de pièce est celui du document (sans redoubler <c>NC_</c> si le n° stocké commence par <c>NC-</c>).</remarks>
    public string BuildCreditNotePdfPath(string creditNo, string recipient, string periodYYYYMM, string refInvoiceKind)
    {
        var safeRecipient = MakeSafeFileName(recipient);
        var docPart = NormalizeCreditNoteInvoiceNoForPdfFileName(creditNo);
        var folder = string.Equals(refInvoiceKind, "mutuelle", StringComparison.OrdinalIgnoreCase)
            ? WorkspacePaths.MutualMonthFolderNc(periodYYYYMM)
            : WorkspacePaths.PatientMonthFolderNc(periodYYYYMM);
        return Path.Combine(folder, $"NC_{docPart}_{safeRecipient}.pdf");
    }

    /// <summary>Affichage PDF / pied de page : <c>NC-04-2026-01</c> → <c>04-2026-01</c> (même style que les factures).</summary>
    public static string FormatCreditNoteNoForPdfHeader(string? invoiceNo)
    {
        var s = (invoiceNo ?? "").Trim();
        if (s.Length == 0) return "";
        if (s.StartsWith("NC-", StringComparison.OrdinalIgnoreCase))
        {
            var rest = s.Substring(3).Trim();
            if (CreditNotePublicNumberFormat.IsMatch(rest))
                return rest;
        }
        return s;
    }

    /// <summary>Partie fichier après le préfixe <c>NC_</c> : n° document <c>MM-YYYY-NN</c>, sans préfixe <c>NC-</c> résiduel.</summary>
    private static string NormalizeCreditNoteInvoiceNoForPdfFileName(string? invoiceNo)
    {
        var raw = (invoiceNo ?? "").Trim();
        if (raw.Length == 0)
            return "SansNumero";

        var doc = raw;
        if (doc.StartsWith("NC-", StringComparison.OrdinalIgnoreCase))
        {
            doc = doc.Substring(3).Trim();
            while (doc.StartsWith("-", StringComparison.Ordinal))
                doc = doc[1..].Trim();
        }

        if (doc.Length == 0)
            doc = MakeSafeFileName(raw);
        else
            doc = MakeSafeFileName(doc);

        return doc.Length > 0 ? doc : "SansNumero";
    }

    /// <param name="recipientAddressLines">Adresse du destinataire (rue, CP ville, pays) pour l'encadré FACTURÉ À, ou null.</param>
    /// <param name="patientFirstNames">Prénom(s) du/des patient(s) concernés (pour la communication en pied de page).</param>
    /// <param name="isAcquittedCash">True si la facture a été payée en espèces au cabinet.</param>
    /// <param name="paidDate">Date de paiement en cash (si connue).</param>
    /// <param name="totalPatientCents">Total à afficher (part patient uniquement). Si null, utilise inv.TotalCents (legacy).</param>
    public void GeneratePatientInvoicePdf(
        Invoice inv,
        List<InvoiceLineRow> lines,
        string outputPath,
        string? recipientAddressLines = null,
        string? patientFirstNames = null,
        bool isAcquittedCash = false,
        DateTime? paidDate = null,
        int? totalPatientCents = null)
    {
        QuestPDF.Settings.License = LicenseType.Community;

        var ci = CultureInfo.GetCultureInfo("fr-BE");
        var issueDate = ParseIso(inv.DateIso);
        var dueDate = issueDate.AddDays(6);
        var monthLabel = MonthName(inv.Period);
        var effectivePaidDate = paidDate ?? issueDate;
        var pr = ProfessionalProfileStore.Load();
        var logoPath = ProfessionalProfileStore.ResolveLogoPath();

        EnsurePdfWritableOrThrow(outputPath);

        Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(30);
                page.DefaultTextStyle(x => x.FontSize(10));

                // En-tête en un seul bloc : FACTURE + logo, puis prestataire + N° Date Échéance Séances
                page.Header().Column(headerCol =>
                {
                    headerCol.Item().Row(headerRow =>
                    {
                        headerRow.RelativeItem().AlignLeft().Text("FACTURE").FontSize(18).Bold();
                        if (!string.IsNullOrEmpty(logoPath) && File.Exists(logoPath))
                            headerRow.ConstantItem(140).AlignRight().Height(90).Image(logoPath).FitArea();
                    });
                    headerCol.Item().PaddingTop(8).Row(r =>
                    {
                        r.RelativeItem().Column(left =>
                        {
                            left.Item().Text(pr.InvoiceProviderName).Bold();
                            left.Item().Text(pr.AddressLine1);
                            left.Item().Text(pr.AddressLine2);
                            left.Item().Text($"INAMI {pr.Inami}");
                            left.Item().Text($"Num TVA : {pr.VatNumber}");
                            left.Item().Text($"Téléphone {pr.Phone}");
                            left.Item().Text($"Email : {pr.Email}");
                            left.Item().Text($"IBAN {pr.Iban}");
                        });
                        r.ConstantItem(220).Column(right =>
                        {
                            right.Item().AlignRight().Text($"N° : {inv.InvoiceNo}");
                            right.Item().AlignRight().Text($"Date : {issueDate:dd-MM-yyyy}");
                            right.Item().AlignRight().Text($"Échéance : {dueDate:dd-MM-yyyy}");
                            right.Item().AlignRight().Text($"Séances du mois de {monthLabel}");
                        });
                    });
                });

                // Contenu : FACTURÉ À (encadré), trait, tableau, total
                page.Content().Column(col =>
                {
                    // FACTURÉ À : encadré avec bordure noire
                    col.Item().PaddingTop(8).Border(1).BorderColor(Colors.Black).Padding(8).Column(box =>
                    {
                        box.Item().Text("FACTURÉ À :").Bold();
                        box.Item().PaddingTop(4).AlignRight().Column(addr =>
                        {
                            addr.Item().Text(inv.Recipient ?? "");
                            if (!string.IsNullOrWhiteSpace(recipientAddressLines))
                            {
                                foreach (var line in recipientAddressLines.Split('\n'))
                                {
                                    if (!string.IsNullOrWhiteSpace(line))
                                        addr.Item().Text(line.Trim());
                                }
                            }
                        });
                    });
                    col.Item().PaddingTop(10).LineHorizontal(1).LineColor(Colors.Black);

                    // Tableau
                    col.Item().PaddingTop(10).Table(table =>
                    {
                        table.ColumnsDefinition(columns =>
                        {
                            columns.ConstantColumn(75);
                            columns.RelativeColumn();
                            columns.ConstantColumn(75);
                        });

                        table.Header(header =>
                        {
                            header.Cell().Element(ThPatient).Text("Date");
                            header.Cell().Element(ThPatient).Text("Détail");
                            header.Cell().Element(ThPatient).AlignRight().Text("Montant");
                        });

                        var totalDisplayCents = totalPatientCents ?? inv.TotalCents;
                        foreach (var l in lines.OrderBy(x => x.DateIso))
                        {
                            table.Cell().Element(TdPatient).Text(FormatDate(l.DateIso));
                            table.Cell().Element(TdPatient).Text(l.Label);
                            table.Cell().Element(TdPatient).AlignRight().Text(FormatEuro(l.TotalCents, ci));
                        }

                        table.Cell().ColumnSpan(2).Element(TdTotalLabelPatient).Text("Total patient");
                        table.Cell().Element(TdTotalValuePatient).AlignRight().Text(FormatEuro(totalDisplayCents, ci));
                    });

                    // Si facture acquittée en cash: mention + récapitulatif immédiatement sous la liste
                    if (isAcquittedCash)
                    {
                        var totalCents = totalPatientCents ?? inv.TotalCents;
                        var roundedPaidCents = ApplyBelgianCashRounding(totalCents);
                        var deltaCents = roundedPaidCents - totalCents;

                        var montantAPayer = FormatEuro(totalCents, ci);
                        var arrondiAbs = FormatEuro(Math.Abs(deltaCents), ci);
                        var arrondiPrefix = deltaCents < 0 ? "-" : "+";
                        var montantPaye = FormatEuro(roundedPaidCents, ci);
                        var soldeDue = FormatEuro(0, ci);

                        col.Item().PaddingTop(12)
                            .Text($"Facture acquittée par paiement en espèces au cabinet le {effectivePaidDate:dd/MM/yyyy}.")
                            .FontSize(9);
                        col.Item().Text($"Montant à payer : {montantAPayer}").FontSize(9);
                        col.Item().Text($"Arrondi légal espèces : {arrondiPrefix}{arrondiAbs}").FontSize(9);
                        col.Item().Text($"Montant payé en espèces : {montantPaye}").FontSize(9);
                        col.Item().Text($"Solde dû : {soldeDue}").FontSize(9);
                    }
                });

                // Pied de page : toujours collé en bas de page (mentions de paiement)
                page.Footer().Column(foot =>
                {
                    var patientLabel = string.IsNullOrWhiteSpace(patientFirstNames)
                        ? "patient"
                        : patientFirstNames!;
                    foot.Item().Text($"Paiement à effectuer dans les 7 jours sur le compte {pr.Iban}").FontSize(9);
                    foot.Item().Text($"Communication : Séances de prise en charge {patientLabel} - {monthLabel}").FontSize(9);
                });
            });
        }).GeneratePdf(outputPath);
    }

    /// <summary>Arrondi belge obligatoire pour les paiements en espèces (au multiple de 0,05 € le plus proche).</summary>
    private static int ApplyBelgianCashRounding(int cents)
    {
        var lastDigit = Math.Abs(cents) % 10;
        var baseCents = cents - (cents >= 0 ? lastDigit : -lastDigit);

        return lastDigit switch
        {
            1 or 2 => baseCents,            // → x0
            3 or 4 => baseCents + (cents >= 0 ? 5 : -5), // → x5
            5 => cents,                     // déjà sur 5
            6 or 7 => baseCents + (cents >= 0 ? 5 : -5), // → x5
            8 or 9 => baseCents + (cents >= 0 ? 10 : -10), // → x0 suivant
            _ => cents                      // 0 reste 0
        };
    }

    private static IContainer ThPatient(IContainer c) => c
        .Border(1).BorderColor(Colors.Black)
        .PaddingVertical(4).PaddingHorizontal(6)
        .DefaultTextStyle(x => x.SemiBold());

    private static IContainer TdPatient(IContainer c) => c
        .Border(1).BorderColor(Colors.Black)
        .PaddingVertical(3).PaddingHorizontal(6);

    private static IContainer TdTotalLabelPatient(IContainer c) => c
        .PaddingTop(6).BorderTop(1).BorderColor(Colors.Black)
        .PaddingVertical(4).PaddingHorizontal(6)
        .DefaultTextStyle(x => x.SemiBold());

    private static IContainer TdTotalValuePatient(IContainer c) => c
        .PaddingTop(6).BorderTop(1).BorderColor(Colors.Black)
        .PaddingVertical(4).PaddingHorizontal(6)
        .DefaultTextStyle(x => x.SemiBold());

    /// <summary>Même gabarit que <see cref="GeneratePatientInvoicePdf"/> : libellés « note de crédit » / « crédité à ».</summary>
    public void GenerateCreditNotePdf(
        Invoice credit,
        Invoice refInvoice,
        List<InvoiceLineRow> lines,
        string outputPath,
        string? recipientAddressLines = null,
        string? patientFirstNames = null)
    {
        QuestPDF.Settings.License = LicenseType.Community;

        var ci = CultureInfo.GetCultureInfo("fr-BE");
        var issueDate = ParseIso(credit.DateIso);
        var dueDate = issueDate.AddDays(6);
        var monthLabel = MonthName(refInvoice.Period);
        var pr = ProfessionalProfileStore.Load();
        var logoPath = ProfessionalProfileStore.ResolveLogoPath();
        var ibanNc = string.IsNullOrWhiteSpace(pr.IbanCreditNote) ? pr.Iban : pr.IbanCreditNote;
        var refNo = (refInvoice.InvoiceNo ?? "").Trim();
        var creditNoDisplay = FormatCreditNoteNoForPdfHeader(credit.InvoiceNo);

        EnsurePdfWritableOrThrow(outputPath);

        Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(30);
                page.DefaultTextStyle(x => x.FontSize(10));

                page.Header().Column(headerCol =>
                {
                    headerCol.Item().Row(headerRow =>
                    {
                        headerRow.RelativeItem().AlignLeft().Text("NOTE DE CRÉDIT").FontSize(18).Bold();
                        if (!string.IsNullOrEmpty(logoPath) && File.Exists(logoPath))
                            headerRow.ConstantItem(140).AlignRight().Height(90).Image(logoPath).FitArea();
                    });
                    headerCol.Item().PaddingTop(8).Row(r =>
                    {
                        r.RelativeItem().Column(left =>
                        {
                            left.Item().Text(pr.InvoiceProviderName).Bold();
                            left.Item().Text(pr.AddressLine1);
                            left.Item().Text(pr.AddressLine2);
                            left.Item().Text($"INAMI {pr.Inami}");
                            left.Item().Text($"Num TVA : {pr.VatNumber}");
                            left.Item().Text($"Téléphone {pr.Phone}");
                            left.Item().Text($"Email : {pr.Email}");
                            left.Item().Text($"IBAN {ibanNc}");
                        });
                        r.ConstantItem(220).Column(right =>
                        {
                            right.Item().AlignRight().Text($"N° : {creditNoDisplay}");
                            right.Item().AlignRight().Text($"Date : {issueDate:dd-MM-yyyy}");
                            right.Item().AlignRight().Text($"Échéance : {dueDate:dd-MM-yyyy}");
                            right.Item().AlignRight().Text($"Séances du mois de {monthLabel}");
                            if (refNo.Length > 0)
                                right.Item().AlignRight().Text($"Pièce de référence n° : {refNo}");
                        });
                    });
                });

                page.Content().Column(col =>
                {
                    col.Item().PaddingTop(8).Border(1).BorderColor(Colors.Black).Padding(8).Column(box =>
                    {
                        box.Item().Text("CRÉDITÉ À :").Bold();
                        box.Item().PaddingTop(4).AlignRight().Column(addr =>
                        {
                            addr.Item().Text(credit.Recipient ?? "");
                            if (!string.IsNullOrWhiteSpace(recipientAddressLines))
                            {
                                foreach (var line in recipientAddressLines.Split('\n'))
                                {
                                    if (!string.IsNullOrWhiteSpace(line))
                                        addr.Item().Text(line.Trim());
                                }
                            }
                        });
                    });
                    col.Item().PaddingTop(10).LineHorizontal(1).LineColor(Colors.Black);

                    col.Item().PaddingTop(10).Table(table =>
                    {
                        table.ColumnsDefinition(columns =>
                        {
                            columns.ConstantColumn(75);
                            columns.RelativeColumn();
                            columns.ConstantColumn(75);
                        });

                        table.Header(header =>
                        {
                            header.Cell().Element(ThPatient).Text("Date");
                            header.Cell().Element(ThPatient).Text("Détail");
                            header.Cell().Element(ThPatient).AlignRight().Text("Montant");
                        });

                        var totalNcCents = Math.Abs(credit.TotalCents);
                        foreach (var l in lines.OrderBy(x => x.DateIso))
                        {
                            table.Cell().Element(TdPatient).Text(FormatDate(l.DateIso));
                            table.Cell().Element(TdPatient).Text(l.Label);
                            table.Cell().Element(TdPatient).AlignRight().Text(FormatEuro(Math.Abs(l.TotalCents), ci));
                        }

                        table.Cell().ColumnSpan(2).Element(TdTotalLabelPatient).Text("Total note de crédit");
                        table.Cell().Element(TdTotalValuePatient).AlignRight().Text(FormatEuro(totalNcCents, ci));
                    });
                });

                page.Footer().Column(foot =>
                {
                    var nameHint = string.IsNullOrWhiteSpace(patientFirstNames) ? "patient" : patientFirstNames!.Trim();
                    foot.Item().Text($"Paiement à effectuer dans les 7 jours sur le compte {ibanNc}").FontSize(9);
                    foot.Item().Text(
                            refNo.Length > 0
                                ? $"Communication : Note de crédit n° {creditNoDisplay} — pièce n° {refNo} — séances {nameHint} - {monthLabel}"
                                : $"Communication : Note de crédit n° {creditNoDisplay} — séances {nameHint} - {monthLabel}")
                        .FontSize(9);
                });
            });
        }).GeneratePdf(outputPath);
    }

    /// <param name="modifiedTotalAoCents">
    /// Nouveau total AO (en centimes) après modification mutuelle.
    /// Null pour un état normal, non null pour un état modifié.
    /// </param>
    /// <param name="initialTotalAoCentsForModification">
    /// Pour un état modifié : total AO de la facture mutuelle d’origine (en centimes), affiché sur la ligne
    /// « Total = somme totale… » lorsque le tableau n’a plus de lignes ou pour refléter le montant facturé initialement.
    /// Null = conserver l’ancien comportement (somme des lignes du tableau uniquement).
    /// </param>
    public void GenerateMutualRecapPdf(
        string mutualName,
        string period,
        List<MutualRow> rows,
        MutualRecapMeta meta,
        string outputPath,
        bool isModification,
        int? modifiedTotalAoCents = null,
        int? initialTotalAoCentsForModification = null)
    {
        QuestPDF.Settings.License = LicenseType.Community;

        var ci = CultureInfo.GetCultureInfo("fr-BE");
        var pr = ProfessionalProfileStore.Load();
        var mutualAddr = string.IsNullOrWhiteSpace(pr.MutualRecapAddressLine)
            ? $"{pr.AddressLine1}, {pr.AddressLine2}".Trim(' ', ',')
            : pr.MutualRecapAddressLine!.Trim();

        EnsurePdfWritableOrThrow(outputPath);

        Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(25);
                page.DefaultTextStyle(x => x.FontSize(10));

                page.Header().Column(col =>
                {
                    col.Item().AlignCenter().Text("Régime du tiers payant").FontSize(12);
                    col.Item().AlignCenter().Text("État récapitulatif des attestations de soins").FontSize(14).Bold();
                    col.Item().PaddingTop(10).Text("Expéditeur").Bold();

                    // Bloc expéditeur + encart mutuelle coloré à droite
                    col.Item().Row(r =>
                    {
                        r.RelativeItem().Border(1).BorderColor(Colors.Grey.Lighten2).Padding(6).Column(left =>
                        {
                            left.Item().Text($"*Nom et prénom du prestataire de soins : {pr.MutualRecapProviderName}").Bold();
                            left.Item().Text($"Adresse : {mutualAddr}");
                            left.Item().Text($"*N° BCE et / ou N° inami : {pr.Inami}");
                            left.Item().Text($"Ma référence pour cet état récapitulatif : {meta.Reference}");
                            left.Item().Text($"Contact (téléphone et/ou e-mail) : {pr.Phone}" + (string.IsNullOrWhiteSpace(pr.Email) ? "" : $" / {pr.Email}"));
                        });

                        r.ConstantItem(140).Height(50).Border(1).BorderColor(Colors.Grey.Lighten2)
                            .Background(Colors.Yellow.Lighten3)
                            .AlignCenter()
                            .AlignMiddle()
                            .Text(mutualName.ToUpperInvariant()).SemiBold();
                    });

                    col.Item().PaddingTop(10).Text("*Inventaire des attestations jointes").Bold();
                });

                page.Content().PaddingTop(5).Column(col =>
                {
                    col.Item().Table(table =>
                    {
                        table.ColumnsDefinition(columns =>
                        {
                            columns.RelativeColumn(); // Nom
                            columns.ConstantColumn(110); // NISS
                            columns.ConstantColumn(80); // BIM
                            columns.ConstantColumn(120); // AO
                            columns.ConstantColumn(120); // ticket
                        });

                        table.Header(h =>
                        {
                            h.Cell().Element(Th).Text("Nom et prénom du patient");
                            h.Cell().Element(Th).Text("Numéro NISS");
                            h.Cell().Element(Th).Text("Bénéficiaire de\nl'intervention majorée");
                            h.Cell().Element(Th).AlignRight().Text("Montant de l'intervention\nAO");
                            h.Cell().Element(Th).AlignRight().Text("Ticket modérateur");
                        });

                        foreach (var r in rows)
                        {
                            table.Cell().Element(Td).Text(r.PatientName);
                            table.Cell().Element(Td).Text(r.Niss);
                            table.Cell().Element(Td).Text(r.IsBim == 1 ? "Oui" : "Non");
                            table.Cell().Element(Td).AlignRight().Text(FormatEuro(r.AoCents, ci));
                            table.Cell().Element(Td).AlignRight().Text(FormatEuro(r.TicketCents, ci));
                        }

                        // Ligne de total : pour une modification, le total « initial » doit suivre la facture d’origine
                        // (total_cents), pas seulement la somme des lignes (souvent 0 s’il n’y a plus de séances AO ce mois).
                        var totalAoFromRows = rows.Sum(x => x.AoCents);
                        var totalAo = isModification && initialTotalAoCentsForModification.HasValue
                            ? initialTotalAoCentsForModification.Value
                            : totalAoFromRows;
                        var totalTicket = rows.Sum(x => x.TicketCents);

                        // Ligne de total "classique" (sans surlignage)
                        table.Cell().ColumnSpan(3).Element(TdTotalLabel)
                            .Text("Total = somme totale de l'intervention AO que vous facturez");
                        table.Cell().Element(TdTotalValue)
                            .AlignRight().Text(FormatEuro(totalAo, ci));
                        table.Cell().Element(TdTotalValue).AlignRight().Text(FormatEuro(totalTicket, ci));

                        // Si modification mutuelle : nouvelle ligne de total AO surlignée en jaune
                        if (isModification && modifiedTotalAoCents.HasValue)
                        {
                            var newAo = modifiedTotalAoCents.Value;
                            table.Cell().ColumnSpan(3).Element(c => TdTotalLabel(c).Background(Colors.Yellow.Lighten3))
                                .Text("Nouveau total AO après modification de la mutuelle");
                            table.Cell().Element(c => TdTotalValue(c).Background(Colors.Yellow.Lighten3))
                                .AlignRight().Text(FormatEuro(newAo, ci));
                            table.Cell().Element(TdTotalValue).Text(""); // ticket modérateur inchangé / non surligné
                        }
                    });

                    if (isModification)
                    {
                        col.Item().PaddingTop(8).Text($"MODIFICATION — pièce n° {meta.Reference}").Bold();
                        if (modifiedTotalAoCents.HasValue)
                            col.Item().Text($"Nouveau montant total AO : {FormatEuro(modifiedTotalAoCents.Value, ci)}");
                        col.Item().Text($"Motif invoqué par la mutuelle : {meta.Reason}");
                        col.Item().Text($"Document de référence (mutuelle) : {meta.ReferenceDoc}");
                        col.Item().Text("Le montant total AO a été modifié suite à la réponse de la mutuelle.");
                    }

                    col.Item().PaddingTop(8).Text("Si nécessaire, vous pouvez ajouter des lignes à ce tableau.");
                    col.Item().PaddingTop(8).Text($"*Numéro de compte bancaire (IBAN) : {pr.IbanMutuelle}");
                    col.Item().Row(r =>
                    {
                        r.RelativeItem().Text("Cachet : (à apposer à la main)");
                        r.RelativeItem().Text($"*Date : {meta.Date:dd-MM-yy}");
                        r.RelativeItem().Text("*Signature : (manuscrite)");
                    });
                });
            });
        }).GeneratePdf(outputPath);
    }

    private static void EnsurePdfWritableOrThrow(string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new InvalidOperationException("Chemin de sortie PDF invalide.");

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        if (!File.Exists(outputPath))
            return;

        // Si le PDF est ouvert (souvent dans Adobe/Edge), la suppression échoue -> on stoppe et on demande de fermer.
        try
        {
            File.Delete(outputPath);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            var file = Path.GetFileName(outputPath);
            throw new InvalidOperationException(
                $"Le fichier PDF « {file} » est en cours d'utilisation par une autre application (souvent Adobe).\n\n" +
                "Veuillez fermer le PDF (et/ou Adobe) puis relancer la génération.",
                ex);
        }
    }

    private static IContainer Th(IContainer c) => c
        .Background(Colors.Grey.Lighten3)
        .PaddingVertical(4)
        .PaddingHorizontal(6)
        .DefaultTextStyle(x => x.SemiBold());

    private static IContainer Td(IContainer c) => c
        .BorderBottom(1)
        .BorderColor(Colors.Grey.Lighten2)
        .PaddingVertical(3)
        .PaddingHorizontal(6);

    private static IContainer TdTotalLabel(IContainer c) => c
        .PaddingTop(6)
        .BorderTop(1)
        .BorderColor(Colors.Grey.Lighten2)
        .PaddingVertical(4)
        .PaddingHorizontal(6)
        .DefaultTextStyle(x => x.SemiBold());

    private static IContainer TdTotalValue(IContainer c) => c
        .PaddingTop(6)
        .BorderTop(1)
        .BorderColor(Colors.Grey.Lighten2)
        .PaddingVertical(4)
        .PaddingHorizontal(6)
        .DefaultTextStyle(x => x.SemiBold());

    private static string FormatEuro(long cents, CultureInfo ci)
    {
        var v = cents / 100m;
        return v.ToString("N2", ci) + " €";
    }

    private static DateTime ParseIso(string iso)
        => DateTime.TryParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : DateTime.Now;

    private static string FormatDate(string iso)
        => ParseIso(iso).ToString("dd-MM-yyyy");

    private static string MonthName(string? period)
    {
        // period expected YYYY-MM
        if (string.IsNullOrWhiteSpace(period)) return "";
        if (DateTime.TryParseExact(period + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d.ToString("MMMM yyyy", CultureInfo.GetCultureInfo("fr-BE"));
        return period;
    }

    private static IEnumerable<string> SplitLines(string? block)
    {
        if (string.IsNullOrWhiteSpace(block))
            return new[] { "" };
        return block.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim());
    }

    private static string MakeSafeFileName(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "Document";
        foreach (var ch in Path.GetInvalidFileNameChars())
            s = s.Replace(ch, '_');
        return s.Trim();
    }
}

public sealed record InvoiceLineRow(string DateIso, string Label, long TotalCents);

/// <summary>Ligne patient pour l'état récapitulatif mutuelle. Classe (et non record) pour que Dapper puisse matérialiser correctement.</summary>
public sealed class MutualRow
{
    public string PatientName { get; set; } = "";
    public string Niss { get; set; } = "";
    public long IsBim { get; set; }
    public long AoCents { get; set; }
    public long TicketCents { get; set; }
}


public sealed record MutualRecapMeta(string Reference, string Reason, string ReferenceDoc, DateTime Date);
