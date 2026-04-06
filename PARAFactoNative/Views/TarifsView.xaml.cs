namespace PARAFactoNative.Views;

public partial class TarifsView
{
    public TarifsView()
    {
        InitializeComponent();
    }

    // Sécurité UI: on interdit toute édition directe dans la grille.
    private void TarifsGrid_BeginningEdit(object sender, System.Windows.Controls.DataGridBeginningEditEventArgs e)
    {
        e.Cancel = true;
    }

    // Double-clic sur cellule: ne pas entrer en mode édition.
    private void TarifsGrid_PreviewMouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
    }
}
