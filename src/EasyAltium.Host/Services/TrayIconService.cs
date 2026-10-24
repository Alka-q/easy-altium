using System.Drawing;
using System.Windows.Forms;
using EasyAltium.Core.Configuration;

namespace EasyAltium.Host.Services;

/// <summary>
/// Sistem tepsisinde (System Tray) çalışan bildirim ikonu ve sağ tık menüsü servisi.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _generatedIcon;
    private readonly ToolStripMenuItem _clipboardMenuItem;

    public event EventHandler? ShowMiniWindowRequested;
    public event EventHandler? InstallToolbarRequested;
    public event EventHandler? OpenSettingsRequested;
    public event EventHandler<bool>? ClipboardListeningToggled;
    public event EventHandler? ExitRequested;

    public TrayIconService(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        _generatedIcon = BrandIconProcessor.CreateApplicationTrayIcon(config);

        var contextMenu = new ContextMenuStrip();

        var showWindowItem = new ToolStripMenuItem("Mini Arama Penceresini Göster / Gizle")
        {
            Font = new Font(SystemFonts.MenuFont ?? SystemFonts.DefaultFont, FontStyle.Bold)
        };
        showWindowItem.Click += (_, _) => ShowMiniWindowRequested?.Invoke(this, EventArgs.Empty);

        _clipboardMenuItem = new ToolStripMenuItem("Pano Dinleyici Aktif (LCSC Yakala)")
        {
            Checked = config.AutoWatchClipboard,
            CheckOnClick = true
        };
        _clipboardMenuItem.CheckedChanged += (_, _) =>
            ClipboardListeningToggled?.Invoke(this, _clipboardMenuItem.Checked);

        var installToolbarItem = new ToolStripMenuItem("Altium Üst Barına (Toolbar) İkonu Kur / Güncelle");
        installToolbarItem.Click += (_, _) => InstallToolbarRequested?.Invoke(this, EventArgs.Empty);

        var settingsItem = new ToolStripMenuItem("Yapılandırma ve Ayarlar...");
        settingsItem.Click += (_, _) => OpenSettingsRequested?.Invoke(this, EventArgs.Empty);

        var exitItem = new ToolStripMenuItem("EasyAltium'dan Çık");
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        contextMenu.Items.Add(showWindowItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(_clipboardMenuItem);
        contextMenu.Items.Add(installToolbarItem);
        contextMenu.Items.Add(settingsItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Icon = _generatedIcon,
            Text = $"EasyAltium - LCSC & JLCPCB Loader ({config.DetectedAltiumVersionLabel})",
            ContextMenuStrip = contextMenu,
            Visible = true
        };

        _notifyIcon.DoubleClick += (_, _) => ShowMiniWindowRequested?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateClipboardCheckedState(bool isListening)
    {
        if (_clipboardMenuItem.Checked != isListening)
            _clipboardMenuItem.Checked = isListening;
    }

    public void ShowBalloonNotification(string title, string message, ToolTipIcon icon = ToolTipIcon.Info)
    {
        _notifyIcon.ShowBalloonTip(3000, title, message, icon);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _generatedIcon.Dispose();
    }
}
