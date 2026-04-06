using System.Windows;

namespace PARAFactoNative.Services;

/// <summary>
/// Rappel après modification ou suppression d’un RDV : prévenir la personne responsable (téléphone comme dans le déplacement de créneau).
/// </summary>
public static class AppointmentResponsibleNotify
{
    public static void ShowNotifyResponsibleDialog(PatientRepo patientRepo, long patientId, string title = "Agenda")
    {
        if (patientId <= 0) return;
        var raw = patientRepo.GetTelephoneByPatientId(patientId);
        string body;
        if (string.IsNullOrWhiteSpace(raw))
        {
            body =
                "Prévenir la personne responsable : (téléphone non renseigné dans la fiche patient).";
        }
        else
        {
            var formatted = InternationalPhoneFormatter.FormatForDisplay(raw);
            body = $"Prévenir la personne responsable au {formatted}.";
        }

        MessageBox.Show(body, title, MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
