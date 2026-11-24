using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Overseer.App.Extensibility;
using Overseer.Core;

namespace Overseer.App.ViewModels;

public sealed partial class SessionItemVM : ObservableObject
{
    public string FilePath { get; }
    public SessionItemVM(string file) => FilePath = file;

    [ObservableProperty] string title = "";
    [ObservableProperty] string subtitle = "";
    [ObservableProperty] string initial = "?";
    [ObservableProperty] IBrush avatarBrush = Brushes.Gray;
    [ObservableProperty] IBrush avatarBg = Brushes.Transparent;
    [ObservableProperty] IBrush stateBrush = Brushes.Gray;
    [ObservableProperty] string stateText = "";
    [ObservableProperty] bool showDot;
    [ObservableProperty] bool isWorking;
    [ObservableProperty] bool isLive;
    [ObservableProperty] string tooltip = "";

    static readonly string[] Palette = { "Accent", "Blue", "Green", "Purple", "Yellow", "Teal", "Pink", "Orange" };

    public void Update(Session s)
    {
        Title = Redactor.Text(s.Title);
        Subtitle = $"{s.ModelName ?? Text.PrettyModel(s.Model)} · {Text.Ago(s.Last)} · {Text.Tokens(s.TotalTokens)}";
        var t = Title.Trim();
        Initial = t.Length > 0 ? char.ToUpperInvariant(t[0]).ToString() : "?";
        var key = Palette[Math.Abs(StableHash(s.Id)) % Palette.Length];
        AvatarBrush = Res.Brush(key);
        AvatarBg = Res.Tint(key, 0x30);
        var st = s.State;
        StateBrush = Res.Brush(Res.StateKey(st));
        StateText = Res.StateText(st);
        ShowDot = st != SessionState.Ended;
        IsWorking = st is SessionState.Working or SessionState.NeedsPermission;
        IsLive = s.IsLive;
        Tooltip = Redactor.Text(s.Title + (s.Cwd != null ? "\n" + s.Cwd : "")) + "\n" + StateText;
    }

    static int StableHash(string s) { unchecked { int h = 17; foreach (var c in s) h = h * 31 + c; return h; } }
}

public sealed partial class MainViewModel : ObservableObject, IOverseerHost, IDisposable
{
    public SessionStore Store { get; } = new();
    public OverseerConfig Config { get; } = OverseerConfig.Load();
    public PriceTable Prices { get; private set; } = PriceTable.Load();
    readonly AlertEngine alerts = new();
    readonly Dictionary<string, SessionState> lastStates = new();
    DispatcherTimer? tick;
    Window? window;

    public ObservableCollection<SessionItemVM> Sessions { get; } = new();
    public ObservableCollection<SessionItemVM> LiveSessions { get; } = new();
    public ObservableCollection<ToastVM> Toasts { get; } = new();

    public OverviewViewModel Overview { get; }
    public ReportsViewModel Reports { get; }
    public SearchViewModel Search { get; }
    public ControlViewModel Control { get; }
    public ConfigureViewModel Configure { get; }
    public ObservableCollection<ExtensionVM> Extensions { get; } = new();

    public event Action<string>? ThemeChanged;
    public event Action<string, string>? TrayStateChanged;
    public event Action? ToggleMiniRequested;
    public event Action? QuitRequested;

    public string[] Pages => HasExtensions
        ? new[] { "Session", "Overview", "Reports", "Search", "Control", "Configure", "Extensions" }
        : new[] { "Session", "Overview", "Reports", "Search", "Control", "Configure" };

    [ObservableProperty] int pageIndex;
    [ObservableProperty] SessionItemVM? selectedItem;
    [ObservableProperty] SessionDetailViewModel? detail;
    [ObservableProperty] string statusText = "Reading session logs…";
    [ObservableProperty] IBrush statusBrush = Brushes.Gray;
    [ObservableProperty] bool redact;
    [ObservableProperty] bool confirmVisible;
    [ObservableProperty] string confirmText = "";
    [ObservableProperty] string confirmYes = "Yes";
    [ObservableProperty] bool hasExtensions;

