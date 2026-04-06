using System;
using System.Windows;
using PARAFactoNative.Models;
using PARAFactoNative.Services;

namespace PARAFactoNative.Views;

public partial class MedicalInfoWindow : Window
{
    public Patient Patient { get; }

    public MedicalInfoWindow(Patient patient)
    {
        InitializeComponent();
        Patient = patient;
        DataContext = Patient;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            new PatientRepo().Upsert(Patient);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Enregistrement médical - erreur", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
