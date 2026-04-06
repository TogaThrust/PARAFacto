using System.Windows;

namespace PARAFactoNative.Views;

public partial class SecurityCodeWindow : Window
{
    public string EnteredCode { get; private set; } = "";

    public SecurityCodeWindow()
    {
        InitializeComponent();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        EnteredCode = CodeBox.Password ?? "";
        DialogResult = true;
    }
}

