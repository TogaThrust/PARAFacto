using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using PARAFactoNative.Models;
using PARAFactoNative.Services;

namespace PARAFactoNative.Views;

public partial class PatientMedicalWindow : Window
{
    private readonly Patient _patient;
    private readonly PatientRepo _repo = new();

    /// <summary>Libellés des tarifs actifs (même logique que l’onglet Patients « Tarif RDV »).</summary>
    public ObservableCollection<string> StatutChoices { get; } = new();

    public PatientMedicalWindow(Patient patient)
    {
        InitializeComponent();
        _patient = patient;
        DataContext = _patient;
        LoadTariffChoices();
    }

    private void LoadTariffChoices()
    {
        StatutChoices.Clear();
        var tarifRepo = new TarifRepo();
        foreach (var t in tarifRepo.GetActive())
        {
            var label = (t.Label ?? "").Trim();
            if (label.Length == 0) continue;
            if (StatutChoices.Any(x => string.Equals(x, label, StringComparison.OrdinalIgnoreCase))) continue;
            StatutChoices.Add(label);
        }

        var current = (_patient.Statut ?? "").Trim();

        if (IsLegacyBimStatut(current) && StatutChoices.Count > 0)
        {
            _patient.Statut = StatutChoices[0];
            return;
        }

        if (current.Length > 0 &&
            !StatutChoices.Any(x => string.Equals(x, current, StringComparison.OrdinalIgnoreCase)))
            StatutChoices.Add(current);

        if (string.IsNullOrWhiteSpace(_patient.Statut) && StatutChoices.Count > 0)
            _patient.Statut = StatutChoices[0];
    }

    private static bool IsLegacyBimStatut(string s)
    {
        s = (s ?? "").Trim();
        return s.Equals("BIM", StringComparison.OrdinalIgnoreCase)
               || s.Equals("NON BIM", StringComparison.OrdinalIgnoreCase)
               || s.Equals("PLEIN", StringComparison.OrdinalIgnoreCase);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Le patient doit déjà exister (créé via la 1ère fenêtre)
            if (_patient.Id <= 0)
            {
                MessageBox.Show("Le patient n'est pas encore créé (Id manquant).", "PARAFacto", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Le statut contient désormais le tarif par défaut de RDV (libellé exact du référentiel tarifs).
            _patient.Statut = string.IsNullOrWhiteSpace(_patient.Statut) ? "" : _patient.Statut.Trim();

            _repo.Upsert(_patient);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
