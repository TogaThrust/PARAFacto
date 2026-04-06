using System.Windows;
using PARAFactoNative.Views;

namespace PARAFactoNative.Services;

public static class ChoiceDialog
{
    public static bool AskYesNo(
        string title,
        string message,
        string yesLabel,
        string noLabel,
        Window? owner = null)
    {
        var win = new ActionChoiceWindow(title, message, yesLabel, noLabel, null)
        {
            Owner = owner ?? Application.Current?.MainWindow
        };
        return win.ShowDialog() == true && win.Choice == ActionChoiceResult.Primary;
    }

    public static ActionChoiceResult AskThree(
        string title,
        string message,
        string primaryLabel,
        string secondaryLabel,
        string cancelLabel,
        Window? owner = null)
    {
        var win = new ActionChoiceWindow(title, message, primaryLabel, secondaryLabel, cancelLabel)
        {
            Owner = owner ?? Application.Current?.MainWindow
        };
        return win.ShowDialog() == true ? win.Choice : ActionChoiceResult.Cancel;
    }
}
