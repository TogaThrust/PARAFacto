using System;
using System.Linq;
using System.Windows;
using PARAFactoNative.Models;
using PARAFactoNative.Services;

namespace PARAFactoNative.Views;

public partial class PatientInfoWindow : Window
{
    public Patient Patient { get; }
    private readonly string _originalCode3;

    public PatientInfoWindow(Patient patient)
    {
        InitializeComponent();
        Patient = patient;

        _originalCode3 = (patient.Code3 ?? "").Trim().ToUpperInvariant();

        // Code3 non modifiable une fois créé
        if (patient.Id > 0)
            TbCode3.IsReadOnly = true;

        // L'app historique affiche l'adresse dans un champ unique (Adresse/Address1).
        // Ici on a 2 champs (Rue + Numéro). Donc on synchronise dans les 2 sens :
        // - si Rue/Numéro sont vides mais Adresse est remplie, on tente de "décomposer".
        // - au moment du save, on reconstruit Adresse à partir de Rue/Numéro.
        TrySplitAddressIntoStreetAndNumber(Patient);

        DataContext = Patient;
    }

    private static void TrySplitAddressIntoStreetAndNumber(Patient p)
    {
        if (p == null) return;

        var hasRue = !string.IsNullOrWhiteSpace(p.Rue);
        var hasNum = !string.IsNullOrWhiteSpace(p.Numero);
        if (hasRue || hasNum) return;

        var a = (p.Address1 ?? "").Trim();
        if (a.Length == 0) return;

        // Heuristique simple: "Rue du Bois 157" => Rue="Rue du Bois", Numero="157"
        // (si pas de numéro identifiable, on laisse tout dans Rue)
        var parts = a.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 1)
        {
            p.Rue = a;
            return;
        }

        var last = parts[^1];
        // Numéro = commence par un chiffre (157, 12B, 4/2, etc.)
        if (last.Length > 0 && char.IsDigit(last[0]))
        {
            p.Numero = last;
            p.Rue = string.Join(' ', parts, 0, parts.Length - 1);
        }
        else
        {
            p.Rue = a;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Normalise code3
            if (!string.IsNullOrWhiteSpace(Patient.Code3))
                Patient.Code3 = Patient.Code3.Trim().ToUpperInvariant();

            // Interdit modification du Code3 après création
            if (Patient.Id > 0 && !string.Equals(Patient.Code3 ?? "", _originalCode3, StringComparison.OrdinalIgnoreCase))
            {
                Patient.Code3 = _originalCode3;
                MessageBox.Show("Le code 3 lettres ne peut pas être modifié pour un patient existant.",
                    "PARAFacto", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Vérifie unicité du Code3 à la création
            if (Patient.Id <= 0)
            {
                var code = (Patient.Code3 ?? "").Trim().ToUpperInvariant();
                if (code.Length != 3)
                {
                    MessageBox.Show("Le code doit contenir exactement 3 lettres.", "PARAFacto",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var exists = new PatientRepo().Search(code).Any(p => string.Equals(p.Code3, code, StringComparison.OrdinalIgnoreCase));
                if (exists)
                {
                    MessageBox.Show($"Le code '{code}' est déjà utilisé. Choisis un autre code.", "PARAFacto",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            // Construit le champ adresse unique attendu par la DB / l'affichage "Patients"
            // (adresse = "Rue + Numéro"), tout en gardant aussi Rue/Numero en colonnes dédiées.
            var rue = (Patient.Rue ?? "").Trim();
            var num = (Patient.Numero ?? "").Trim();
            var addr = (rue + " " + num).Trim();
            Patient.Address1 = string.IsNullOrWhiteSpace(addr) ? null : addr;

            new PatientRepo().Upsert(Patient);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Enregistrement patient - erreur", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
