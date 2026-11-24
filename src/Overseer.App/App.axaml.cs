using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Styling;
using Overseer.App.ViewModels;
using Overseer.App.Views;
using Overseer.Core;

namespace Overseer.App;

public partial class App : Application
{
    MainViewModel? vm;
    MainWindow? window;
    MiniWindow? mini;
    TrayIcon? tray;
    string trayState = "";

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // log and carry on instead of closing: a monitor that crashes on one odd file is worse than one that skips it
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            ErrorLog.Write(e.Exception, "ui");
            e.Handled = true;
            vm?.ReportError();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) ErrorLog.Write(ex, "fatal"); };
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            vm = new MainViewModel();
            ApplyTheme(vm.Config.Theme);
            vm.ThemeChanged += ApplyTheme;
            window = new MainWindow { DataContext = vm };
            vm.AttachWindow(window);
            vm.TrayStateChanged += SetTrayState;
            vm.ToggleMiniRequested += ToggleMini;
            vm.QuitRequested += () => Quit(desktop);
            vm.ShowMainRequested += ShowMain;
            vm.HideWindowRequested += () =>
            {
                window.Hide();
                Notifier.Show("Overseer is still running", "It's in the tray and will tell you when a session needs you.");
            };
            window.Closing += (_, e) =>
            {
                if (vm.Config.CloseToTray && !quitting) { e.Cancel = true; window.Hide(); }
            };
            CreateTray(desktop);
            if (!vm.Config.StartMinimized) window.Show();
            vm.Start();
        }
        base.OnFrameworkInitializationCompleted();
    }

    bool quitting;
    void Quit(IClassicDesktopStyleApplicationLifetime desktop)
    {
        quitting = true;
        vm?.Dispose();
        tray?.Dispose();
        desktop.Shutdown();
    }

    void ApplyTheme(string theme) =>
        RequestedThemeVariant = theme switch { "light" => ThemeVariant.Light, "dark" => ThemeVariant.Dark, _ => ThemeVariant.Default };

    void CreateTray(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var menu = new NativeMenu();
        var open = new NativeMenuItem("Open Overseer");
        open.Click += (_, _) => ShowMain();
        var miniItem = new NativeMenuItem("Mini window");
        miniItem.Click += (_, _) => ToggleMini();
        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) => Quit(desktop);
        menu.Items.Add(open);
        menu.Items.Add(miniItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(quit);
        tray = new TrayIcon { ToolTipText = "Overseer", Menu = menu, IsVisible = true };
        tray.Clicked += (_, _) => ShowMain();
        SetTrayState("idle", "Overseer");
        TrayIcon.SetIcons(this, new TrayIcons { tray });
    }

    void SetTrayState(string state, string tooltip)
    {
        if (tray == null) return;
        tray.ToolTipText = tooltip;
        if (state == trayState) return;
        trayState = state;
        tray.Icon = new WindowIcon(AssetLoader.Open(new Uri($"avares://Overseer/Assets/tray-{state}.png")));
    }

    void ShowMain()
    {
        if (window == null) return;
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    void ToggleMini()
    {
        if (vm == null) return;
        if (mini is { IsVisible: true }) { mini.Close(); mini = null; return; }
        mini = new MiniWindow { DataContext = vm };
        mini.Closed += (_, _) => mini = null;
        mini.Show();
    }
}
