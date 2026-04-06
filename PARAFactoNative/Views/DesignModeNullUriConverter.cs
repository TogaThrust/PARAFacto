using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;

namespace PARAFactoNative.Views;

/// <summary>
/// En mode concepteur, retourne null pour éviter que le concepteur WPF charge une image
/// (erreur "Informations sur ce format de pixel introuvables" avec certains JPEG/PNG).
/// À l'exécution, convertit le chemin fichier en Uri pour BitmapImage.UriSource.
/// </summary>
public sealed class DesignModeNullUriConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var inDesignMode = parameter is DependencyObject dep && DesignerProperties.GetIsInDesignMode(dep);
        if (!inDesignMode && Application.Current?.MainWindow != null)
            inDesignMode = DesignerProperties.GetIsInDesignMode(Application.Current.MainWindow);
        if (inDesignMode)
            return null;

        var path = value?.ToString()?.Trim();
        if (string.IsNullOrEmpty(path)) return null;
        if (!File.Exists(path)) return null;

        try
        {
            return new Uri(path, UriKind.Absolute);
        }
        catch
        {
            return null;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
