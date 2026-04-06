using System.Windows.Controls;
using System.Windows.Input;
using PARAFactoNative.ViewModels;

namespace PARAFactoNative.Views;

public partial class FacturesView : UserControl
{
    public FacturesView()
    {
        InitializeComponent();
    }


    private void InvoicesGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    {
        // Seule la colonne "Commentaires" est éditable.
        var header = e.Column?.Header?.ToString() ?? "";
        if (!string.Equals(header, "Commentaires", System.StringComparison.OrdinalIgnoreCase))
            e.Cancel = true;
    }

    private void OpenPdf_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        // Row button: set selected to the row's DataContext, then open.
        if (sender is not Button b) return;
        if (b.DataContext is not InvoiceRow row) return;
        if (DataContext is not MainViewModel vm) return;
        if (vm.Factures is null) return;

        vm.Factures.Selected = row;

        if (vm.Factures.OpenPdfCommand.CanExecute(null))
            vm.Factures.OpenPdfCommand.Execute(null);
    }

    private void InvoicesGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit) return;
        if (e.Row?.Item is not InvoiceRow row) return;
        if (e.EditingElement is not TextBox tb) return;
        if (DataContext is not MainViewModel vm) return;

        var header = e.Column?.Header?.ToString() ?? "";
        if (!string.Equals(header, "Commentaires", System.StringComparison.OrdinalIgnoreCase)) return;

        var comment = tb.Text ?? "";
        row.UserComment = comment;
        vm.Factures.SaveInvoiceComment(row.InvoiceId, comment);
    }

    private void InvoicesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        if (grid.SelectedItem is not InvoiceRow row) return;
        if (DataContext is not MainViewModel vm) return;
        if (vm.Factures is null) return;

        vm.Factures.HandleInvoiceRowDoubleClick(row);
    }

}
