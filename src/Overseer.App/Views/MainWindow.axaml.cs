using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Threading;
using Overseer.App.ViewModels;

namespace Overseer.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Draw our own title bar (name, menus, bell) where the OS supports it; the OS keeps drawing the window buttons.
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            ExtendClientAreaToDecorationsHint = true;
            ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.PreferSystemChrome;
            ExtendClientAreaTitleBarHeightHint = 36;
            if (OperatingSystem.IsWindows()) CaptionSpace.Width = 138;
            else TitleBar.Margin = new Thickness(72, 0, 0, 0);
        }

        DragArea.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            else BeginMoveDrag(e);
        };

        // clicking the already-selected session still opens it
        SessionsList.Tapped += (_, _) => { if (DataContext is MainViewModel vm && vm.SelectedItem != null) vm.PageIndex = 1; };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainViewModel vm) vm.PropertyChanged += OnVmChanged;
    }

    void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.PaletteOpen) && sender is MainViewModel { PaletteOpen: true })
            Dispatcher.UIThread.Post(() => PaletteBox.Focus(), DispatcherPriority.Input);
    }

    void OnBellClosed(object? sender, EventArgs e) { if (DataContext is MainViewModel vm) vm.BellOpen = false; }

    void OnOverlayPressed(object? sender, PointerPressedEventArgs e) { if (DataContext is MainViewModel vm) vm.PaletteOpen = false; }

    void OnDialogPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;
}
