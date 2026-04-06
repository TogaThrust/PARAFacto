using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using PARAFactoNative.Models;

namespace PARAFactoNative.Services;

public sealed class JournalierPdfService
{
    static JournalierPdfService()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public string Generate(DateTime date, IReadOnlyList<SeanceRow> seances, string outputPdfPath)
    {
        if (seances is null) throw new ArgumentNullException(nameof(seances));
        if (string.IsNullOrWhiteSpace(outputPdfPath)) throw new ArgumentNullException(nameof(outputPdfPath));

        var outDir = Path.GetDirectoryName(outputPdfPath);
        if (!string.IsNullOrWhiteSpace(outDir))
            Directory.CreateDirectory(outDir);

        var fr = CultureInfo.GetCultureInfo("fr-BE");

        var title = $"ENCAISSEMENTS — {date:dd-MM-yyyy}";
        var generatedLabel = $"Document généré automatiquement — {date.ToString("dddd d MMMM yyyy", fr)}";

        string Euro(decimal v) => v.ToString("0.00", fr) + " €";

        var totalRecuPat = seances.Where(s => s.IsCash).Sum(s => s.PartPatientCents) / 100m;
        var totalFactPat = seances.Where(s => !s.IsCash).Sum(s => s.PartPatientCents) / 100m;
        var totalFactMut = seances.Where(s => !s.IsCash).Sum(s => s.PartMutuelleCents) / 100m;
        var totalJour = totalRecuPat + totalFactPat + totalFactMut;

        const float headerH = 18;
        const float rowH = 18;
        const int targetBodyRows = 22;

        Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(34);
                page.DefaultTextStyle(x => x.FontSize(9));

                page.Header()
                    .AlignCenter()
                    .PaddingBottom(8)
                    .Text(title)
                    .FontSize(14)
                    .SemiBold()
                    .FontColor(Colors.Grey.Darken3);

                page.Content().Column(col =>
                {
                    col.Item()
                       .AlignCenter()
                       .PaddingBottom(10)
                       .Text($"Séances : {seances.Count}")
                       .FontSize(10)
                       .FontColor(Colors.Grey.Darken1);

                    col.Item().Table(table =>
                    {
                        table.ColumnsDefinition(columns =>
                        {
                            columns.ConstantColumn(42);
                            columns.ConstantColumn(92);
                            columns.ConstantColumn(58);
                            columns.RelativeColumn(2.8f);
                            columns.ConstantColumn(58);
                            columns.ConstantColumn(58);
                            columns.ConstantColumn(62);
                        });

                        table.Header(header =>
                        {
                            header.Cell().Element(c => HeaderCell(c, headerH)).AlignCenter().Text("CODE");
                            header.Cell().Element(c => HeaderCell(c, headerH)).AlignCenter().Text("NISS");
                            header.Cell().Element(c => HeaderCell(c, headerH)).AlignCenter().Text("NOMENC");
                            header.Cell().Element(c => HeaderCell(c, headerH)).AlignCenter().Text("TARIF");
                            header.Cell().Element(c => HeaderCell(c, headerH)).AlignCenter().Text("RECU PAT");
                            header.Cell().Element(c => HeaderCell(c, headerH)).AlignCenter().Text("FACT PAT");
                            header.Cell().Element(c => HeaderCell(c, headerH)).AlignCenter().Text("FACT MUT");
                        });

                        foreach (var s in seances)
                        {
                            var partPatient = s.PartPatientCents / 100m;
                            var partMutuelle = s.PartMutuelleCents / 100m;
                            var tarif = s.TarifEncaissements ?? s.TarifLabel ?? "";

                            table.Cell().Element(c => RowCell(c, rowH)).Text((s.Code3 ?? "").Trim());
                            table.Cell().Element(c => RowCell(c, rowH)).Text((s.Niss ?? "").Trim());
                            table.Cell().Element(c => RowCell(c, rowH)).Text(string.IsNullOrWhiteSpace(s.Nomenclature) ? "0" : s.Nomenclature.Trim());
                            table.Cell().Element(c => RowCell(c, rowH)).Text(OneLineMax(tarif, 28));

                            table.Cell().Element(c => RowCellRight(c, rowH)).Text(s.IsCash && partPatient > 0 ? Euro(partPatient) : "");
                            table.Cell().Element(c => RowCellRight(c, rowH)).Text(!s.IsCash && partPatient > 0 ? Euro(partPatient) : "");
                            table.Cell().Element(c => RowCellRight(c, rowH)).Text(!s.IsCash && partMutuelle > 0 ? Euro(partMutuelle) : "");
                        }

                        var blanks = Math.Max(0, targetBodyRows - seances.Count);
                        for (int i = 0; i < blanks; i++)
                        {
                            table.Cell().Element(c => BlankSpace(c, rowH)).Text("");
                            table.Cell().Element(c => BlankSpace(c, rowH)).Text("");
                            table.Cell().Element(c => BlankSpace(c, rowH)).Text("");
                            table.Cell().Element(c => BlankSpace(c, rowH)).Text("");
                            table.Cell().Element(c => BlankSpace(c, rowH)).Text("");
                            table.Cell().Element(c => BlankSpace(c, rowH)).Text("");
                            table.Cell().Element(c => BlankSpace(c, rowH)).Text("");
                        }

                        table.Cell().ColumnSpan(4).Element(TotalsBand).AlignCenter().Text("TOTAUX");
                        table.Cell().Element(TotalsBandRight).Text(Euro(totalRecuPat));
                        table.Cell().Element(TotalsBandRight).Text(Euro(totalFactPat));
                        table.Cell().Element(TotalsBandRight).Text(Euro(totalFactMut));

                        table.Cell().ColumnSpan(7).Element(SpacerRow).Text("");

                        table.Cell().ColumnSpan(5).Element(DailyBand).AlignCenter().Text("TOTAL JOURNALIER").SemiBold();
                        table.Cell().Element(DailyBandRight).Text(Euro(totalJour)).SemiBold();
                        table.Cell().Element(DailyBandRight).Text("");
                    });

                    col.Item().PaddingTop(18).Text("Certifié sincère et véritable.").FontSize(11);
                    col.Item().PaddingTop(6).Text("Toute correction à ce document doit être faite manuellement et validée par un paraphe.").FontSize(11);
                    col.Item().PaddingTop(28).AlignRight().Text("Signature :").FontSize(11);
                });

                page.Footer()
                    .AlignCenter()
                    .PaddingTop(8)
                    .DefaultTextStyle(x => x.FontSize(8).FontColor(Colors.Grey.Darken1))
                    .Text(generatedLabel);
            });
        }).GeneratePdf(outputPdfPath);

        return outputPdfPath;
    }

    private static string OneLineMax(string? s, int maxChars)
    {
        s = (s ?? "").Trim();
        s = s.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
        while (s.Contains("  ")) s = s.Replace("  ", " ");

        if (s.Length <= maxChars) return s;
        if (maxChars <= 1) return "…";
        return s.Substring(0, maxChars - 1) + "…";
    }

    private static IContainer HeaderCell(IContainer c, float h)
        => c.Height(h)
            .Background(Colors.Grey.Darken3)
            .DefaultTextStyle(x => x.FontColor(Colors.White).SemiBold().FontSize(9))
            .AlignMiddle()
            .PaddingHorizontal(4);

    private static IContainer RowCell(IContainer c, float h)
        => c.Height(h)
            .AlignMiddle()
            .PaddingHorizontal(3)
            .BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten3);

    private static IContainer RowCellRight(IContainer c, float h)
        => c.Height(h)
            .AlignMiddle()
            .AlignRight()
            .PaddingHorizontal(3)
            .BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten3);

    private static IContainer BlankSpace(IContainer c, float h)
        => c.Height(h);

    private static IContainer TotalsBand(IContainer c)
        => c.Height(18)
            .Background(Colors.Grey.Lighten3)
            .AlignMiddle()
            .PaddingHorizontal(4)
            .DefaultTextStyle(x => x.SemiBold().FontSize(9));

    private static IContainer TotalsBandRight(IContainer c)
        => c.Height(18)
            .Background(Colors.Grey.Lighten3)
            .AlignMiddle()
            .AlignRight()
            .PaddingHorizontal(4)
            .DefaultTextStyle(x => x.SemiBold().FontSize(9));

    private static IContainer SpacerRow(IContainer c)
        => c.Height(10);

    private static IContainer DailyBand(IContainer c)
        => c.Height(18)
            .Background(Colors.Green.Lighten3)
            .AlignMiddle()
            .PaddingHorizontal(4)
            .DefaultTextStyle(x => x.SemiBold().FontSize(9));

    private static IContainer DailyBandRight(IContainer c)
        => c.Height(18)
            .Background(Colors.Green.Lighten3)
            .AlignMiddle()
            .AlignRight()
            .PaddingHorizontal(4)
            .DefaultTextStyle(x => x.SemiBold().FontSize(9));
}
