using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using EasyAltium.Core.Configuration;
using EasyAltium.Host.ViewModels;

namespace EasyAltium.Host.Views;

public partial class ComponentBrowserWindow : Window
{
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hWnd);

    private readonly ComponentBrowserViewModel _viewModel;
    private bool _isSuppressingTreeSelection;

    public bool AllowFullClose { get; set; }

    public ComponentBrowserViewModel ViewModel => _viewModel;

    public ComponentBrowserWindow(AppConfig config, MainViewModel mainViewModel)
    {
        InitializeComponent();

        _viewModel = new ComponentBrowserViewModel(config, mainViewModel);
        _viewModel.MinimizeForPlacementRequested += (_, _) => MinimizeAndFocusAltium();
        DataContext = _viewModel;

        var iconSource = Services.BrandIconProcessor.LoadWindowIconSource(config);
        if (iconSource is not null)
        {
            Icon = iconSource;
        }

        Loaded += async (_, _) =>
        {
            Activate();
            CatalogSearchBox.Focus();
            await _viewModel.InitializeAsync();
        };
    }

    private void CatalogSearchBox_OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _viewModel.SearchCommand.CanExecute(null))
        {
            _viewModel.SearchCommand.Execute(null);
            e.Handled = true;
        }
    }

    private async void CategoryTreeView_OnSelectedItemChanged(
        object sender,
        RoutedPropertyChangedEventArgs<object> e)
    {
        if (_isSuppressingTreeSelection)
            return;

        if (e.NewValue is CatalogCategoryNodeViewModel selectedNode)
        {
            try
            {
                _isSuppressingTreeSelection = true;
                await _viewModel.SelectCategoryNodeAsync(selectedNode, toggleExpandIfParent: false);
            }
            finally
            {
                _isSuppressingTreeSelection = false;
            }
        }
    }

    private async void CategoryHeaderBorder_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsDescendantOfToggleButton(e.OriginalSource as DependencyObject))
            return;

        if (sender is FrameworkElement fe && fe.DataContext is CatalogCategoryNodeViewModel clickedNode)
        {
            if (clickedNode.HasChildren)
            {
                clickedNode.IsExpanded = !clickedNode.IsExpanded;
            }

            e.Handled = true;
            try
            {
                _isSuppressingTreeSelection = true;
                await _viewModel.SelectCategoryNodeAsync(clickedNode, toggleExpandIfParent: false);
            }
            finally
            {
                _isSuppressingTreeSelection = false;
            }
        }
    }

    private void MinimizeAndFocusAltium()
    {
        WindowState = WindowState.Minimized;

        try
        {
            foreach (Process proc in Process.GetProcessesByName("X2"))
            {
                try
                {
                    if (proc.MainWindowHandle != IntPtr.Zero)
                    {
                        SetForegroundWindow(proc.MainWindowHandle);
                        break;
                    }
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    private static bool IsDescendantOfToggleButton(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ToggleButton)
                return true;

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowFullClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }
}
