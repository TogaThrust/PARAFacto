using System;
using System.Windows;
using PARAFactoNative.Models;
using PARAFactoNative.Services;

namespace PARAFactoNative.Views;

public partial class PatientMedicalWindow : Window
{
    private readonly Patient _patient;
    private readonly PatientRepo _repo = new();

    public PatientMedicalWindow(Patient patient)
    {
        InitializeComponent();
        _patient = patient;
        DataContext = _patient;
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

            // Normalisation rapide du statut
            _patient.Statut = string.IsNullOrWhiteSpace(_patient.Statut) ? "NON BIM" : _patient.Statut.Trim();

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
