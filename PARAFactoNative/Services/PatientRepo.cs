using System.Collections.Generic;
using System.Linq;
using Dapper;
using PARAFactoNative.Models;

namespace PARAFactoNative.Services;

public sealed class PatientRepo
{
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
}