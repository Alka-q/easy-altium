using System.Runtime.InteropServices;
using System.Windows.Interop;
using EasyAltium.Core.Models;

namespace EasyAltium.Host.Services;

/// <summary>
/// Monitors the Windows clipboard in real time via the Win32 AddClipboardFormatListener API
/// and detects LCSC part codes matching ^C[1-9]\d{2,}$.
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
    /// Raised when a valid LCSC part code (e.g., C2040, C14663) is detected on the clipboard.
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

            // Avoid duplicate triggers for the same part code within 3 seconds
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
            // Silently ignore if another process temporarily holds the clipboard lock
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
