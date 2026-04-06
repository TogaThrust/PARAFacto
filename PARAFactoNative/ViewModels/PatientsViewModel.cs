using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using Dapper;
using PARAFactoNative.Models;
using PARAFactoNative.Services;

namespace PARAFactoNative.ViewModels;

/// <summary>
/// ViewModel Patients : liste + édition.
/// 
/// Objectif : permettre à la Console de déclencher "Nouveau patient" / "Modifier" et
/// d'afficher un écran d'édition (données personnelles + médicales).
/// 
/// ⚠️ On fait un Save "schema-aware" : on lit PRAGMA table_info(patients) et on ne tente
/// de renseigner que les colonnes existantes. Ça évite de casser si la DB évolue.
/// </summary>
public sealed class PatientsViewModel : NotifyBase
{
    private readonly PatientRepo _repo = new();
    private readonly ImportService _import = new();

    /// <summary>Déclenché après un import réussi pour que la Console et les autres onglets se rechargent.</summary>
    public event Action? ImportCompleted;

    public ObservableCollection<Patient> Items { get; } = new();

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!Set(ref _searchText, value)) return;
            Reload();
            OnPropertyChanged(nameof(CountLabel));
        }
    }

    public string CountLabel => $"{Items.Count} patient(s)";

    private Patient? _selected;
    public Patient? Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            OnPropertyChanged();
            EditCommand.RaiseCanExecuteChanged();
            ApplySelectionToEditor();
        }
    }

    // ===== Edition state
    private bool _isEditing;
    public bool IsEditing
    {
        get => _isEditing;
        private set
        {
            if (_isEditing == value) return;
            _isEditing = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsReadOnly));

            // IMPORTANT: RelayCommand ne se réévalue pas automatiquement
            SaveCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsReadOnly => !IsEditing;

    public IReadOnlyList<string> StatutChoices { get; } = new[] { "BIM", "NON BIM", "PLEIN" };

    public bool IsCode3ReadOnly => !IsNew; // modifiable uniquement à la création

    private bool _isNew;
    public bool IsNew
    {
        get => _isNew;
        private set
        {
            if (_isNew == value) return;
            _isNew = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsCode3ReadOnly));
        }
    }

    private long _editingId;
    public long EditingId
    {
        get => _editingId;
        private set
        {
            if (_editingId == value) return;
            _editingId = value;
            OnPropertyChanged();
        }
    }

    // Code d'origine (pour interdire la modification du Code3 après création)
    private string _originalCode3 = "";

    // ===== Champs (personnel)
    private string _code3 = "";
    public string Code3 { get => _code3; set => Set(ref _code3, value); }

    private string _nom = "";
    public string Nom { get => _nom; set => Set(ref _nom, value); }

    private string _prenom = "";
    public string Prenom { get => _prenom; set => Set(ref _prenom, value); }

    private string _statut = "";
    public string Statut { get => _statut; set => Set(ref _statut, value); }

    private string _mutuelle = "";
    public string Mutuelle
    {
        get => _mutuelle;
        set => Set(ref _mutuelle, NormalizeMutuelle(value));
    }

    private string _niss = "";
    public string Niss { get => _niss; set => Set(ref _niss, value); }

    private string _telephone = "";
    public string Telephone { get => _telephone; set => Set(ref _telephone, value); }

    private string _email = "";
    public string Email { get => _email; set => Set(ref _email, value); }

    private string _rue = "";
    public string Rue { get => _rue; set => Set(ref _rue, value); }

    private string _numero = "";
    public string Numero { get => _numero; set => Set(ref _numero, value); }

    private string _adresse = "";
    public string Adresse { get => _adresse; set => Set(ref _adresse, value); } // champ concat (optionnel)

    private string _cp = "";
    public string CodePostal { get => _cp; set => Set(ref _cp, value); }

    private string _ville = "";
    public string Ville { get => _ville; set => Set(ref _ville, value); }

    private string _pays = "";
    public string Pays { get => _pays; set => Set(ref _pays, value); }

    private string _referend = "";
    public string Referend { get => _referend; set => Set(ref _referend, value); }

    private DateTime? _dateNaissance;
    public DateTime? DateNaissance { get => _dateNaissance; set => Set(ref _dateNaissance, value); }

    // ===== Champs (médical) - conforme à la table patients
    private string _prescriberLastName = "";
    public string PrescriberLastName { get => _prescriberLastName; set => Set(ref _prescriberLastName, value); }

    private string _prescriberFirstName = "";
    public string PrescriberFirstName { get => _prescriberFirstName; set => Set(ref _prescriberFirstName, value); }

    private string _prescriberCode = "";
    public string PrescriberCode { get => _prescriberCode; set => Set(ref _prescriberCode, value); }

    private string _datePrescription = "";
    public string DatePrescription { get => _datePrescription; set => Set(ref _datePrescription, value); }

    private string _dateAccord = "";
    public string DateAccord { get => _dateAccord; set => Set(ref _dateAccord, value); }

    private string _periodeAccord = "";
    public string PeriodeAccord { get => _periodeAccord; set => Set(ref _periodeAccord, value); }

    private string _nomenclature = "";
    public string Nomenclature { get => _nomenclature; set => Set(ref _nomenclature, value); }

    private string _commentaire = "";
    public string Commentaire { get => _commentaire; set => Set(ref _commentaire, value); }

    // ===== Commands
    public RelayCommand ReloadCommand { get; }
    public RelayCommand ImportCommand { get; }
    public RelayCommand ReimportCommand { get; }
    public RelayCommand NewCommand { get; }
    public RelayCommand EditCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand CancelCommand { get; }

    public PatientsViewModel()
    {
        // RelayCommand (non générique) attend des délégués SANS paramètre : Action / Func<bool>
        ReloadCommand = new RelayCommand(() => Reload());

        ImportCommand = new RelayCommand(() => Import(doWipe: false));
        ReimportCommand = new RelayCommand(() => Import(doWipe: true));

        NewCommand = new RelayCommand(() => BeginNew());
        EditCommand = new RelayCommand(() => EditOrSave(), () => Selected is not null || IsEditing);
        SaveCommand = new RelayCommand(() => Save(), () => IsEditing);
        CancelCommand = new RelayCommand(() => Cancel(), () => IsEditing);

        Reload();
    }

    private static string NormalizeMutuelle(string? s)
    {
        s = (s ?? "").Trim();
        if (s.Length == 0) return "";

        // Supprimer accents/diacritiques
        var normalized = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            var uc = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (uc != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }

        // MAJUSCULES + recomposition
        return sb.ToString()
            .Normalize(NormalizationForm.FormC)
            .ToUpperInvariant();
    }

    public void Reload()
    {
        Items.Clear();

        var q = (SearchText ?? "").Trim();
        var list = q.Length == 0 ? _repo.GetAllSorted() : _repo.Search(q);
        foreach (var p in list)
            Items.Add(p);

        if (Selected is not null)
        {
            var currentId = GetId(Selected);
            Selected = Items.FirstOrDefault(x => GetId(x) == currentId) ?? Items.FirstOrDefault();
        }
        else
        {
            Selected = Items.FirstOrDefault();
        }

        OnPropertyChanged(nameof(CountLabel));
    }


    private void ApplySelectionToEditor()
    {
        if (Selected is null) return;
        if (IsNew) return; // ne pas écraser une création en cours

        LoadFieldsFromPatient(Selected);
        IsEditing = true;
        IsNew = false;
        EditingId = GetId(Selected);
        _originalCode3 = Code3;
    }

    private void EditOrSave()
    {
        if (IsEditing && !IsNew)
        {
            Save();
            return;
        }

        BeginEdit(Selected);
    }

    private void Import(bool doWipe)
    {
        try
        {
            // One-click import from the fixed workspace folder:
            // ...\Documents\PARAFACTO_Native (prefers OneDrive\Documents when present)
            var res = _import.ImportAllFromDefaultFolder();

            var msg = $"Import terminé.\n\nPatients: {res.Patients}, Tarifs: {res.Tarifs}, Séances: {res.Seances}\nFactures: {res.Factures}, Paiements: {res.Paiements}, Pertes: {res.Pertes}";
            if (res.Factures > 0)
                msg += "\n\nLes factures affichées proviennent des fichiers patients_invoices_log.csv et mutuelles_invoices_log.csv du dossier d'import. Pour ne plus voir des factures de test (ex. 04-2026), supprimez les lignes correspondantes dans ces CSV puis réimportez.";
            MessageBox.Show(msg, "PARAFacto", MessageBoxButton.OK, MessageBoxImage.Information);

            Reload();
            ImportCompleted?.Invoke();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "Import - erreur", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ==== Entrées depuis la Console
    public void BeginNew()
    {
        IsEditing = true;
        IsNew = true;
        EditingId = 0;
        ClearFields();
        _originalCode3 = "";
    }

    public void BeginEdit(long? patientId)
    {
        if (patientId is null || patientId <= 0)
        {
            BeginEdit(Selected);
            return;
        }

        var p = Items.FirstOrDefault(x => GetId(x) == patientId.Value);
        if (p is null)
        {
            Reload();
            p = Items.FirstOrDefault(x => GetId(x) == patientId.Value);
        }

        BeginEdit(p);
    }

    private void BeginEdit(Patient? p)
    {
        if (p is null) return;

        IsEditing = true;
        IsNew = false;
        EditingId = GetId(p);
        LoadFieldsFromPatient(p);
        _originalCode3 = Code3;
    }

    private void Cancel()
    {
        IsEditing = false;
        IsNew = false;
        EditingId = 0;
        if (Selected is not null)
            LoadFieldsFromPatient(Selected);
        else
            ClearFields();
        _originalCode3 = "";
    }

    private void ClearFields()
    {
        Code3 = "";
        Nom = "";
        Prenom = "";
        Statut = "";
        Mutuelle = "";
        Niss = "";
        Telephone = "";
        Email = "";
        Rue = "";
        Numero = "";
        Adresse = "";
        CodePostal = "";
        Ville = "";
        Pays = "";
        Referend = "";
        DateNaissance = null;
        PrescriberLastName = "";
        PrescriberFirstName = "";
        PrescriberCode = "";
        DatePrescription = "";
        DateAccord = "";
        PeriodeAccord = "";
        Nomenclature = "";
        Commentaire = "";
    }

    private static long GetId(Patient p)
    {
        var t = p.GetType();
        var pi = t.GetProperty("Id") ?? t.GetProperty("PatientId");
        var v = pi?.GetValue(p);
        return v switch
        {
            long l => l,
            int i => i,
            _ => 0
        };
    }

    private void LoadFieldsFromPatient(Patient p)
    {
        object? Get(params string[] names)
        {
            var t = p.GetType();
            foreach (var n in names)
            {
                var pi = t.GetProperty(n, BindingFlags.Public | BindingFlags.Instance);
                if (pi != null) return pi.GetValue(p);
            }
            return null;
        }

        static string S(object? v) => (v?.ToString() ?? "").Trim();

        Code3 = S(Get("Code3", "Code", "CodePatient"));
        Nom = S(Get("Nom", "LastName"));
        Prenom = S(Get("Prenom", "FirstName"));
        Statut = S(Get("Statut", "Status"));
        Mutuelle = S(Get("Mutuelle", "MutualName", "MutuelleNom"));
        Niss = S(Get("Niss", "NISS"));
        Telephone = S(Get("Telephone", "Phone", "Gsm"));
        Email = S(Get("Email", "Mail"));
        Rue = S(Get("Rue"));
        Numero = S(Get("Numero"));
        Adresse = S(Get("Adresse", "Address", "Address1"));
        CodePostal = S(Get("CodePostal", "CP", "Zip"));
        Ville = S(Get("Ville", "City"));
        Pays = S(Get("Pays", "Country"));
        Referend = S(Get("Referend"));

        // Note: un Nullable<DateTime> "boxé" arrive soit comme DateTime (si HasValue), soit null.
        var dn = Get("DateNaissance", "BirthDate", "Naissance");
        DateNaissance = dn is DateTime dt ? dt : (DateTime?)null;

        PrescriberFirstName = S(Get("PrescriberFirstName", "PrenomMedPresc", "prenom_med_presc"));
        PrescriberLastName = S(Get("PrescriberLastName", "NomMedPresc", "nom_med_presc"));
        PrescriberCode = S(Get("PrescriberCode", "CodeMedecin", "code_medecin"));
        DatePrescription = S(Get("DatePrescription", "date_prescription"));
        DateAccord = S(Get("DateAccord", "date_accord"));
        PeriodeAccord = S(Get("PeriodeAccord", "periode_accord"));
        Nomenclature = S(Get("Nomenclature", "nomenclature"));
        Commentaire = S(Get("Commentaire", "commentaire"));
    }

    private void Save()
    {
        // validations minimales
        var code = (Code3 ?? "").Trim().ToUpperInvariant();
        if (code.Length != 3)
        {
            MessageBox.Show("Le code patient doit contenir exactement 3 lettres.", "PARAFacto", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Interdiction de modifier le code 3 lettres après création
        if (!IsNew && !string.IsNullOrWhiteSpace(_originalCode3) &&
            !string.Equals(code, _originalCode3.Trim().ToUpperInvariant(), StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("Le code 3 lettres ne peut pas être modifié pour un patient existant.", "PARAFacto", MessageBoxButton.OK, MessageBoxImage.Warning);
            Code3 = _originalCode3; // on restaure l'ancien
            return;
        }

        var nom = (Nom ?? "").Trim();
        var prenom = (Prenom ?? "").Trim();
        if (string.IsNullOrWhiteSpace(nom) || string.IsNullOrWhiteSpace(prenom))
        {
            MessageBox.Show("Nom et prénom sont obligatoires.", "PARAFacto", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            if (IsNew)
            {
                var exists = _repo.Search(code).Any(p => string.Equals((p.Code3 ?? "").Trim(), code, StringComparison.OrdinalIgnoreCase));
                if (exists)
                {
                    MessageBox.Show($"Le code '{code}' est déjà utilisé. Choisis un autre code 3 lettres.", "PARAFacto", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            var p = new Patient
            {
                Id = IsNew ? 0 : EditingId,
                Code3 = code,
                LastName = nom,
                FirstName = prenom,

                Statut = string.IsNullOrWhiteSpace(Statut) ? "NON BIM" : Statut.Trim(),
                MutualName = string.IsNullOrWhiteSpace(Mutuelle) ? null : Mutuelle.Trim(),
                Niss = string.IsNullOrWhiteSpace(Niss) ? null : Niss.Trim(),

                Rue = string.IsNullOrWhiteSpace(Rue) ? null : Rue.Trim(),
                Numero = string.IsNullOrWhiteSpace(Numero) ? null : Numero.Trim(),
                Address1 = !string.IsNullOrWhiteSpace(Adresse)
                    ? Adresse.Trim()
                    : (string.IsNullOrWhiteSpace(Rue) && string.IsNullOrWhiteSpace(Numero)
                        ? null
                        : ((Rue ?? "").Trim() + " " + (Numero ?? "").Trim()).Trim()),

                Zip = string.IsNullOrWhiteSpace(CodePostal) ? null : CodePostal.Trim(),
                City = string.IsNullOrWhiteSpace(Ville) ? null : Ville.Trim(),
                Country = string.IsNullOrWhiteSpace(Pays) ? null : Pays.Trim(),

                Phone = string.IsNullOrWhiteSpace(Telephone) ? null : Telephone.Trim(),
                Email = string.IsNullOrWhiteSpace(Email) ? null : Email.Trim(),
                Referend = string.IsNullOrWhiteSpace(Referend) ? null : Referend.Trim(),

                PrescriberLastName = string.IsNullOrWhiteSpace(PrescriberLastName) ? null : PrescriberLastName.Trim(),
                PrescriberFirstName = string.IsNullOrWhiteSpace(PrescriberFirstName) ? null : PrescriberFirstName.Trim(),
                PrescriberCode = string.IsNullOrWhiteSpace(PrescriberCode) ? null : PrescriberCode.Trim(),
                DatePrescription = string.IsNullOrWhiteSpace(DatePrescription) ? null : DatePrescription.Trim(),
                DateAccord = string.IsNullOrWhiteSpace(DateAccord) ? null : DateAccord.Trim(),
                PeriodeAccord = string.IsNullOrWhiteSpace(PeriodeAccord) ? null : PeriodeAccord.Trim(),
                Nomenclature = string.IsNullOrWhiteSpace(Nomenclature) ? null : Nomenclature.Trim(),
                Commentaire = string.IsNullOrWhiteSpace(Commentaire) ? null : Commentaire.Trim(),
            };

            var targetId = EditingId;
            var targetCode = p.Code3 ?? "";
            var targetLast = p.LastName ?? "";
            var targetFirst = p.FirstName ?? "";

            _repo.Upsert(p);

            Reload();

            if (targetId > 0)
            {
                Selected = Items.FirstOrDefault(x => x.Id == targetId)
                           ?? Items.FirstOrDefault(x =>
                               string.Equals((x.Code3 ?? "").Trim(), targetCode, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                Selected = Items.FirstOrDefault(x =>
                               string.Equals((x.Code3 ?? "").Trim(), targetCode, StringComparison.OrdinalIgnoreCase) &&
                               string.Equals((x.LastName ?? "").Trim(), targetLast, StringComparison.OrdinalIgnoreCase) &&
                               string.Equals((x.FirstName ?? "").Trim(), targetFirst, StringComparison.OrdinalIgnoreCase))
                           ?? Items.FirstOrDefault(x =>
                               string.Equals((x.Code3 ?? "").Trim(), targetCode, StringComparison.OrdinalIgnoreCase));
            }

            IsEditing = false;
            IsNew = false;
            EditingId = 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "PARAFacto", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static object? GetProp(object o, string name)
        => o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(o);
}
