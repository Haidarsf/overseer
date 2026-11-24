using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Overseer.App.ViewModels;

namespace Overseer.App.Views;

public partial class SessionView : UserControl
{
    public SessionView()
    {
        InitializeComponent();
        // focus a new permission request so A and D work straight away
        PermissionHost.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && PermissionHost.IsVisible) Dispatcher.UIThread.Post(() => PermissionCard.Focus(), DispatcherPriority.Input);
        };
    }

    /// <summary>A = allow once, D = deny (opens the note field) while the permission card has focus.</summary>
    void OnPermissionKey(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox) return;
        if (TopLevel.GetTopLevel(this)?.DataContext is not MainViewModel main || PermissionCard.DataContext is not PendingVM p) return;
        if (e.Key == Key.A && e.KeyModifiers == KeyModifiers.None && !p.DenyOpen) { main.AllowOnceCommand.Execute(p); e.Handled = true; }
        else if (e.Key == Key.D && e.KeyModifiers == KeyModifiers.None && !p.DenyOpen)
        {
            main.OpenDenyCommand.Execute(p);
            Dispatcher.UIThread.Post(() => DenyBox.Focus(), DispatcherPriority.Input);
            e.Handled = true;
        }
    }
}
