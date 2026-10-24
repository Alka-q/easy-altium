using System.Globalization;
using System.Windows;
using EasyAltium.Core.Models;

namespace EasyAltium.Host.Views;

public partial class PartPreviewPopup : Window
{
    public event EventHandler<BridgeAction>? ActionConfirmed;

    public PartPreviewPopup()
    {
        InitializeComponent();
        PositionBottomRight();
    }

    public void BindPart(LcscPartMetadata part)
    {
        ArgumentNullException.ThrowIfNull(part);

        LcscCodeText.Text = part.LcscId;
        JlcpcbClassBadge.Text = $"JLCPCB {part.JlcpcbClass}";
        MpnAndBrandText.Text = string.IsNullOrWhiteSpace(part.Manufacturer)
            ? part.Mpn
            : $"{part.Mpn} — {part.Manufacturer}";

        PackageAndStockText.Text =
            $"Kılıf: {(string.IsNullOrWhiteSpace(part.Package) ? "-" : part.Package)} | " +
            $"Stok: {part.Stock.ToString("N0", CultureInfo.InvariantCulture)} | " +
            $"Fiyat: ${part.UnitPriceUsd:0.0000}";

        DescriptionText.Text = string.IsNullOrWhiteSpace(part.Description)
            ? $"Footprint: {part.FootprintRef}"
            : part.Description;

        PositionBottomRight();
    }

    private void PositionBottomRight()
    {
        Rect workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 16;
        Top = workArea.Bottom - Height - 16;
    }

    private void PlaceButton_OnClick(object sender, RoutedEventArgs e)
    {
        Hide();
        ActionConfirmed?.Invoke(this, BridgeAction.PlaceOrLoad);
    }

    private void LoadOnlyButton_OnClick(object sender, RoutedEventArgs e)
    {
        Hide();
        ActionConfirmed?.Invoke(this, BridgeAction.LoadOnly);
    }

    private void DismissButton_OnClick(object sender, RoutedEventArgs e)
    {
        Hide();
    }
}
