using System.Globalization;
using System.Text.RegularExpressions;
using PARAFactoNative.Models;

namespace PARAFactoNative.Services;

/// <summary>Extrait l'heure de début depuis le commentaire d'import agenda (<c>[RDV#id] HH:mm</c>).</summary>
public static class SeanceRdvTimeHelper
{
    private static readonly Regex RdvComment =
        new(@"^\[RDV#\d+\]\s*(\d{1,2}:\d{2})", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RdvIdPrefix =
        new(@"^\[RDV#(\d+)\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RdvMarkerFull =
        new(@"^\[RDV#\d+\]\s*\d{1,2}:\d{2}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Extrait l’identifiant de rendez-vous agenda depuis le préfixe <c>[RDV#id]</c>.</summary>
    public static bool TryParseRdvAppointmentId(string? commentaire, out long appointmentId)
    {
        appointmentId = 0;
        if (string.IsNullOrWhiteSpace(commentaire)) return false;
        var m = RdvIdPrefix.Match(commentaire.Trim());
        if (!m.Success) return false;
        return long.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out appointmentId)
               && appointmentId > 0;
    }

    /// <summary>Préfixe exact du marqueur d’import (<c>[RDV#id] HH:mm</c>) au début du commentaire.</summary>
    public static bool TryGetRdvMarkerPrefix(string? commentaire, out string? prefix)
    {
        prefix = null;
        if (string.IsNullOrWhiteSpace(commentaire)) return false;
        var m = RdvMarkerFull.Match(commentaire.Trim());
        if (!m.Success) return false;
        prefix = m.Value;
        return true;
    }

    /// <summary>Texte libre après le marqueur agenda (vide s’il n’y a que le marqueur).</summary>
    public static string GetUserCommentAfterMarker(string? fullComment, string? markerPrefix)
    {
        if (string.IsNullOrEmpty(markerPrefix) || string.IsNullOrWhiteSpace(fullComment)) return fullComment ?? "";
        var c = fullComment.Trim();
        if (!c.StartsWith(markerPrefix, StringComparison.Ordinal)) return c;
        return c.Length > markerPrefix.Length ? c[markerPrefix.Length..].TrimStart() : "";
    }

    /// <summary>Recompose commentaire avec marqueur figé + notes utilisateur (console / formulaire).</summary>
    public static string? MergeRdvMarkerWithUserInput(string? originalFullComment, string? userNotesOnly)
    {
        if (!TryGetRdvMarkerPrefix(originalFullComment, out var p) || string.IsNullOrEmpty(p))
            return string.IsNullOrWhiteSpace(userNotesOnly) ? originalFullComment?.Trim() : userNotesOnly.Trim();
        if (string.IsNullOrWhiteSpace(userNotesOnly)) return p;
        return $"{p} {userNotesOnly.Trim()}";
    }

    /// <summary>
    /// Si la ligne avait un marqueur agenda, force sa présence : l’UI ne peut pas le supprimer ;
    /// le texte saisi sans marqueur devient le suffixe utilisateur.
    /// </summary>
    public static string? EnsurePreservedRdvMarkerOnUpdate(string? previousDbCommentaire, string? incomingFromUi)
    {
        if (!TryGetRdvMarkerPrefix(previousDbCommentaire, out var p) || string.IsNullOrEmpty(p))
            return string.IsNullOrWhiteSpace(incomingFromUi) ? null : incomingFromUi.Trim();
        if (string.IsNullOrWhiteSpace(incomingFromUi)) return p;
        var inc = incomingFromUi.Trim();
        if (inc.StartsWith(p, StringComparison.Ordinal))
        {
            if (inc.Length <= p.Length) return p;
            var rest = inc[p.Length..].TrimStart();
            return string.IsNullOrEmpty(rest) ? p : $"{p} {rest}";
        }
        return $"{p} {inc}";
    }

    public static TimeSpan? TryParseStartFromComment(string? commentaire)
    {
        if (string.IsNullOrWhiteSpace(commentaire)) return null;
        var m = RdvComment.Match(commentaire.Trim());
        if (!m.Success) return null;
        return TimeSpan.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, out var ts) ? ts : null;
    }

    /// <summary>Clé de tri : RDV importés par heure croissante ; séances sans marqueur en dernier (puis nom, id).</summary>
    public static TimeSpan SortKeyForDayList(SeanceRow r) =>
        TryParseStartFromComment(r.Commentaire) ?? TimeSpan.FromHours(48);
}
