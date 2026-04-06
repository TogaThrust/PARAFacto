using System.Collections.Generic;
using System.Linq;
using Dapper;
using PARAFactoNative.Models;

namespace PARAFactoNative.Services;

public sealed class TarifRepo
{
    // ✅ compat ConsoleViewModel
    public IReadOnlyList<Tarif> GetAllActive() => GetActive();

    public IReadOnlyList<Tarif> GetAll()
    {
        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");

        return cn.Query<Tarif>(@"
SELECT
  id            AS Id,
  libelle       AS Label,
  part_patient  AS PartPatientCents,
  part_mutuelle AS PartMutuelleCents,
  is_active     AS Active
FROM tarifs
ORDER BY libelle;
").ToList();
    }

    public IReadOnlyList<Tarif> GetActive() =>
        GetAll().Where(t => t.Active).ToList();

    public void Upsert(Tarif t)
    {
        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");

        // Unicité logique sur libelle (simple et pratique)
        cn.Execute(@"
INSERT INTO tarifs(libelle, part_patient, part_mutuelle, is_active)
VALUES(@Label, @PartPatientCents, @PartMutuelleCents, @a)
ON CONFLICT(libelle) DO UPDATE SET
  part_patient=excluded.part_patient,
  part_mutuelle=excluded.part_mutuelle,
  is_active=excluded.is_active;
", new
        {
            t.Label,
            t.PartPatientCents,
            t.PartMutuelleCents,
            a = t.Active ? 1 : 0
        });
    }

    /// <summary>Nombre de séances qui pointent encore vers ce tarif (bloque la suppression, FK RESTRICT).</summary>
    public long CountSeancesUsingTarif(long tarifId)
    {
        if (tarifId <= 0) return 0;
        using var cn = Db.Open();
        return cn.QuerySingle<long>("SELECT COUNT(*) FROM seances WHERE tarif_id = @tarifId;", new { tarifId });
    }

    public void Delete(long id)
    {
        if (id <= 0) return;
        using var cn = Db.Open();
        cn.Execute("PRAGMA foreign_keys = ON;");
        cn.Execute("DELETE FROM tarifs WHERE id = @id;", new { id });
    }
}