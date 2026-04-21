using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Globalization;
using Dapper;
using PARAFactoNative.Models;

namespace PARAFactoNative.Services;

public sealed class PatientRepo
{
    public bool IsCode3Available(string? code3, long? excludePatientId = null)
    {
        var normalized = NormalizeCode3(code3);
        if (normalized.Length != 3)
            return false;

        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");
        var count = cn.ExecuteScalar<long>(
            "SELECT COUNT(1) FROM patients WHERE upper(trim(code3)) = @code AND (@excludeId IS NULL OR id <> @excludeId);",
            new { code = normalized, excludeId = excludePatientId });
        return count == 0;
    }

    public string GenerateSuggestedCode3(string? lastName, string? firstName, string? preferredCode3 = null, long? excludePatientId = null)
    {
        var hasNameInput = KeepLettersUpper(lastName).Length > 0 || KeepLettersUpper(firstName).Length > 0;
        var normalizedPreferred = NormalizeCode3(preferredCode3);
        if (!hasNameInput && normalizedPreferred.Length != 3)
            return "";

        var candidates = BuildCandidateCodes(lastName, firstName, preferredCode3);
        foreach (var c in candidates)
        {
            if (IsCode3Available(c, excludePatientId))
                return c;
        }

        for (var a = 'A'; a <= 'Z'; a++)
        for (var b = 'A'; b <= 'Z'; b++)
        for (var c = 'A'; c <= 'Z'; c++)
        {
            var code = string.Create(3, (a, b, c), static (span, t) =>
            {
                span[0] = t.a;
                span[1] = t.b;
                span[2] = t.c;
            });
            if (IsCode3Available(code, excludePatientId))
                return code;
        }

        return "ZZZ";
    }

    private static IEnumerable<string> BuildCandidateCodes(string? lastName, string? firstName, string? preferredCode3)
    {
        var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        var ln = KeepLettersUpper(lastName);
        var fn = KeepLettersUpper(firstName);
        var pref = NormalizeCode3(preferredCode3);
        var baseCode = BuildBaseCode(ln, fn);
        var hasNameInput = ln.Length > 0 || fn.Length > 0;

        bool add(string code)
        {
            if (code.Length != 3) return false;
            return seen.Add(code);
        }

        if (pref.Length == 3 && !hasNameInput) yield return pref;
        if (add(baseCode)) yield return baseCode;

        if (baseCode.Length == 3)
        {
            var prefix2 = baseCode.Substring(0, 2);
            foreach (var ch in fn)
            {
                var code = prefix2 + ch;
                if (add(code)) yield return code;
            }

            foreach (var ch in ln.Skip(2))
            {
                var code = $"{baseCode[0]}{ch}{baseCode[2]}";
                if (add(code)) yield return code;
            }
        }

        var pool = (ln + fn).Distinct().ToArray();
        foreach (var a in pool)
        foreach (var b in pool)
        foreach (var c in pool)
        {
            var code = $"{a}{b}{c}";
            if (add(code)) yield return code;
        }
    }

    private static string BuildBaseCode(string ln, string fn)
    {
        if (ln.Length == 0 && fn.Length == 0)
            return "";

        var pool = (ln + fn);
        var l1 = ln.Length > 0 ? ln[0] : (pool.Length > 0 ? pool[0] : 'X');
        var l2 = ln.Length > 1 ? ln[1] : (pool.Length > 1 ? pool[1] : 'X');
        var f1 = fn.Length > 0 ? fn[0] : (pool.Length > 2 ? pool[2] : 'X');
        return $"{l1}{l2}{f1}";
    }

    private static string NormalizeCode3(string? code)
    {
        var letters = KeepLettersUpper(code);
        return letters.Length > 3 ? letters[..3] : letters;
    }

