using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace EasyAltium.Host.Services;

/// <summary>
/// Sistem tepsisinde (System Tray) çalışan bildirim ikonu ve sağ tık menüsü servisi.
/// </summary>
public sealed partial class TrayIconService : IDisposable
{
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr hIcon);

    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _generatedIcon;
    private readonly ToolStripMenuItem _clipboardMenuItem;

    public event EventHandler? ShowMiniWindowRequested;
    public event EventHandler? OpenSettingsRequested;
    public event EventHandler<bool>? ClipboardListeningToggled;
    public event EventHandler? ExitRequested;

    public TrayIconService(bool initialClipboardState)
    {
        _generatedIcon = CreateBrandIcon();

        var contextMenu = new ContextMenuStrip();

        var showWindowItem = new ToolStripMenuItem("Mini Arama Penceresini Göster / Gizle")
        {
            Font = new Font( SystemFonts.MenuFont ?? SystemFonts.DefaultFont, FontStyle.Bold)
        };
        showWindowItem.Click += (_, _) => ShowMiniWindowRequested?.Invoke(this, EventArgs.Empty);

        _clipboardMenuItem = new ToolStripMenuItem("Pano Dinleyici Aktif (LCSC Yakala)")
        {
            Checked = initialClipboardState,
            CheckOnClick = true
        };
        _clipboardMenuItem.CheckedChanged += (_, _) =>
            ClipboardListeningToggled?.Invoke(this, _clipboardMenuItem.Checked);

        var settingsItem = new ToolStripMenuItem("Yapılandırma ve Ayarlar...");
        settingsItem.Click += (_, _) => OpenSettingsRequested?.Invoke(this, EventArgs.Empty);

        var exitItem = new ToolStripMenuItem("EasyAltium'dan Çık");
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        contextMenu.Items.Add(showWindowItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(_clipboardMenuItem);
        contextMenu.Items.Add(settingsItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Icon = _generatedIcon,
            Text = "EasyAltium - LCSC & JLCPCB Library Loader (AD 26.10.1)",
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

    private static Icon CreateBrandIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var bgBrush = new SolidBrush(Color.FromArgb(24, 26, 31));
            using var borderPen = new Pen(Color.FromArgb(245, 158, 11), 2.5f);
            g.FillEllipse(bgBrush, 2, 2, 28, 28);
            g.DrawEllipse(borderPen, 2, 2, 28, 28);

            using var font = new Font("Segoe UI", 10.5f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var textBrush = new SolidBrush(Color.FromArgb(245, 158, 11));
            var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            g.DrawString("EA", font, textBrush, new RectangleF(0, 1, 32, 30), format);
        }

        IntPtr hIcon = bmp.GetHicon();
        try
        {
            using Icon tempIcon = Icon.FromHandle(hIcon);
            return (Icon)tempIcon.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _generatedIcon.Dispose();
    }
}
