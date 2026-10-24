using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EasyAltium.Core.Bridge;
using EasyAltium.Core.Configuration;

namespace EasyAltium.Host.Services;

/// <summary>
/// Removes the checkerboard background from doc/EasyAltium-componentimge.jfif and generates
/// pure-white transparent Altium Toolbar PNG/BMP/SVG assets as well as the application/Tray icon.
/// </summary>
public static partial class BrandIconProcessor
{
    private static ImageSource? s_cachedWindowIcon;
    private static Bitmap? s_cachedChipBitmap;
    private static readonly object s_syncLock = new();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr hIcon);

    public static void EnsureToolbarIconAssets(AppConfig config, bool forceRebuild = false)
    {
        ArgumentNullException.ThrowIfNull(config);

        var installer = new AltiumToolbarInstaller(config);
        string svgPath = Path.Combine(installer.ToolbarAssetsDirectory, "EasyAltium.svg");
        string bmpPath = Path.Combine(installer.ToolbarAssetsDirectory, "EasyAltium.bmp");

        if (!forceRebuild &&
            File.Exists(installer.ToolbarPngIconPath) &&
            File.Exists(installer.ToolbarHiDpiPngIconPath) &&
            File.Exists(svgPath) &&
            File.Exists(bmpPath))
        {
            return;
        }

        Directory.CreateDirectory(installer.ToolbarAssetsDirectory);

        using Bitmap transparentChip = GetOrLoadTransparentChipBitmap(installer.SourceJfifPath);

        using Bitmap icon16 = ResizeBitmapHighQuality(transparentChip, 16, 16);
        using Bitmap icon32 = ResizeBitmapHighQuality(transparentChip, 32, 32);

        // Altium standart buton boyutu 16x16 (EasyAltium.png) ve High-DPI 2x boyutu 32x32 (EasyAltium_x2.png)
        icon16.Save(installer.ToolbarPngIconPath, ImageFormat.Png);
        icon32.Save(installer.ToolbarHiDpiPngIconPath, ImageFormat.Png);

        icon16.Save(installer.ToolbarSmallPngIconPath, ImageFormat.Png);
        string smallHiDpiPath = Path.Combine(installer.ToolbarAssetsDirectory, "EasyAltium_16_x2.png");
        icon32.Save(smallHiDpiPath, ImageFormat.Png);

        string largePngPath = Path.Combine(installer.ToolbarAssetsDirectory, "EasyAltium_32.png");
        icon32.Save(largePngPath, ImageFormat.Png);

        icon16.Save(bmpPath, ImageFormat.Bmp);

        File.WriteAllText(svgPath, BuildCleanChipSvg());
    }

    private static string BuildCleanChipSvg() =>
        """
        <?xml version="1.0" encoding="utf-8"?>
        <svg version="1.1" id="EasyAltium_16" xmlns="http://www.w3.org/2000/svg" x="0px" y="0px" viewBox="0 0 16 16" style="enable-background:new 0 0 16 16;" xml:space="preserve">
        <style type="text/css">
            .st0{fill:#FFFFFF;}
        </style>
        <path class="st0" d="M12,1H4C3.4,1,3,1.4,3,2v1H1v1.4h2v1.9H1v1.4h2v1.9H1V11h2v1.9H1v1.4h2V14c0,0.6,0.4,1,1,1h8c0.6,0,1-0.4,1-1v-0.7h2v-1.4h-2V10h2V8.6h-2V6.7h2V5.3h-2V3.4h2V2h-2V2C13,1.4,12.6,1,12,1z M11.4,13.4H4.6V2.6h6.8V13.4z"/>
        <circle class="st0" cx="6.4" cy="4.6" r="1.1"/>
        </svg>
        """;

    public static Icon CreateApplicationTrayIcon(AppConfig config)
    {
        var installer = new AltiumToolbarInstaller(config);
        EnsureToolbarIconAssets(config);

        using Bitmap chipBmp = GetOrLoadTransparentChipBitmap(installer.SourceJfifPath);
        using Bitmap canvas = new(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(canvas))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(System.Drawing.Color.Transparent);

            using var bgBrush = new SolidBrush(System.Drawing.Color.FromArgb(230, 24, 27, 34));
            g.FillEllipse(bgBrush, 1, 1, 30, 30);

            g.DrawImage(chipBmp, new Rectangle(4, 4, 24, 24));
        }

        IntPtr hIcon = canvas.GetHicon();
        try
        {
            using Icon temp = Icon.FromHandle(hIcon);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    public static ImageSource? LoadWindowIconSource(AppConfig config)
    {
        if (s_cachedWindowIcon is not null)
            return s_cachedWindowIcon;

        lock (s_syncLock)
        {
            if (s_cachedWindowIcon is not null)
                return s_cachedWindowIcon;

            try
            {
                var installer = new AltiumToolbarInstaller(config);
                if (!File.Exists(installer.ToolbarPngIconPath))
                    EnsureToolbarIconAssets(config);

                if (!File.Exists(installer.ToolbarPngIconPath))
                    return null;

                byte[] bytes = File.ReadAllBytes(installer.ToolbarPngIconPath);
                var bmpImage = new BitmapImage();
                using var ms = new MemoryStream(bytes);
                bmpImage.BeginInit();
                bmpImage.CacheOption = BitmapCacheOption.OnLoad;
                bmpImage.StreamSource = ms;
                bmpImage.EndInit();
                bmpImage.Freeze();
                s_cachedWindowIcon = bmpImage;
                return s_cachedWindowIcon;
            }
            catch
            {
                return null;
            }
        }
    }

    private static Bitmap GetOrLoadTransparentChipBitmap(string sourcePath)
    {
        lock (s_syncLock)
        {
            s_cachedChipBitmap ??= LoadAndExtractTransparentChipBitmap(sourcePath);
            return (Bitmap)s_cachedChipBitmap.Clone();
        }
    }

    private static Bitmap LoadAndExtractTransparentChipBitmap(string sourcePath)
    {
        if (!File.Exists(sourcePath))
            return CreateFallbackChipBitmap();

        using var raw = new Bitmap(sourcePath);
        int width = raw.Width;
        int height = raw.Height;
        var clean = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        // Görseldeki dama tahtası arka plan pikselleri 70..135 parlaklık aralığındadır,
        // beyaz IC çip çizgileri ise 165..255 aralığındadır. Yumuşak kenar (anti-alias) geçişiyle ayıkla:
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                System.Drawing.Color c = raw.GetPixel(x, y);
                int lum = (c.R + c.G + c.B) / 3;

                if (lum <= 155)
                {
                    clean.SetPixel(x, y, System.Drawing.Color.FromArgb(0, 0, 0, 0));
                }
                else
                {
                    int alpha = Math.Clamp((lum - 155) * 255 / 85, 0, 255);
                    clean.SetPixel(x, y, System.Drawing.Color.FromArgb(alpha, 255, 255, 255));
                }
            }
        }

        // Kare ikon tuvaline ortala
        int maxDim = Math.Max(width, height);
        var squared = new Bitmap(maxDim, maxDim, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(squared))
        {
            g.Clear(System.Drawing.Color.Transparent);
            int offsetX = (maxDim - width) / 2;
            int offsetY = (maxDim - height) / 2;
            g.DrawImage(clean, offsetX, offsetY, width, height);
        }

        clean.Dispose();
        return squared;
    }

    private static Bitmap ResizeBitmapHighQuality(Bitmap source, int width, int height)
    {
        var dest = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(dest))
        {
            g.Clear(System.Drawing.Color.Transparent);
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(source, new Rectangle(0, 0, width, height));
        }

        // Küçültme (downsampling) sırasında ince çizgilerin soluklaşmasını önlemek için
        // tüm çip piksellerini saf parlak beyaz (#FFFFFF) ve yüksek opaklığa güçlendir
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                System.Drawing.Color c = dest.GetPixel(x, y);
                if (c.A > 18)
                {
                    int boostedAlpha = Math.Clamp(c.A * 3, 0, 255);
                    dest.SetPixel(x, y, System.Drawing.Color.FromArgb(boostedAlpha, 255, 255, 255));
                }
                else
                {
                    dest.SetPixel(x, y, System.Drawing.Color.FromArgb(0, 0, 0, 0));
                }
            }
        }

        return dest;
    }

    private static Bitmap CreateFallbackChipBitmap()
    {
        var bmp = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(System.Drawing.Color.Transparent);
        using var pen = new System.Drawing.Pen(System.Drawing.Color.White, 2.2f);
        g.DrawRectangle(pen, 9, 5, 14, 22);
        for (int i = 0; i < 4; i++)
        {
            int y = 8 + i * 5;
            g.DrawLine(pen, 3, y, 9, y);
            g.DrawLine(pen, 23, y, 29, y);
        }
        g.FillEllipse(System.Drawing.Brushes.White, 12, 8, 3, 3);
        return bmp;
    }
}