    private static string KeepLettersUpper(string? s)
    {
        s ??= "";
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var ch in d)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetter(ch)) sb.Append(char.ToUpperInvariant(ch));
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    public Patient? GetById(long id)
    {
        if (id <= 0) return null;
        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");
        return cn.QueryFirstOrDefault<Patient>(@"
SELECT
  id        AS Id,
  code3     AS Code3,
  niss      AS Niss,
  nom       AS LastName,
  prenom    AS FirstName,
  rue       AS Rue,
  numero    AS Numero,
  adresse   AS Address1,
  cp        AS Zip,
  ville     AS City,
  pays      AS Country,
  telephone AS Phone,
  mail      AS Email,
  statut    AS Statut,
  mutuelle  AS MutualName,
  referend  AS Referend,
  prenom_med_presc AS PrescriberFirstName,
  nom_med_presc    AS PrescriberLastName,
  code_medecin     AS PrescriberCode,
  date_prescription AS DatePrescription,
  date_accord       AS DateAccord,
  periode_accord    AS PeriodeAccord,
  nomenclature      AS Nomenclature,
  commentaire      AS Commentaire
FROM patients
WHERE id=@id
LIMIT 1;
", new { id });
    }

    public string? GetTelephoneByPatientId(long patientId)
    {
        if (patientId <= 0) return null;
        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");
        return cn.ExecuteScalar<string?>(
            "SELECT COALESCE(NULLIF(trim(telephone),''), NULL) FROM patients WHERE id=@id;",
            new { id = patientId });
    }

    // ✅ compat ConsoleViewModel
    public List<Patient> GetAllSorted() => GetAll();

    public List<Patient> GetAll()
    {
        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");

        return cn.Query<Patient>(@"
SELECT
  id        AS Id,
  code3     AS Code3,
  niss      AS Niss,
  nom       AS LastName,
  prenom    AS FirstName,
  rue       AS Rue,
  numero    AS Numero,
  adresse   AS Address1,
  cp        AS Zip,
  ville     AS City,
  pays      AS Country,
  telephone AS Phone,
  mail      AS Email,
  statut    AS Statut,
  mutuelle  AS MutualName,
  referend  AS Referend,
  prenom_med_presc AS PrescriberFirstName,
  nom_med_presc    AS PrescriberLastName,
  code_medecin     AS PrescriberCode,
  date_prescription AS DatePrescription,
  date_accord       AS DateAccord,
  periode_accord    AS PeriodeAccord,
  nomenclature      AS Nomenclature,
  commentaire      AS Commentaire
FROM patients
ORDER BY nom, prenom;
").ToList();
    }

    public List<Patient> Search(string q)
    {
        q = (q ?? "").Trim();
        if (q.Length == 0) return GetAll();

        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");

        return cn.Query<Patient>(@"
SELECT
  id        AS Id,
  code3     AS Code3,
  niss      AS Niss,
  nom       AS LastName,
  prenom    AS FirstName,
  rue       AS Rue,
  numero    AS Numero,
  adresse   AS Address1,
  cp        AS Zip,
  ville     AS City,
  pays      AS Country,
  telephone AS Phone,
  mail      AS Email,
  statut    AS Statut,
  mutuelle  AS MutualName,
  referend  AS Referend,
  prenom_med_presc AS PrescriberFirstName,
  nom_med_presc    AS PrescriberLastName,
  code_medecin     AS PrescriberCode,
  date_prescription AS DatePrescription,
  date_accord       AS DateAccord,
  periode_accord    AS PeriodeAccord,
  nomenclature      AS Nomenclature,
  commentaire      AS Commentaire
FROM patients
WHERE
  code3     LIKE @p OR
  nom       LIKE @p OR
  prenom    LIKE @p OR
  mutuelle  LIKE @p OR
  niss      LIKE @p OR
  referend  LIKE @p
ORDER BY nom, prenom;
", new { p = $"%{q}%" }).ToList();
    }

    public void Upsert(Patient p)
    {
        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");

        if (p.Id <= 0)
        {
            var newId = cn.ExecuteScalar<long>(@"

INSERT INTO patients(
  code3, nom, prenom, niss, statut, mutuelle,
  rue, numero, adresse, cp, ville, pays, mail, telephone,
  referend,
  prenom_med_presc, nom_med_presc, code_medecin,
  date_prescription, date_accord, periode_accord, nomenclature, commentaire
)
VALUES(
  @Code3, @LastName, @FirstName, @Niss, @Statut, @MutualName,
  @Rue, @Numero, @Address1, @Zip, @City, @Country, @Email, @Phone,
  @Referend,
  @PrescriberFirstName, @PrescriberLastName, @PrescriberCode,
  @DatePrescription, @DateAccord, @PeriodeAccord, @Nomenclature, @Commentaire
);

SELECT last_insert_rowid();
", p);
            p.Id = (int)newId;
        }
        else
        {
            cn.Execute(@"
UPDATE patients
SET
  code3=@Code3,
  nom=@LastName,
  prenom=@FirstName,
  niss=@Niss,
  statut=@Statut,
  mutuelle=@MutualName,
  rue=@Rue,
  numero=@Numero,
  adresse=@Address1,
  cp=@Zip,
  ville=@City,
  pays=@Country,
  mail=@Email,
  telephone=@Phone,
  referend=@Referend,
  prenom_med_presc=@PrescriberFirstName,
  nom_med_presc=@PrescriberLastName,
  code_medecin=@PrescriberCode,
  date_prescription=@DatePrescription,
  date_accord=@DateAccord,
  periode_accord=@PeriodeAccord,
  nomenclature=@Nomenclature,
  commentaire=@Commentaire
            WHERE id=@Id;
", p);
        }
    }

    public long CountSeancesForPatient(long patientId)
    {
        if (patientId <= 0) return 0;
        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");
        return cn.ExecuteScalar<long>("SELECT COUNT(1) FROM seances WHERE patient_id=@id;", new { id = patientId });
    }

    /// <summary>
    /// Supprime le patient si aucune ligne dans <c>seances</c> (contrainte FK).
    /// Les rendez-vous agenda (<c>appointments</c>) sont supprimés en cascade par SQLite.
    /// </summary>
    public bool TryDeletePatientIfNoSeances(long patientId, out string error)
    {
        error = "";
        if (patientId <= 0)
        {
            error = "Identifiant patient invalide.";
            return false;
        }

        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");

        var n = cn.ExecuteScalar<long>("SELECT COUNT(1) FROM seances WHERE patient_id=@id;", new { id = patientId });
        if (n > 0)
        {
            error = $"Impossible de supprimer ce patient : {n} séance(s) déjà enregistrée(s).";
            return false;
        }

        try
        {
            var rows = cn.Execute("DELETE FROM patients WHERE id=@id;", new { id = patientId });
            if (rows == 0)
            {
                error = "Patient introuvable.";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}