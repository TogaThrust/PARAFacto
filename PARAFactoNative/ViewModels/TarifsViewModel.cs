using System.Windows;
using Microsoft.Data.Sqlite;
using PARAFactoNative.Models;
using PARAFactoNative.Services;
using PARAFactoNative.Views;

namespace PARAFactoNative.ViewModels;

public sealed class TarifsViewModel : NotifyBase
{
    private readonly TarifRepo _repo = new();

    public List<Tarif> Items { get; private set; } = new();

    private Tarif? _selected;
    public Tarif? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
            {
                EditCommand.RaiseCanExecuteChanged();
                DeleteCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public RelayCommand NewCommand { get; }
    public RelayCommand EditCommand { get; }
    public RelayCommand DeleteCommand { get; }

    public TarifsViewModel()
    {
        NewCommand = new RelayCommand(NewTarif);
        EditCommand = new RelayCommand(EditTarif, () => Selected != null);
        DeleteCommand = new RelayCommand(DeleteTarif, () => Selected != null);
    }

    private void NewTarif()
    {
        var w = new TarifEditWindow(null) { Owner = Application.Current.MainWindow };
        if (w.ShowDialog() != true || w.Result is null) return;
        _repo.Upsert(w.Result);
        Reload();
    }

    private void EditTarif()
    {
        if (Selected is null) return;
        var w = new TarifEditWindow(Selected) { Owner = Application.Current.MainWindow };
        if (w.ShowDialog() != true || w.Result is null) return;
        _repo.Upsert(w.Result);
        Reload();
    }

    private void DeleteTarif()
    {
        if (Selected is null) return;

        var usage = _repo.CountSeancesUsingTarif(Selected.Id);
        if (usage > 0)
        {
            MessageBox.Show(
                $"Impossible de supprimer le tarif « {Selected.Label} » : il est encore utilisé par {usage} séance(s).\n\n" +
                "Pour le retirer des listes à l’encodage sans perdre l’historique, utilisez « Modifier » et décochez « Actif ».",
                "PARAFacto — Suppression impossible",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!ChoiceDialog.AskYesNo(
                "PARAFacto",
                $"Supprimer le tarif « {Selected.Label} » ?",
                "Supprimer",
                "Annuler"))
            return;

        try
        {
            _repo.Delete(Selected.Id);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // SQLITE_CONSTRAINT (ex. clé étrangère)
        {
            MessageBox.Show(
                "La suppression a échoué : ce tarif est encore référencé par des données.\n\n" +
                "Vous pouvez le désactiver via « Modifier » pour qu’il n’apparaisse plus dans les listes.",
                "PARAFacto",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        Reload();
    }

    public void Reload()
    {
        Items = _repo.GetAll().ToList();
        Raise(nameof(Items));
    }
}