    public bool IsSessionPage => PageIndex == 0;
    public bool IsOverviewPage => PageIndex == 1;
    public bool IsReportsPage => PageIndex == 2;
    public bool IsSearchPage => PageIndex == 3;
    public bool IsControlPage => PageIndex == 4;
    public bool IsConfigurePage => PageIndex == 5;
    public bool IsExtensionsPage => PageIndex == 6;
    public bool NoSession => Detail == null;

    TaskCompletionSource<bool>? confirmTcs;

    public MainViewModel()
    {
        redact = Config.Redact;
        Redactor.Enabled = Config.Redact;
        Overview = new OverviewViewModel(this);
        Reports = new ReportsViewModel(this);
        Search = new SearchViewModel(this);
        Control = new ControlViewModel(this);
        Configure = new ConfigureViewModel(this);
    }

    public void AttachWindow(Window w) => window = w;

    public void Start()
    {
        Store.Changed += () => Dispatcher.UIThread.Post(Refresh);
        Store.HookEvent += (s, ev, _) => Dispatcher.UIThread.Post(() => OnHook(s, ev));
        Store.Start(TimeSpan.FromSeconds(15));
        tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        tick.Tick += (_, _) => Refresh();
        tick.Start();
        foreach (var ext in ExtensionLoader.Load(Config, this)) Extensions.Add(ext);
        HasExtensions = Extensions.Count > 0;
        if (HasExtensions) OnPropertyChanged(nameof(Pages));
    }

    public void Dispose() { tick?.Stop(); Store.Dispose(); }

    // ---------- page switching ----------
    partial void OnPageIndexChanged(int value)
    {
        foreach (var p in new[] { nameof(IsSessionPage), nameof(IsOverviewPage), nameof(IsReportsPage), nameof(IsSearchPage), nameof(IsControlPage), nameof(IsConfigurePage), nameof(IsExtensionsPage) })
            OnPropertyChanged(p);
        if (value == 1) Overview.Update();
        if (value == 2) Reports.Update();
        if (value == 4) Control.Load();
        if (value == 5) Configure.Load();
    }

    partial void OnSelectedItemChanged(SessionItemVM? value)
    {
        if (value == null) return;
        var s = Store.Find(value.FilePath);
        Detail = s == null ? null : new SessionDetailViewModel(this, s);
        OnPropertyChanged(nameof(NoSession));
        if (PageIndex != 0) PageIndex = 0;
    }

    public void SelectSession(string filePath, bool showConversation = false)
    {
        var item = Sessions.FirstOrDefault(x => x.FilePath == filePath);
        if (item == null) return;
        SelectedItem = item;
        PageIndex = 0;
        if (showConversation) Detail?.LoadConversationCommand.Execute(null);
    }

    partial void OnRedactChanged(bool value)
    {
        Redactor.Enabled = value;
        Config.Redact = value;
        Config.Save();
        Refresh();
        Detail?.Update(true);
    }

    [RelayCommand] void ToggleMini() => ToggleMiniRequested?.Invoke();
    [RelayCommand] void Quit() => QuitRequested?.Invoke();
    public void SetTheme(string theme) { Config.Theme = theme; Config.Save(); ThemeChanged?.Invoke(theme); Refresh(); }
    public void ReloadPrices() => Prices = PriceTable.Load();

    // ---------- refresh ----------
    public void Refresh()
    {
        var top = Store.TopLevel();
        Sync(Sessions, top.Take(80).ToList());
        Sync(LiveSessions, top.Where(s => s.IsLive).ToList());

        if (SelectedItem == null && Sessions.Count > 0)
        {
            var pick = top.FirstOrDefault(s => s.State is SessionState.Working or SessionState.NeedsPermission) ?? top.First();
            SelectedItem = Sessions.FirstOrDefault(x => x.FilePath == pick.FilePath);
        }
        Detail?.Update(false);
        if (IsOverviewPage) Overview.Update();

        int live = top.Count(s => s.IsLive);
        int working = top.Count(s => s.State == SessionState.Working);
        int waiting = top.Count(s => s.State == SessionState.NeedsPermission);
        StatusText = waiting > 0 ? $"{waiting} need{(waiting == 1 ? "s" : "")} you · {working} working · {live} live"
                   : $"{live} live · {working} working";
        StatusBrush = Res.Brush(waiting > 0 ? "Yellow" : working > 0 ? "Accent" : live > 0 ? "Green" : "Faint");
        TrayStateChanged?.Invoke(waiting > 0 ? "attention" : working > 0 ? "working" : "idle", "Overseer · " + StatusText);

        DetectFinished(top);
        foreach (var a in alerts.Evaluate(Store, Config))
        {
            Toast(a.Title + ": " + a.Message, !a.Urgent);
            if (!IsWindowActive) Notifier.Show(a.Title, a.Message);
        }
    }

