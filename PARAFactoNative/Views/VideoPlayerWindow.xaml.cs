using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace PARAFactoNative.Views;

public partial class VideoPlayerWindow : Window
{
    private readonly string _videoPath;

    public VideoPlayerWindow(string videoPath)
    {
        InitializeComponent();
        _videoPath = videoPath;
        VideoTitleText.Text = Path.GetFileNameWithoutExtension(_videoPath);
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        VideoPlayer.Source = new Uri(_videoPath, UriKind.Absolute);
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(VideoPlayer.Play));
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        VideoPlayer.Stop();
        VideoPlayer.Source = null;
    }

    private void VideoPlayer_OnMediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        MessageBox.Show(
            $"Impossible de lire la vidéo.\n{e.ErrorException?.Message ?? "Erreur inconnue"}",
            "PARAFacto",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void ResumeButton_OnClick(object sender, RoutedEventArgs e)
    {
        VideoPlayer.Play();
    }

    private void PauseButton_OnClick(object sender, RoutedEventArgs e)
    {
        VideoPlayer.Pause();
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
