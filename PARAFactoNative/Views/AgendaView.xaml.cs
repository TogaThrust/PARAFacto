using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PARAFactoNative.ViewModels;

namespace PARAFactoNative.Views;

public partial class AgendaView
{
    public AgendaView()
    {
        InitializeComponent();
    }

    private void CalendarDayHeaderButton_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not AgendaViewModel vm) return;
        switch (sender)
        {
            case Button { DataContext: AgendaMonthCellVm mo }:
                vm.OnCalendarDateHeaderDoubleClick(mo.CellDate);
                e.Handled = true;
                break;
            case Button { DataContext: AgendaWeekColumnVm we }:
                vm.OnCalendarDateHeaderDoubleClick(we.Day);
                e.Handled = true;
                break;
        }
    }

    private void AgendaLineRow_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if (sender is not FrameworkElement { DataContext: AgendaLineVm line }) return;
        if (DataContext is not AgendaViewModel vm) return;
        if (!line.IsLunchBreak) return;
        vm.OnCalendarLunchLineDoubleClick(line);
        e.Handled = true;
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
