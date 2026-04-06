using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using PARAFactoNative.Services;

namespace PARAFactoNative.Views;

public sealed class RelocateSlotItem
{
    public string Time { get; }
    public RelocateSlotItem(string time) => Time = time;
}

public partial class AppointmentRelocateSlotWindow : Window
{
    public string? SelectedSlotHhMm { get; private set; }

    public AppointmentRelocateSlotWindow(string patientLine, string currentSlotLine, IReadOnlyList<string> slots, string? responsiblePhone)
    {
        InitializeComponent();
        PatientLine.Text = patientLine;
        CurrentSlotLine.Text = currentSlotLine;
        foreach (var s in slots)
            SlotsList.Items.Add(new RelocateSlotItem(s));
        PhoneLine.Text = string.IsNullOrWhiteSpace(responsiblePhone)
            ? "(téléphone non renseigné dans la fiche patient)"
            : InternationalPhoneFormatter.FormatForDisplay(responsiblePhone);
    }

    private void SlotsList_OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        OkBtn.IsEnabled = SlotsList.SelectedItem is RelocateSlotItem;

    private void OkBtn_OnClick(object sender, RoutedEventArgs e)
    {
        if (SlotsList.SelectedItem is not RelocateSlotItem row) return;
        SelectedSlotHhMm = row.Time;
        DialogResult = true;
    }
}
