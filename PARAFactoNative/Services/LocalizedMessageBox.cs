using System.Windows;

namespace PARAFactoNative.Services;

public static class LocalizedMessageBox
{
    public static MessageBoxResult Show(string messageBoxText) =>
        System.Windows.MessageBox.Show(UiTextTranslator.Translate(messageBoxText));

    public static MessageBoxResult Show(string messageBoxText, string caption) =>
        System.Windows.MessageBox.Show(UiTextTranslator.Translate(messageBoxText), UiTextTranslator.Translate(caption));

    public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button) =>
        System.Windows.MessageBox.Show(UiTextTranslator.Translate(messageBoxText), UiTextTranslator.Translate(caption), button);

    public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon) =>
        System.Windows.MessageBox.Show(UiTextTranslator.Translate(messageBoxText), UiTextTranslator.Translate(caption), button, icon);

    public static MessageBoxResult Show(Window? owner, string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon) =>
        owner is null
            ? System.Windows.MessageBox.Show(UiTextTranslator.Translate(messageBoxText), UiTextTranslator.Translate(caption), button, icon)
            : System.Windows.MessageBox.Show(owner, UiTextTranslator.Translate(messageBoxText), UiTextTranslator.Translate(caption), button, icon);
}
