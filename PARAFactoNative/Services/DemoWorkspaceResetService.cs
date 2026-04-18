using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using PARAFactoNative.Models;

namespace PARAFactoNative.Services;

/// <summary>
/// Supprime le fichier SQLite applicatif, recrée le schéma (<see cref="DbBootstrapper.EnsureDatabase"/>),
/// puis insère tarifs et patients de démonstration (suffixe « -Démo ») et des rendez-vous agenda pour un mois calendaire.
/// Le champ <see cref="Patient.Statut"/> porte le libellé exact du tarif RDV (onglet Tarifs).
/// </summary>
public static class DemoWorkspaceResetService
{
    public sealed class ResetAndSeedResult
    {
        public int Tarifs { get; init; }
        public int Patients { get; init; }
        public int Appointments { get; init; }
    }

    /// <summary>Libellés et montants (centimes) alignés sur une grille tarifaire cabinet / école courante.</summary>
    private static readonly (string Label, int PartPatientCents, int PartMutuelleCents)[] DemoTarifRows =
    {
        ("NON BIM CABINET 30 MIN", 3270, 0),
        ("BIM CABINET 30 MIN", 654, 2616),
        ("PLEIN CABINET 30 MIN", 3270, 0),
        ("NON BIM ECOLE 30 MIN", 3270, 0),
        ("BIM ECOLE 30 MIN", 654, 2616),
        ("PLEIN ECOLE 30 MIN", 3270, 0),
    };

    private sealed record DemoPatientSpec(
        string Code3,
        string LastName,
        string FirstName,
        /// <summary>Libellé exact d’un tarif dans <see cref="DemoTarifRows"/> (combo « Tarif RDV »).</summary>
        string TarifRdvLabel,
        string? MutualName,
        string? Referend,
        string Rue,
        string Numero,
        string Address1,
        string Zip,
        string City,
        string Country,
        string Phone,
        string Email,
        string Niss,
        string PrescriberLastName,
        string PrescriberFirstName,
        string PrescriberCode,
        string DatePrescription,
        string DateAccord,
        string PeriodeAccord,
        string Nomenclature,
        string Commentaire);

