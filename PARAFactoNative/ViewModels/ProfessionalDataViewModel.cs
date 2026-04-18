using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PARAFactoNative.Models;
using PARAFactoNative.Services;

namespace PARAFactoNative.ViewModels;

public sealed class ProfessionalDataViewModel : NotifyBase
{
    private string _consoleHeaderTitle = "";
    private string _invoiceProviderName = "";
    private string _addressLine1 = "";
    private string _addressLine2 = "";
    private string _inami = "";
    private string _vatNumber = "";
    private string _phone = "";
    private string _email = "";
    private string _iban = "";
    private string _ibanMutuelle = "";
    private string _ibanCreditNote = "";
    private string _bic = "";
    private string _mutualRecapProviderName = "";
    private string _mutualRecapAddressLine = "";
    private string _reminderSenderDisplayName = "";
    private string? _logoPreviewPath;
    private string? _pendingLogoPickPath;
    private ImageSource? _logoPreviewSource;

    public string ConsoleHeaderTitle { get => _consoleHeaderTitle; set => Set(ref _consoleHeaderTitle, value ?? ""); }
    public string InvoiceProviderName { get => _invoiceProviderName; set => Set(ref _invoiceProviderName, value ?? ""); }
    public string AddressLine1 { get => _addressLine1; set => Set(ref _addressLine1, value ?? ""); }
    public string AddressLine2 { get => _addressLine2; set => Set(ref _addressLine2, value ?? ""); }
    public string Inami { get => _inami; set => Set(ref _inami, value ?? ""); }
    public string VatNumber { get => _vatNumber; set => Set(ref _vatNumber, value ?? ""); }
    public string Phone { get => _phone; set => Set(ref _phone, value ?? ""); }
    public string Email { get => _email; set => Set(ref _email, value ?? ""); }
    public string Iban { get => _iban; set => Set(ref _iban, value ?? ""); }
    public string IbanMutuelle { get => _ibanMutuelle; set => Set(ref _ibanMutuelle, value ?? ""); }
    public string IbanCreditNote { get => _ibanCreditNote; set => Set(ref _ibanCreditNote, value ?? ""); }
    public string Bic { get => _bic; set => Set(ref _bic, value ?? ""); }
    public string MutualRecapProviderName { get => _mutualRecapProviderName; set => Set(ref _mutualRecapProviderName, value ?? ""); }
    public string MutualRecapAddressLine { get => _mutualRecapAddressLine; set => Set(ref _mutualRecapAddressLine, value ?? ""); }
    public string ReminderSenderDisplayName { get => _reminderSenderDisplayName; set => Set(ref _reminderSenderDisplayName, value ?? ""); }

    public string? LogoPreviewPath
    {
        get => _logoPreviewPath;
        private set
        {
            if (!Set(ref _logoPreviewPath, value)) return;
            Raise(nameof(HasLogoPreview));
            RebuildLogoPreviewSource();
        }
    }

    public ImageSource? LogoPreviewSource
    {
        get => _logoPreviewSource;
        private set => Set(ref _logoPreviewSource, value);
    }

    public bool HasLogoPreview => LogoPreviewSource is not null;

    public RelayCommand ReloadCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand PickLogoCommand { get; }
    public RelayCommand ClearLogoCommand { get; }

    public ProfessionalDataViewModel()
    {
        ReloadCommand = new RelayCommand(LoadFromStore);
        SaveCommand = new RelayCommand(Save);
        PickLogoCommand = new RelayCommand(PickLogo);
        ClearLogoCommand = new RelayCommand(ClearLogo, () => HasLogoPreview || !string.IsNullOrWhiteSpace(_pendingLogoPickPath));
        LoadFromStore();
    }

