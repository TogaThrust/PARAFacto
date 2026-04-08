using PARAFactoNative.Services;

namespace PARAFactoNative.ViewModels;

public sealed class MainViewModel : NotifyBase
{
    public ConsoleViewModel Console { get; } = new();
    public LegalComplianceViewModel Legal { get; }
    public PatientsViewModel Patients { get; } = new();
    public TarifsViewModel Tarifs { get; } = new();
    public SeancesViewModel Seances { get; } = new();
    public FacturesViewModel Factures { get; } = new();
    public AgendaViewModel Agenda { get; } = new();

    private string _statusText = "Ready";
    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value);
    }

    public MainViewModel()
    {
        Legal = new LegalComplianceViewModel(new AppSettingsStore(), AppContext.BaseDirectory);

        // Prime lists
        Patients.Reload();
        Tarifs.Reload();
        Seances.Refresh();
        Factures.Reload();
        Console.ReloadRefs();

        // Console -> other tabs sync
        Console.RequestNewPatientRequested += () =>
        {
            Patients.BeginNew();
            StatusText = "Nouvelle fiche patient.";
        };

        Console.RequestEditPatientRequested += patientId =>
        {
            Patients.BeginEdit(patientId);
            StatusText = patientId is > 0
                ? $"Fiche patient chargée (ID {patientId})."
                : "Fiche patient chargée.";
        };

        Console.RequestShowSeancesRequested += () =>
        {
            Seances.SetDay(Console.SeanceDate);
            StatusText = $"Séances du {Console.SeanceDate:yyyy-MM-dd}.";
        };

        Console.RequestEditJournalierRequested += date =>
        {
            Seances.SetDay(date);
            StatusText = $"Séances du {date:yyyy-MM-dd}.";
        };

        Agenda.AgendaAppointmentDeleted += deletedDay =>
        {
            if (Console.SeanceDate.Date == deletedDay)
                Console.RefreshTodaySeancesList();
        };

        Console.LinkedAgendaDataChanged += () => Agenda.RefreshAppointmentsCalendar();

        Patients.ImportCompleted += ReloadAll;
    }

    public void ReloadAll()
    {
        Patients.Reload();
        Tarifs.Reload();
        Seances.Refresh();
        Factures.Reload();
        Agenda.ReloadRefs();
        Console.ReloadRefs();
    }
}
