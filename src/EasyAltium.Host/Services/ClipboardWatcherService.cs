using System.Runtime.InteropServices;
using System.Windows.Interop;
using EasyAltium.Core.Models;

namespace EasyAltium.Host.Services;

/// <summary>
/// Win32 AddClipboardFormatListener API'sini kullanarak Windows panosunu (Clipboard)
/// gerçek zamanlı dinleyen ve ^C[1-9]\d{2,}$ formatındaki LCSC parça kodlarını yakalayan servis.
/// </summary>
public sealed partial class ClipboardWatcherService : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AddClipboardFormatListener(IntPtr hwnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RemoveClipboardFormatListener(IntPtr hwnd);

    private HwndSource? _hwndSource;
    private IntPtr _windowHandle = IntPtr.Zero;
    private bool _isListening;
    private string _lastDetectedCode = string.Empty;
    private DateTime _lastDetectionTimeUtc = DateTime.MinValue;

    /// <summary>
    /// Panoda geçerli bir LCSC parça kodu (Örn: C2040, C14663) yakalandığında tetiklenir.
    /// </summary>
    public event EventHandler<string>? LcscPartCodeDetected;

    public bool IsEnabled { get; set; } = true;

    public void AttachToWindow(System.Windows.Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var helper = new WindowInteropHelper(window);
        _windowHandle = helper.EnsureHandle();
        _hwndSource = HwndSource.FromHwnd(_windowHandle);
        _hwndSource?.AddHook(WndProc);

        if (AddClipboardFormatListener(_windowHandle))
            _isListening = true;
    }

    public void ResetLastDetectedCode()
    {
        _lastDetectedCode = string.Empty;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE && IsEnabled)
        {
            TryProcessClipboardText();
        }

        return IntPtr.Zero;
    }

    private void TryProcessClipboardText()
    {
        try
        {
            if (!System.Windows.Clipboard.ContainsText())
                return;

            string rawText = System.Windows.Clipboard.GetText();
            string? lcscId = LcscPartMetadata.TryExtractLcscId(rawText);
            if (string.IsNullOrWhiteSpace(lcscId))
                return;

            string normalized = lcscId.Trim().ToUpperInvariant();
            DateTime now = DateTime.UtcNow;

            // Aynı parça kodu 3 saniye içinde mükerrer tetiklenmesin
            if (string.Equals(normalized, _lastDetectedCode, StringComparison.OrdinalIgnoreCase) &&
                (now - _lastDetectionTimeUtc).TotalSeconds < 3.0)
            {
                return;
            }

            _lastDetectedCode = normalized;
            _lastDetectionTimeUtc = now;

            LcscPartCodeDetected?.Invoke(this, normalized);
        }
        catch
        {
            // Başka bir uygulama panoyu kilitlediyse sessizce atla
        }
    }

    public void Dispose()
    {
        if (_isListening && _windowHandle != IntPtr.Zero)
        {
            try
            {
                RemoveClipboardFormatListener(_windowHandle);
            }
            catch
            {
            }
            _isListening = false;
        }

        if (_hwndSource is not null)
        {
            _hwndSource.RemoveHook(WndProc);
            _hwndSource = null;
        }
    }
}
