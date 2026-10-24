using System.Windows;
using EasyAltium.Core.Configuration;
using EasyAltium.Core.Models;
using EasyAltium.Host.Services;
using EasyAltium.Host.ViewModels;
using EasyAltium.Host.Views;

namespace EasyAltium.Host;

public partial class App : System.Windows.Application
{
    private AppConfig? _config;
    private MainViewModel? _mainViewModel;
    private MiniSearchWindow? _miniWindow;
    private PartPreviewPopup? _previewPopup;
    private ClipboardWatcherService? _clipboardWatcher;
    private TrayIconService? _trayIcon;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _config = AppConfig.LoadOrCreate();
        _mainViewModel = new MainViewModel(_config);

        _miniWindow = new MiniSearchWindow(_mainViewModel);
        _previewPopup = new PartPreviewPopup();
        _clipboardWatcher = new ClipboardWatcherService
        {
            IsEnabled = _config.AutoWatchClipboard
        };
        _trayIcon = new TrayIconService(_config.AutoWatchClipboard);

        // Olay bağlantıları
        _miniWindow.SourceInitialized += (_, _) =>
        {
            _clipboardWatcher.AttachToWindow(_miniWindow);
            _mainViewModel.AppendLog("Win32 Pano Dinleyici (AddClipboardFormatListener) aktif.");
        };

        _mainViewModel.ClipboardListeningChanged += (_, enabled) =>
        {
            if (_clipboardWatcher is not null)
                _clipboardWatcher.IsEnabled = enabled;

            _trayIcon?.UpdateClipboardCheckedState(enabled);
        };

        _mainViewModel.OpenSettingsRequested += (_, _) => ShowSettingsDialog();

        _trayIcon.ShowMiniWindowRequested += (_, _) => ToggleMiniWindow();
        _trayIcon.OpenSettingsRequested += (_, _) => ShowSettingsDialog();
        _trayIcon.ClipboardListeningToggled += (_, enabled) =>
        {
            if (_mainViewModel is not null)
                _mainViewModel.IsClipboardListening = enabled;
        };
        _trayIcon.ExitRequested += (_, _) => ShutdownApplication();

        _clipboardWatcher.LcscPartCodeDetected += async (_, lcscId) =>
        {
            await OnClipboardPartCodeDetectedAsync(lcscId);
        };

        _previewPopup.ActionConfirmed += async (_, action) =>
        {
            if (_mainViewModel is null)
                return;

            var result = await _mainViewModel.ExecuteImportAsync(action);
            if (result is not null)
            {
                _trayIcon?.ShowBalloonNotification(
                    "EasyAltium — Aktarım Tamamlandı",
                    result.SummaryMessage);
            }
        };

        _miniWindow.Show();
    }

    private async Task OnClipboardPartCodeDetectedAsync(string lcscId)
    {
        if (_mainViewModel is null || _previewPopup is null)
            return;

        _mainViewModel.AppendLog($"Panodan LCSC kodu yakalandı: {lcscId}");
        LcscPartMetadata? metadata = await _mainViewModel.FetchPartPreviewAsync(lcscId);
        if (metadata is not null)
        {
            _previewPopup.BindPart(metadata);
            _previewPopup.Show();
            _previewPopup.Activate();
        }
    }

    private void ToggleMiniWindow()
    {
        if (_miniWindow is null)
            return;

        if (_miniWindow.IsVisible)
        {
            _miniWindow.Hide();
        }
        else
        {
            _miniWindow.Show();
            if (_miniWindow.WindowState == WindowState.Minimized)
                _miniWindow.WindowState = WindowState.Normal;
            _miniWindow.Activate();
        }
    }

    private void ShowSettingsDialog()
    {
        if (_config is null || _mainViewModel is null)
            return;

        var dialog = new SettingsWindow(_config);
        if (_miniWindow is { IsVisible: true })
            dialog.Owner = _miniWindow;

        if (dialog.ShowDialog() == true)
        {
            _mainViewModel.SyncFromConfig();
            _mainViewModel.AppendLog("Yapılandırma ayarları kaydedildi.");
        }
    }

    private void ShutdownApplication()
    {
        _clipboardWatcher?.Dispose();
        _trayIcon?.Dispose();

        if (_previewPopup is not null)
            _previewPopup.Close();

        if (_miniWindow is not null)
        {
            _miniWindow.AllowFullClose = true;
            _miniWindow.Close();
        }

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _clipboardWatcher?.Dispose();
        _trayIcon?.Dispose();
        base.OnExit(e);
    }
}
