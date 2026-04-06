using System;
using System.Windows.Controls;

namespace PARAFactoNative.Views;

public partial class AgendaView
{
    public AgendaView()
    {
        InitializeComponent();
    }

    private void AppointmentDatePicker_OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is not DatePicker dp) return;
        dp.BlackoutDates.Clear();
        try
        {
            var end = DateTime.Today.AddDays(-1);
            dp.BlackoutDates.Add(new CalendarDateRange(new DateTime(1900, 1, 1), end));
        }
        catch
        {
            /* plage déjà enregistrée ou refus du calendrier */
        }
    }
}
