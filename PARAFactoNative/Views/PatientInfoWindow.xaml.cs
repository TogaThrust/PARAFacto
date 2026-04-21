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
    private readonly PatientRepo _repo = new();
    private bool _isAutoUpdatingCode;
    private bool _codeEditedByUser;
    private bool _isLoaded;

    public PatientInfoWindow(Patient patient)
    {
        InitializeComponent();
        Patient = patient;

        _originalCode3 = (patient.Code3 ?? "").Trim().ToUpperInvariant();

        // Code3 non modifiable une fois créé
        if (patient.Id > 0)
            TbCode3.IsReadOnly = true;
        else
            RefreshSuggestedCode3(keepUserCodeIfAvailable: true, showWarningIfReplaced: false);

        // L'app historique affiche l'adresse dans un champ unique (Adresse/Address1).
        // Ici on a 2 champs (Rue + Numéro). Donc on synchronise dans les 2 sens :
        // - si Rue/Numéro sont vides mais Adresse est remplie, on tente de "décomposer".
        // - au moment du save, on reconstruit Adresse à partir de Rue/Numéro.
        TrySplitAddressIntoStreetAndNumber(Patient);

        DataContext = Patient;
        Loaded += (_, _) => _isLoaded = true;
    }

    private void Code3_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_isAutoUpdatingCode || Patient.Id > 0 || !_isLoaded)
            return;
        if (TbCode3.IsKeyboardFocusWithin)
            _codeEditedByUser = true;
    }

    private void NameFields_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (Patient.Id > 0 || _codeEditedByUser)
            return;
        RefreshSuggestedCode3(keepUserCodeIfAvailable: true, showWarningIfReplaced: false);
    }

    private static string NormalizeCode3Letters(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "";
        var letters = new string(input.Where(char.IsLetter).ToArray()).ToUpperInvariant();
        return letters.Length <= 3 ? letters : letters[..3];
    }

    private void RefreshSuggestedCode3(bool keepUserCodeIfAvailable, bool showWarningIfReplaced)
    {
        if (Patient.Id > 0) return;
        var current = NormalizeCode3Letters(Patient.Code3);
        if (keepUserCodeIfAvailable && _codeEditedByUser && current.Length == 3 && _repo.IsCode3Available(current))
        {
            SetCode3Value(current);
            return;
        }

        var suggested = _repo.GenerateSuggestedCode3(Patient.LastName, Patient.FirstName, preferredCode3: null);
        var replacedUnavailableUserCode = current.Length == 3 && !string.Equals(current, suggested, StringComparison.OrdinalIgnoreCase);
        SetCode3Value(suggested);

        if (showWarningIfReplaced && replacedUnavailableUserCode)
        {
            MessageBox.Show(
                $"Le code '{current}' est déjà utilisé. Proposition automatique: '{suggested}'.",
                "PARAFacto",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void SetCode3Value(string? value)
    {
        var normalized = NormalizeCode3Letters(value);
        _isAutoUpdatingCode = true;
        Patient.Code3 = normalized;
        if (TbCode3.Text != normalized)
            TbCode3.Text = normalized;
        _isAutoUpdatingCode = false;
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
                SetCode3Value(Patient.Code3);

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
                RefreshSuggestedCode3(keepUserCodeIfAvailable: true, showWarningIfReplaced: true);
                var code = NormalizeCode3Letters(Patient.Code3);
                if (code.Length != 3)
                {
                    MessageBox.Show("Le code doit contenir exactement 3 lettres.", "PARAFacto",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                SetCode3Value(code);
            }

            // Construit le champ adresse unique attendu par la DB / l'affichage "Patients"
            // (adresse = "Rue + Numéro"), tout en gardant aussi Rue/Numero en colonnes dédiées.
            var rue = (Patient.Rue ?? "").Trim();
            var num = (Patient.Numero ?? "").Trim();
            var addr = (rue + " " + num).Trim();
            Patient.Address1 = string.IsNullOrWhiteSpace(addr) ? null : addr;

            _repo.Upsert(Patient);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Enregistrement patient - erreur", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