    private static readonly DemoPatientSpec[] DemoPatientSpecs =
    {
        new(
            Code3: "SLM",
            LastName: "Lemaire-Démo",
            FirstName: "Sophie",
            TarifRdvLabel: "NON BIM CABINET 30 MIN",
            MutualName: "MUTUELLE DÉMO ALPHA",
            Referend: "Institut Saint-Jean-Démo",
            Rue: "Rue des Fripiers",
            Numero: "14",
            Address1: "14 Rue des Fripiers",
            Zip: "7000",
            City: "Mons",
            Country: "BE",
            Phone: "0470123401",
            Email: "sophie.lemaire.demo@example.net",
            Niss: "96020199701",
            PrescriberLastName: "Fontaine",
            PrescriberFirstName: "Philippe",
            PrescriberCode: "12345678901",
            DatePrescription: "2025-11-10",
            DateAccord: "2025-12-01",
            PeriodeAccord: "01/01/2026 - 31/12/2026",
            Nomenclature: "246313",
            Commentaire: "Patient démo — suivi classique cabinet."
        ),
        new(
            Code3: "MVR",
            LastName: "Verstraeten-Démo",
            FirstName: "Marc",
            TarifRdvLabel: "BIM CABINET 30 MIN",
            MutualName: "MUTUELLE DÉMO BETA",
            Referend: "École communale Les Érables-Démo",
            Rue: "Avenue Mélina Mercouri",
            Numero: "3",
            Address1: "3 Avenue Mélina Mercouri",
            Zip: "7000",
            City: "Mons",
            Country: "BE",
            Phone: "0470123402",
            Email: "marc.verstraeten.demo@example.net",
            Niss: "85051299702",
            PrescriberLastName: "Dubois",
            PrescriberFirstName: "Anne",
            PrescriberCode: "10987654321",
            DatePrescription: "2025-10-05",
            DateAccord: "2025-11-15",
            PeriodeAccord: "01/09/2025 - 30/06/2026",
            Nomenclature: "246313",
            Commentaire: "Patient démo BIM — tiers payant."
        ),
        new(
            Code3: "JNG",
            LastName: "Ngoma-Démo",
            FirstName: "Julie",
            TarifRdvLabel: "PLEIN ECOLE 30 MIN",
            MutualName: null,
            Referend: "Athénée royal Liège-Démo",
            Rue: "Rue Saint-Gilles",
            Numero: "112",
            Address1: "112 Rue Saint-Gilles",
            Zip: "4000",
            City: "Liège",
            Country: "BE",
            Phone: "0470123403",
            Email: "julie.ngoma.demo@example.net",
            Niss: "90111599703",
            PrescriberLastName: "Henrard",
            PrescriberFirstName: "Luc",
            PrescriberCode: "11223344556",
            DatePrescription: "2025-09-20",
            DateAccord: "2025-10-01",
            PeriodeAccord: "01/10/2025 - 30/09/2026",
            Nomenclature: "246313",
            Commentaire: "Patient démo forfait plein — séances école."
        ),
        new(
            Code3: "CLB",
            LastName: "Bernard-Démo",
            FirstName: "Claire",
            TarifRdvLabel: "NON BIM ECOLE 30 MIN",
            MutualName: "CPAS DÉMO QUAREGNON",
            Referend: "CPAS de Quaregnon — Service social-Démo",
            Rue: "Rue Jules Destrée",
            Numero: "45",
            Address1: "45 Rue Jules Destrée",
            Zip: "7390",
            City: "Quaregnon",
            Country: "BE",
            Phone: "0470123404",
            Email: "claire.bernard.demo@example.net",
            Niss: "88030899704",
            PrescriberLastName: "Lambert",
            PrescriberFirstName: "Chantal",
            PrescriberCode: "22334455667",
            DatePrescription: "2025-12-02",
            DateAccord: "2025-12-20",
            PeriodeAccord: "01/01/2026 - 31/12/2026",
            Nomenclature: "246313",
            Commentaire: "Patient démo — orientation CPAS."
        ),
        new(
            Code3: "TPE",
            LastName: "Peeters-Démo",
            FirstName: "Tom",
            TarifRdvLabel: "BIM ECOLE 30 MIN",
            MutualName: "PARTENAMUT DÉMO",
            Referend: "Groupe scolaire Orion-Démo",
            Rue: "Rue des Mésanges",
            Numero: "8",
            Address1: "8 Rue des Mésanges",
            Zip: "7500",
            City: "Tournai",
            Country: "BE",
            Phone: "0470123405",
            Email: "tom.peeters.demo@example.net",
            Niss: "92042199705",
            PrescriberLastName: "Caron",
            PrescriberFirstName: "Isabelle",
            PrescriberCode: "33445566778",
            DatePrescription: "2025-11-28",
            DateAccord: "2025-12-10",
            PeriodeAccord: "01/01/2026 - 31/08/2026",
            Nomenclature: "246313",
            Commentaire: "Patient démo — même référent que d’autres séances (facturation groupe)."
        ),
        new(
            Code3: "EDU",
            LastName: "Dubois-Démo",
            FirstName: "Emma",
            TarifRdvLabel: "PLEIN CABINET 30 MIN",
            MutualName: "SOLIDARIS DÉMO",
            Referend: null,
            Rue: "Rue de la Montagne",
            Numero: "27",
            Address1: "27 Rue de la Montagne",
            Zip: "6000",
            City: "Charleroi",
            Country: "BE",
            Phone: "0470123406",
            Email: "emma.dubois.demo@example.net",
            Niss: "87090299706",
            PrescriberLastName: "Marchal",
            PrescriberFirstName: "David",
            PrescriberCode: "44556677889",
            DatePrescription: "2025-08-14",
            DateAccord: "2025-09-01",
            PeriodeAccord: "01/09/2025 - 31/08/2026",
            Nomenclature: "246313",
            Commentaire: "Patient démo — plein tarif cabinet."
        ),
        new(
            Code3: "LRN",
            LastName: "Renard-Démo",
            FirstName: "Lucas",
            TarifRdvLabel: "BIM CABINET 30 MIN",
            MutualName: "MUTUELLE CHRETIENNE DÉMO",
            Referend: "Les Petits Pas-Démo ASBL",
            Rue: "Rue de Fer",
            Numero: "6",
            Address1: "6 Rue de Fer",
            Zip: "5000",
            City: "Namur",
            Country: "BE",
            Phone: "0470123407",
            Email: "lucas.renard.demo@example.net",
            Niss: "93071499707",
            PrescriberLastName: "Poncelet",
            PrescriberFirstName: "Martine",
            PrescriberCode: "55667788990",
            DatePrescription: "2025-10-22",
            DateAccord: "2025-11-05",
            PeriodeAccord: "01/11/2025 - 31/10/2026",
            Nomenclature: "246313",
            Commentaire: "Patient démo — association partenaire."
        ),
        new(
            Code3: "NJC",
            LastName: "Jacobs-Démo",
            FirstName: "Nina",
            TarifRdvLabel: "NON BIM CABINET 30 MIN",
            MutualName: "MUTUELLE NEUTRE DÉMO",
            Referend: "Centre PMS Molenbeek-Démo",
            Rue: "Rue du Duc",
            Numero: "102",
            Address1: "102 Rue du Duc",
            Zip: "1080",
            City: "Molenbeek-Saint-Jean",
            Country: "BE",
            Phone: "0470123408",
            Email: "nina.jacobs.demo@example.net",
            Niss: "91010199708",
            PrescriberLastName: "Smet",
            PrescriberFirstName: "Véronique",
            PrescriberCode: "66778899001",
            DatePrescription: "2025-12-18",
            DateAccord: "2026-01-05",
            PeriodeAccord: "01/01/2026 - 31/12/2026",
            Nomenclature: "246313",
            Commentaire: "Patient démo — prise en charge PMS."
        ),
    };

