using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace PARAFactoNative.Models;

public sealed class SeanceRow : INotifyPropertyChanged
{
    private static readonly Regex RdvMarkerAtStart =
        new(@"^\[RDV#\d+\]\s*\d{1,2}:\d{2}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public long Id { get; set; }

    public long PatientId { get; set; }

    public long TarifId { get; set; }

    // Stored fields (current canonical names)
    public string DateIso { get; set; } = "";
    public int PartPatient { get; set; }          // assumed cents
    public int PartMutuelle { get; set; }         // assumed cents

    private string? _commentaire;
    public string? Commentaire
    {
        get => _commentaire;
        set
        {
            if (_commentaire == value) return;
            _commentaire = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CommentaireEditable));
        }
    }

    /// <summary>Préfixe agenda non modifiable (rempli après chargement depuis la base).</summary>
    private string? _rdvAgendaMarkerPrefix;
    public string? RdvAgendaMarkerPrefix
    {
        get => _rdvAgendaMarkerPrefix;
        set
        {
            if (_rdvAgendaMarkerPrefix == value) return;
            _rdvAgendaMarkerPrefix = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CommentaireEditable));
        }
    }

    /// <summary>Partie éditable du commentaire (hors marqueur <c>[RDV#…] HH:mm</c>).</summary>
    public string CommentaireEditable
    {
        get
        {
            if (string.IsNullOrEmpty(RdvAgendaMarkerPrefix)) return Commentaire ?? "";
            var c = (Commentaire ?? "").Trim();
            var p = RdvAgendaMarkerPrefix;
            if (c.StartsWith(p, StringComparison.Ordinal))
                return c.Length > p.Length ? c[p.Length..].TrimStart() : "";
            return c;
        }
        set
        {
            if (RdvAgendaMarkerPrefix is null)
            {
                Commentaire = value;
                return;
            }
            var user = SanitizeUserPartAgainstMarker(value);
            Commentaire = string.IsNullOrEmpty(user) ? RdvAgendaMarkerPrefix : $"{RdvAgendaMarkerPrefix} {user}";
        }
    }

    private static string SanitizeUserPartAgainstMarker(string? raw)
    {
        var s = (raw ?? "").Trim();
        while (true)
        {
            var m = RdvMarkerAtStart.Match(s);
            if (!m.Success) break;
            s = s.Length > m.Length ? s[m.Length..].TrimStart() : "";
        }
        return s;
    }

    public string PatientNom { get; set; } = "";
    public string PatientPrenom { get; set; } = "";
    public string? Referend { get; set; }
    public string TarifLibelle { get; set; } = "";

    // --------------------------------------------------------------------
    // Backward/compat properties used by older services/ViewModels
    // --------------------------------------------------------------------

    // Some places expect SeanceId
    public long SeanceId
    {
        get => Id;
        set => Id = value;
    }

    // Amounts in cents (aliases)
    public int PartPatientCents
    {
        get => PartPatient;
        set => PartPatient = value;
    }

    public int PartMutuelleCents
    {
        get => PartMutuelle;
        set => PartMutuelle = value;
    }

    // Patient identification (may be filled by queries; defaults keep UI safe)
    public string? Code3 { get; set; }
    public string? Niss { get; set; }

    // Legacy "NOMENC" / nomenclature field
    public string? Nomenclature { get; set; }

    // Tariff labels used by PDF/console
    public string? TarifLabel
    {
        get => TarifLibelle;
        set => TarifLibelle = value ?? "";
    }

    public string? TarifEncaissements { get; set; }

    // Cash flag (used for RECU PAT column); default false
    public bool IsCash { get; set; }

    // Display helpers used by view models
    public string PatientDisplay
        => $"{(Code3 ?? "").Trim()} - {PatientNom} {PatientPrenom}".Trim();

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
