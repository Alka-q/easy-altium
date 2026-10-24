using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using EasyAltium.Host.ViewModels;

namespace EasyAltium.Host.Views;

public partial class MiniSearchWindow : Window
{
    public bool AllowFullClose { get; set; }

    public MiniSearchWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void SearchBox_OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is MainViewModel vm && vm.FetchPartCommand.CanExecute(null))
        {
            vm.FetchPartCommand.Execute(null);
            e.Handled = true;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Sistem tepsisinde (System Tray) çalışmaya devam etmek için çarpıya basıldığında gizle
        if (!AllowFullClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }
}