    private static readonly string[] DaySlotTimes = { "09:00", "10:00", "11:00", "14:00" };

    public static void DeleteSqliteFiles()
    {
        SqliteConnection.ClearAllPools();
        AppPaths.EnsureDataDir();
        var path = AppPaths.DbPath;
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var p = path + suffix;
            if (!File.Exists(p)) continue;
            try
            {
                File.Delete(p);
            }
            catch
            {
                /* fichier verrouillé : l’app doit être fermée ailleurs ou pools non libérés */
            }
        }
    }

    /// <summary>
    /// Efface les fichiers SQLite, applique le schéma, ensemence tarifs / patients démo / RDV pour <paramref name="agendaYear"/>-<paramref name="agendaMonth"/>.
    /// </summary>
    public static ResetAndSeedResult DeleteRebootstrapAndSeed(int agendaYear, int agendaMonth)
    {
        if (agendaMonth is < 1 or > 12)
            throw new ArgumentOutOfRangeException(nameof(agendaMonth));

        BelgianHolidayHelper.Initialize();

        DeleteSqliteFiles();
        DbBootstrapper.EnsureDatabase();

        var tarifRepo = new TarifRepo();
        foreach (var (label, pp, pm) in DemoTarifRows)
            tarifRepo.Upsert(new Tarif { Label = label, PartPatientCents = pp, PartMutuelleCents = pm, Active = true });

        var tarifByLabel = tarifRepo.GetAll()
            .ToDictionary(t => (t.Label ?? "").Trim(), t => t.Id, StringComparer.OrdinalIgnoreCase);

        long ResolveTarifId(string? label)
        {
            var key = (label ?? "").Trim();
            if (!tarifByLabel.TryGetValue(key, out var id))
                throw new InvalidOperationException($"Tarif RDV inconnu (libellé manquant dans la grille tarifaire) : « {key} ».");
            return id;
        }

        var patientRepo = new PatientRepo();
        var insertedPatients = new List<Patient>();
        foreach (var spec in DemoPatientSpecs)
        {
            var p = new Patient
            {
                Code3 = spec.Code3,
                LastName = spec.LastName,
                FirstName = spec.FirstName,
                Statut = spec.TarifRdvLabel,
                MutualName = spec.MutualName,
                Referend = spec.Referend,
                Rue = spec.Rue,
                Numero = spec.Numero,
                Address1 = spec.Address1,
                Zip = spec.Zip,
                City = spec.City,
                Country = spec.Country,
                Phone = spec.Phone,
                Email = spec.Email,
                Niss = spec.Niss,
                PrescriberLastName = spec.PrescriberLastName,
                PrescriberFirstName = spec.PrescriberFirstName,
                PrescriberCode = spec.PrescriberCode,
                DatePrescription = spec.DatePrescription,
                DateAccord = spec.DateAccord,
                PeriodeAccord = spec.PeriodeAccord,
                Nomenclature = spec.Nomenclature,
                Commentaire = spec.Commentaire,
            };
            patientRepo.Upsert(p);
            insertedPatients.Add(p);
        }

        var apptRepo = new AppointmentRepo();
        var start = new DateTime(agendaYear, agendaMonth, 1);
        var end = start.AddMonths(1).AddDays(-1);
        var apptCount = 0;
        var patientIndex = 0;

        for (var d = start; d <= end; d = d.AddDays(1))
        {
            if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                continue;
            if (BelgianHolidayHelper.IsPublicHoliday(d))
                continue;

            foreach (var time in DaySlotTimes)
            {
                var patient = insertedPatients[patientIndex % insertedPatients.Count];
                patientIndex++;
                var tid = ResolveTarifId(patient.Statut);
                apptRepo.Insert(patient.Id, tid, d.ToString("yyyy-MM-dd"), time, 30, null);
                apptCount++;
            }
        }

        return new ResetAndSeedResult
        {
            Tarifs = DemoTarifRows.Length,
            Patients = insertedPatients.Count,
            Appointments = apptCount,
        };
    }
}