    private void RebuildLogoPreviewSource()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_logoPreviewPath) || !File.Exists(_logoPreviewPath))
            {
                LogoPreviewSource = null;
                return;
            }

            var bi = new BitmapImage();
            bi.BeginInit();
            bi.UriSource = new Uri(Path.GetFullPath(_logoPreviewPath), UriKind.Absolute);
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.EndInit();
            bi.Freeze();
            LogoPreviewSource = bi;
        }
        catch
        {
            LogoPreviewSource = null;
        }
    }

    private void LoadFromStore()
    {
        var p = ProfessionalProfileStore.Load();
        ConsoleHeaderTitle = p.ConsoleHeaderTitle;
        InvoiceProviderName = p.InvoiceProviderName;
        AddressLine1 = p.AddressLine1;
        AddressLine2 = p.AddressLine2;
        Inami = p.Inami;
        VatNumber = p.VatNumber;
        Phone = p.Phone;
        Email = p.Email;
        Iban = p.Iban;
        IbanMutuelle = p.IbanMutuelle;
        IbanCreditNote = p.IbanCreditNote;
        Bic = p.Bic;
        MutualRecapProviderName = p.MutualRecapProviderName;
        MutualRecapAddressLine = p.MutualRecapAddressLine ?? "";
        ReminderSenderDisplayName = p.ReminderSenderDisplayName;
        _pendingLogoPickPath = null;
        LogoPreviewPath = ProfessionalProfileStore.ResolveLogoPath();
        ClearLogoCommand.RaiseCanExecuteChanged();
    }

    private static string? T(string key) => Application.Current?.TryFindResource(key) as string;

    private static string AppCaption =>
        T("App.WindowTitle") ?? "PARAFacto";

    private void PickLogo()
    {
        var dlg = new OpenFileDialog
        {
            Filter = T("Professional.PickLogo.Filter")
                     ?? "Images|*.jpg;*.jpeg;*.png;*.bmp;*.gif|*.*",
            Title = T("Professional.PickLogo.Title") ?? "Logo"
        };
        if (dlg.ShowDialog() != true) return;
        _pendingLogoPickPath = dlg.FileName;
        LogoPreviewPath = dlg.FileName;
        ClearLogoCommand.RaiseCanExecuteChanged();
    }

    private void ClearLogo()
    {
        _pendingLogoPickPath = null;
        ProfessionalProfileStore.ClearLogoFile();
        LogoPreviewPath = ProfessionalProfileStore.ResolveLogoPath();
        ClearLogoCommand.RaiseCanExecuteChanged();
    }

    private void Save()
    {
        if (string.IsNullOrWhiteSpace(ConsoleHeaderTitle) || string.IsNullOrWhiteSpace(InvoiceProviderName))
        {
            MessageBox.Show(
                T("Professional.Validation.RequiredFields")
                ?? "Le titre console et le nom sur les factures sont obligatoires.",
                AppCaption,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            var p = new ProfessionalProfile
            {
                ConsoleHeaderTitle = ConsoleHeaderTitle.Trim(),
                InvoiceProviderName = InvoiceProviderName.Trim(),
                AddressLine1 = AddressLine1.Trim(),
                AddressLine2 = AddressLine2.Trim(),
                Inami = Inami.Trim(),
                VatNumber = VatNumber.Trim(),
                Phone = Phone.Trim(),
                Email = Email.Trim(),
                Iban = Iban.Trim(),
                IbanMutuelle = string.IsNullOrWhiteSpace(IbanMutuelle) ? Iban.Trim() : IbanMutuelle.Trim(),
                IbanCreditNote = string.IsNullOrWhiteSpace(IbanCreditNote) ? Iban.Trim() : IbanCreditNote.Trim(),
                Bic = Bic.Trim(),
                MutualRecapProviderName = MutualRecapProviderName.Trim(),
                MutualRecapAddressLine = string.IsNullOrWhiteSpace(MutualRecapAddressLine) ? null : MutualRecapAddressLine.Trim(),
                ReminderSenderDisplayName = ReminderSenderDisplayName.Trim(),
                LogoRelativeFileName = ProfessionalProfileStore.Load().LogoRelativeFileName,
            };

            ProfessionalProfileStore.Save(p, _pendingLogoPickPath);
            _pendingLogoPickPath = null;
            LogoPreviewPath = ProfessionalProfileStore.ResolveLogoPath();
            ClearLogoCommand.RaiseCanExecuteChanged();
            MessageBox.Show(
                T("Professional.Save.Success")
                ?? "Données professionnelles enregistrées. Les prochains PDF utiliseront ces informations.",
                AppCaption,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                T("Professional.Save.ErrorTitle") ?? "Enregistrement — erreur",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
