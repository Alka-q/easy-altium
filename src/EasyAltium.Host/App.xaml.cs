using System.Threading;
using System.Windows;
using EasyAltium.Core.Bridge;
using EasyAltium.Core.Configuration;
using EasyAltium.Core.Models;
using EasyAltium.Host.Services;
using EasyAltium.Host.ViewModels;
using EasyAltium.Host.Views;

namespace EasyAltium.Host;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceEventName = @"Local\EasyAltium_ShowMiniWindow_Event";

    private EventWaitHandle? _showWindowEvent;
    private CancellationTokenSource? _ipcListenCts;
    private AppConfig? _config;
    private MainViewModel? _mainViewModel;
    private MiniSearchWindow? _miniWindow;
    private PartPreviewPopup? _previewPopup;
    private ClipboardWatcherService? _clipboardWatcher;
    private TrayIconService? _trayIcon;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Tekil örnek (Single-Instance) kontrolü: Altium Toolbar'dan "--show" ile çağrıldığında
        // mevcut çalışan örneğin penceresini öne getirir ve ikinci süreci kapatır.
        bool createdNew;
        _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, SingleInstanceEventName, out createdNew);
        if (!createdNew)
        {
            _showWindowEvent.Set();
            Shutdown();
            return;
        }

        base.OnStartup(e);

        _config = AppConfig.LoadOrCreate();

        // doc/EasyAltium-componentimge.jfif simgesinden şeffaf PNG/BMP üret ve Altium Toolbar'a (DXP.RCS) kur
        try
        {
            BrandIconProcessor.EnsureToolbarIconAssets(_config);
            var toolbarInstaller = new AltiumToolbarInstaller(_config);
            toolbarInstaller.InstallToolbarAndMenus();
        }
        catch
        {
        }

        _mainViewModel = new MainViewModel(_config);
        _miniWindow = new MiniSearchWindow(_mainViewModel);
        MainWindow = _miniWindow;

        _previewPopup = new PartPreviewPopup();
        _clipboardWatcher = new ClipboardWatcherService
        {
            IsEnabled = _config.AutoWatchClipboard
        };
        _trayIcon = new TrayIconService(_config);

        _miniWindow.SourceInitialized += (_, _) =>
        {
            _clipboardWatcher.AttachToWindow(_miniWindow);
            _mainViewModel.AppendLog("Win32 Pano Dinleyici ve Altium Toolbar ikonu hazır.");
        };

        _mainViewModel.ClipboardListeningChanged += (_, enabled) =>
        {
            if (_clipboardWatcher is not null)
                _clipboardWatcher.IsEnabled = enabled;

            _trayIcon?.UpdateClipboardCheckedState(enabled);
        };

        _mainViewModel.OpenSettingsRequested += (_, _) => ShowSettingsDialog();

        _trayIcon.ShowMiniWindowRequested += (_, _) => ShowAndActivateMiniWindow();
        _trayIcon.InstallToolbarRequested += (_, _) => InstallAltiumToolbarWithFeedback();
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

        StartSingleInstanceListener();
        _miniWindow.Show();
    }

    private void StartSingleInstanceListener()
    {
        if (_showWindowEvent is null)
            return;

        _ipcListenCts = new CancellationTokenSource();
        CancellationToken token = _ipcListenCts.Token;

        _ = Task.Run(() =>
        {
            while (!token.IsCancellationRequested)
            {
                int signaled = WaitHandle.WaitAny([_showWindowEvent, token.WaitHandle]);
                if (signaled == 0 && !token.IsCancellationRequested)
                {
                    Dispatcher.BeginInvoke(ShowAndActivateMiniWindow);
                }
            }
        }, token);
    }

    private void InstallAltiumToolbarWithFeedback()
    {
        if (_config is null || _mainViewModel is null)
            return;

        try
        {
            BrandIconProcessor.EnsureToolbarIconAssets(_config);
            var installer = new AltiumToolbarInstaller(_config);
            ToolbarInstallResult res = installer.InstallToolbarAndMenus();
            _mainViewModel.AppendLog(res.Message);
            _trayIcon?.ShowBalloonNotification("EasyAltium Toolbar", res.Message);
        }
        catch (Exception ex)
        {
            _mainViewModel.AppendLog($"Toolbar kurulum hatası: {ex.Message}");
        }
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

    private void ShowAndActivateMiniWindow()
    {
        if (_miniWindow is null)
            return;

        if (!_miniWindow.IsVisible)
            _miniWindow.Show();

        if (_miniWindow.WindowState == WindowState.Minimized)
            _miniWindow.WindowState = WindowState.Normal;

        _miniWindow.Activate();
        _miniWindow.Focus();
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
        _ipcListenCts?.Cancel();
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
        _ipcListenCts?.Cancel();
        _clipboardWatcher?.Dispose();
        _trayIcon?.Dispose();
        _showWindowEvent?.Dispose();
        base.OnExit(e);
    }
}
