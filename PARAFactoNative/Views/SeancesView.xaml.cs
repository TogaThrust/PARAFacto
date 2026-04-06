using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PARAFactoNative.Views;

public partial class SeancesView : UserControl
{
    private const string MsgEditionViaSeances =
        "L'édition n'est pas possible à partir de cette page, seulement la suppression. Pour modifier une séance, rendez-vous sur l'onglet « Console », sélectionnez la date voulue et la ligne de séance à modifier. Faites alors la modification.";

    public SeancesView()
    {
        InitializeComponent();
    }

    private void SeancesDataGrid_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject src) return;
        if (ItemsControl.ContainerFromElement(SeancesDataGrid, src) is not DataGridRow) return;

        MessageBox.Show(MsgEditionViaSeances, "PARAFacto — Séances", MessageBoxButton.OK, MessageBoxImage.Information);
        e.Handled = true;
    }
}