    static void Sync(ObservableCollection<SessionItemVM> target, List<Session> source)
    {
        for (int i = target.Count - 1; i >= 0; i--)
            if (!source.Any(s => s.FilePath == target[i].FilePath)) target.RemoveAt(i);
        for (int i = 0; i < source.Count; i++)
        {
            var s = source[i];
            var idx = -1;
            for (int j = 0; j < target.Count; j++) if (target[j].FilePath == s.FilePath) { idx = j; break; }
            SessionItemVM item;
            if (idx < 0) { item = new SessionItemVM(s.FilePath); target.Insert(i, item); }
            else { item = target[idx]; if (idx != i) target.Move(idx, i); }
            item.Update(s);
        }
    }

    bool IsWindowActive => window is { IsActive: true, IsVisible: true };

    /// <summary>Notifies when a session goes from working to waiting, from either hooks or the live session file.</summary>
    void DetectFinished(List<Session> top)
    {
        foreach (var s in top.Where(x => x.IsLive))
        {
            var now = s.State;
            if (lastStates.TryGetValue(s.Id, out var before) && before == SessionState.Working && now == SessionState.Idle && Config.NotifyOnFinish)
            {
                var msg = $"\"{Redactor.Text(s.Title)}\" is waiting for you.";
                if (!IsWindowActive) Notifier.Show("Claude finished", msg);
                Toast("Claude finished: " + msg, true);
            }
            lastStates[s.Id] = now;
        }
    }

    void OnHook(Session s, string ev)
    {
        if (ev == "PermissionRequest" || ev == "Notification") Refresh();
    }

    // ---------- toasts & confirmations ----------
    public void Toast(ActionResult r) => Toast(r.Message, r.Ok);

    public void Toast(string message, bool ok)
    {
        var t = new ToastVM { Message = message, Ok = ok };
        Toasts.Add(t);
        while (Toasts.Count > 4) Toasts.RemoveAt(0);
        DispatcherTimer.RunOnce(() => Toasts.Remove(t), TimeSpan.FromSeconds(ok ? 5 : 9));
    }

    [RelayCommand] void DismissToast(ToastVM t) => Toasts.Remove(t);

    public Task<bool> ConfirmAsync(string text, string yes = "Yes")
    {
        confirmTcs?.TrySetResult(false);
        confirmTcs = new TaskCompletionSource<bool>();
        ConfirmText = text;
        ConfirmYes = yes;
        ConfirmVisible = true;
        return confirmTcs.Task;
    }

    [RelayCommand] void ConfirmAccept() { ConfirmVisible = false; confirmTcs?.TrySetResult(true); }
    [RelayCommand] void ConfirmCancel() { ConfirmVisible = false; confirmTcs?.TrySetResult(false); }

    // ---------- file pickers ----------
    public async Task<string?> PickFolderAsync(string title)
    {
        if (window == null) return null;
        var r = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return r.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<string?> SaveFileAsync(string title, string suggestedName, string extension)
    {
        if (window == null) return null;
        var f = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = title, SuggestedFileName = suggestedName, DefaultExtension = extension });
        return f?.TryGetLocalPath();
    }

    // ---------- IOverseerHost (for extensions) ----------
    IReadOnlyList<Session> IOverseerHost.Sessions => Store.TopLevel();
    event Action? IOverseerHost.Changed { add => Store.Changed += value; remove => Store.Changed -= value; }
    void IOverseerHost.Toast(string message, bool ok) => Dispatcher.UIThread.Post(() => Toast(message, ok));
}
