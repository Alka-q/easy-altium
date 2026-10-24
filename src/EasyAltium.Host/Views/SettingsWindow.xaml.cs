using System.Windows;
using EasyAltium.Core.Configuration;
using EasyAltium.Core.Models;

namespace EasyAltium.Host.Views;

public partial class SettingsWindow : Window
{
    private readonly AppConfig _config;

    public SettingsWindow(AppConfig config)
    {
        InitializeComponent();
        _config = config ?? throw new ArgumentNullException(nameof(config));

        AltiumExeBox.Text = _config.AltiumExecutablePath;
        BridgePrjBox.Text = _config.BridgeScriptProjectPath;
        NpnpExeBox.Text = _config.NpnpExecutablePath;
        NpnpArgsBox.Text = _config.NpnpArgumentsTemplate;
        LibraryRootBox.Text = _config.LibraryRootDirectory;
        PassiveTemplateBox.Text = _config.PassiveTemplateSchLibPath;
        InteractivePlacementCheck.IsChecked = _config.PlacementMethod == PlacementMethod.InteractiveCursor;
        OpenLibIfNoSchCheck.IsChecked = _config.OpenLibraryIfNoSchDoc;
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        _config.AltiumExecutablePath = AltiumExeBox.Text.Trim();
        _config.BridgeScriptProjectPath = BridgePrjBox.Text.Trim();
        _config.NpnpExecutablePath = NpnpExeBox.Text.Trim();
        _config.NpnpArgumentsTemplate = NpnpArgsBox.Text.Trim();
        _config.LibraryRootDirectory = LibraryRootBox.Text.Trim();
        _config.PassiveTemplateSchLibPath = PassiveTemplateBox.Text.Trim();
        _config.PlacementMethod = InteractivePlacementCheck.IsChecked == true
            ? PlacementMethod.InteractiveCursor
            : PlacementMethod.DirectToSheet;
        _config.OpenLibraryIfNoSchDoc = OpenLibIfNoSchCheck.IsChecked == true;

        _config.EnsureDirectoriesExist();
        _config.Save();

        DialogResult = true;
        Close();
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
