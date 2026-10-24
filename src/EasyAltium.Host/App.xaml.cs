using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using EasyAltium.Core.Bridge;
using EasyAltium.Core.Configuration;
using EasyAltium.Core.Models;
using EasyAltium.Host.Services;
using EasyAltium.Host.ViewModels;
using EasyAltium.Host.Views;

namespace EasyAltium.Host;

public partial class App : System.Windows.Application
{
    public const string SingleInstanceEventName = @"Local\EasyAltium_ShowMiniWindow_Event";
    private const int SW_RESTORE = 9;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private EventWaitHandle? _showWindowEvent;
    private CancellationTokenSource? _ipcListenCts;
    private AppConfig? _config;
    private MainViewModel? _mainViewModel;
    private MiniSearchWindow? _miniWindow;
    private SettingsWindow? _settingsWindow;
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

        _mainViewModel = new MainViewModel(_config);
        _miniWindow = new MiniSearchWindow(_mainViewModel);
        MainWindow = _miniWindow;

        _settingsWindow = new SettingsWindow(_config);
        _settingsWindow.SettingsSaved += (_, _) =>
        {
            _mainViewModel.SyncFromConfig();
            _mainViewModel.AppendLog("Yapılandırma ayarları kaydedildi.");
        };

        _previewPopup = new PartPreviewPopup();
        _clipboardWatcher = new ClipboardWatcherService
        {
            IsEnabled = _config.AutoWatchClipboard
        };
        _trayIcon = new TrayIconService(_config);

        _miniWindow.SourceInitialized += (_, _) =>
        {
            _clipboardWatcher.AttachToWindow(_miniWindow);
            // Ayarlar penceresinin Win32 HWND'sini başlangıçta hazırla (tıklandığında <1ms içinde anında açılsın)
            _ = new WindowInteropHelper(_settingsWindow).EnsureHandle();
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
        StartDxpRcsSelfHealingMonitor();
        _miniWindow.Show();

        // Arka planda ikon varlıklarını ve DXP.RCS kaydını doğrula (UI açılışını hiç bekletmeden)
        AppConfig cfg = _config;
        _ = Task.Run(() =>
        {
            try
            {
                BrandIconProcessor.EnsureToolbarIconAssets(cfg);
                var toolbarInstaller = new AltiumToolbarInstaller(cfg);
                toolbarInstaller.EnsureToolbarInstalledIfNeeded();
            }
            catch
            {
            }
        });
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

    /// <summary>
    /// Altium Designer (X2.EXE) kapatılırken belleğindeki eski DXP.RCS içeriğini diske yazarak
    /// yeni eklenen toolbar butonlarını ezdiğinde bunu 1.5 saniye içinde yakalayıp otomatik onarır.
    /// </summary>
    private void StartDxpRcsSelfHealingMonitor()
    {
        if (_config is null || _ipcListenCts is null)
            return;

        CancellationToken token = _ipcListenCts.Token;
        var installer = new AltiumToolbarInstaller(_config);

        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1500));
            while (!token.IsCancellationRequested && await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                try
                {
                    if (installer.EnsureToolbarInstalledIfNeeded())
                    {
                        _ = Dispatcher.BeginInvoke(() =>
                        {
                            _mainViewModel?.AppendLog("Altium kapanışı sonrası DXP.RCS toolbar kaydı otomatik korundu.");
                        });
                    }
                }
                catch
                {
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

        BringWindowToFront(_miniWindow);
    }

    private void ShowSettingsDialog()
    {
        if (_config is null || _mainViewModel is null)
            return;

        _settingsWindow ??= new SettingsWindow(_config);
        _settingsWindow.LoadFromConfig();

        if (_miniWindow is { IsVisible: true } && _miniWindow.WindowState != WindowState.Minimized)
        {
            _settingsWindow.Left = _miniWindow.Left + (_miniWindow.Width - _settingsWindow.Width) / 2;
            _settingsWindow.Top = _miniWindow.Top + (_miniWindow.Height - _settingsWindow.Height) / 2;
        }

        if (!_settingsWindow.IsVisible)
            _settingsWindow.Show();

        if (_settingsWindow.WindowState == WindowState.Minimized)
            _settingsWindow.WindowState = WindowState.Normal;

        BringWindowToFront(_settingsWindow);
    }

    private static void BringWindowToFront(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero)
        {
            ShowWindow(hwnd, SW_RESTORE);
            SetForegroundWindow(hwnd);
        }

        window.Activate();
        window.Focus();
    }

    private void ShutdownApplication()
    {
        _ipcListenCts?.Cancel();
        _clipboardWatcher?.Dispose();
        _trayIcon?.Dispose();

        if (_previewPopup is not null)
            _previewPopup.Close();

        if (_settingsWindow is not null)
        {
            _settingsWindow.AllowFullClose = true;
            _settingsWindow.Close();
        }

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
