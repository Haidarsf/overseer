using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Overseer.App.Extensibility;
using Overseer.Core;

namespace Overseer.App.ViewModels;

public sealed record NavItemVM(string Label, string Shortcut);
public sealed record PaletteItemVM(string Label, string Kind, Action Run);
public sealed partial class StartOptionVM : ObservableObject
{
    public StartOptionVM(string name, string flags, Preset? preset) { Name = name; Flags = flags; Preset = preset; }
    public string Name { get; }
    public string Flags { get; }
    public Preset? Preset { get; }
}

public sealed partial class MainViewModel : ObservableObject, IOverseerHost, IDisposable
{
    public SessionStore Store { get; } = new();
    public OverseerConfig Config { get; } = OverseerConfig.Load();
    public PriceTable Prices { get; private set; } = PriceTable.Load();
    readonly AlertEngine alerts = new();
    readonly Dictionary<string, SessionState> lastStates = new();
    readonly HashSet<string> notifiedPending = new();
    readonly Dictionary<string, PendingVM> pendingById = new();
    // answered here, but the hook has not picked the answer up and removed its file yet
    readonly HashSet<string> answered = new();
    readonly HashSet<string> stopping = new();
    DispatcherTimer? tick, clock;
    FileSystemWatcher? pendingWatcher;
    Window? window;

    public ObservableCollection<SessionItemVM> Sessions { get; } = new();
    public ObservableCollection<SessionItemVM> VisibleSessions { get; } = new();
    public ObservableCollection<SessionItemVM> LiveSessions { get; } = new();
    public ObservableCollection<PendingVM> Pending { get; } = new();
    public ObservableCollection<ToastVM> Toasts { get; } = new();
    public ObservableCollection<PaletteItemVM> PaletteItems { get; } = new();

    public OverviewViewModel Overview { get; }
    public ReportsViewModel Reports { get; }
    public SearchViewModel Search { get; }
    public ControlViewModel Control { get; }
    public UpdatesViewModel Updates { get; }
    public ConfigureViewModel Configure { get; }
    public ObservableCollection<ExtensionVM> Extensions { get; } = new();

    public event Action<string>? ThemeChanged;
    public event Action<string, string>? TrayStateChanged;
    public event Action? ToggleMiniRequested;
    public event Action? QuitRequested;
    public event Action? HideWindowRequested;

    public List<NavItemVM> Nav => HasExtensions
        ? new() { new("Overview", "Ctrl+1"), new("Session", "Ctrl+2"), new("Reports", "Ctrl+3"), new("Search", "Ctrl+4"), new("Settings", "Ctrl+,"), new("Extensions", "") }
        : new() { new("Overview", "Ctrl+1"), new("Session", "Ctrl+2"), new("Reports", "Ctrl+3"), new("Search", "Ctrl+4"), new("Settings", "Ctrl+,") };

    [ObservableProperty] int pageIndex;
    [ObservableProperty] SessionItemVM? selectedItem;
    [ObservableProperty] SessionDetailViewModel? detail;
    [ObservableProperty] string statusText = "Reading session logs…";
    [ObservableProperty] IBrush statusBrush = Brushes.Gray;
    [ObservableProperty] bool redact;
    [ObservableProperty] bool hideDone;
    [ObservableProperty] bool isDark;
    [ObservableProperty] bool confirmVisible;
    [ObservableProperty] string confirmText = "";
    [ObservableProperty] string confirmYes = "Yes";
    [ObservableProperty] bool hasExtensions;
    [ObservableProperty] bool bellOpen;
    [ObservableProperty] string needsCount = "0";
    [ObservableProperty] bool hasNeeds;
    [ObservableProperty] string bellNote = "";
    [ObservableProperty] string viewTitle = "Overview";
    [ObservableProperty] string viewSub = "";

    // status bar
    [ObservableProperty] string liveText = "";
    [ObservableProperty] IBrush liveBrush = Brushes.Gray;
    [ObservableProperty] string answeringText = "";
    [ObservableProperty] string defaultsText = "";
    [ObservableProperty] string planText = "";

    // command palette
    [ObservableProperty] bool paletteOpen;
    [ObservableProperty] string paletteQuery = "";
    [ObservableProperty] PaletteItemVM? paletteSelected;

    // new session dialog
    [ObservableProperty] bool newOpen;
    [ObservableProperty] string newFolder = Paths.Home;
    [ObservableProperty] List<string> recentFolders = new();
    [ObservableProperty] List<StartOptionVM> startOptions = new();
    [ObservableProperty] StartOptionVM? startPick;

    public bool IsOverviewPage => PageIndex == 0;
    public bool IsSessionPage => PageIndex == 1;
    public bool IsReportsPage => PageIndex == 2;
    public bool IsSearchPage => PageIndex == 3;
    public bool IsSettingsPage => PageIndex == 4;
    public bool IsExtensionsPage => PageIndex == 5;
    public bool NoSession => Detail == null;
    public bool HasPendingRequests => Pending.Count > 0;
    public bool NoPendingRequests => Pending.Count == 0;
    public string PendingCount => Pending.Count.ToString(CultureInfo.InvariantCulture);
    public bool NoVisibleSessions => VisibleSessions.Count == 0;
    public bool NoLiveSessions => LiveSessions.Count == 0;

    TaskCompletionSource<bool>? confirmTcs;
    bool autoPicking;

    public MainViewModel()
    {
        redact = Config.Redact;
        hideDone = Config.HideDone;
        Redactor.Enabled = Config.Redact;
        Overview = new OverviewViewModel(this);
        Reports = new ReportsViewModel(this);
        Search = new SearchViewModel(this);
        Control = new ControlViewModel(this);
        Configure = new ConfigureViewModel(this);
        Updates = new UpdatesViewModel(this);
        Pending.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasPendingRequests)); OnPropertyChanged(nameof(NoPendingRequests)); OnPropertyChanged(nameof(PendingCount));
        };
        VisibleSessions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(NoVisibleSessions));
        LiveSessions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(NoLiveSessions));
    }

    public void AttachWindow(Window w) => window = w;

    public void Start()
    {
        SyncDark();
        if (Application.Current != null)
            Application.Current.ActualThemeVariantChanged += (_, _) => { SyncDark(); Refresh(); Detail?.Update(true); };
        PermissionBridge.Heartbeat();
        Store.Changed += () => Dispatcher.UIThread.Post(Refresh);
        Store.HookEvent += (s, ev, _) => Dispatcher.UIThread.Post(() => OnHook(s, ev));
        Store.Start(TimeSpan.FromSeconds(15));
        tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        tick.Tick += (_, _) => { PermissionBridge.Heartbeat(); Refresh(); };
        tick.Start();
        clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        clock.Tick += (_, _) => { foreach (var p in Pending) p.Tick(); };
        clock.Start();
        DispatcherTimer.RunOnce(() => _ = Updates.CheckIfDue(), TimeSpan.FromSeconds(20));
        try
        {
            pendingWatcher = new FileSystemWatcher(PermissionBridge.PendingDir, "*.json") { EnableRaisingEvents = true };
            pendingWatcher.Created += (_, _) => Dispatcher.UIThread.Post(Refresh);
            pendingWatcher.Renamed += (_, _) => Dispatcher.UIThread.Post(Refresh);   // the hook writes a temp file, then renames it
            pendingWatcher.Deleted += (_, _) => Dispatcher.UIThread.Post(Refresh);
        }
        catch { /* the 5-second refresh still picks them up */ }
        foreach (var ext in ExtensionLoader.Load(Config, this)) Extensions.Add(ext);
        HasExtensions = Extensions.Count > 0;
        if (HasExtensions) OnPropertyChanged(nameof(Nav));
        Refresh();
    }

    public void Dispose()
    {
        tick?.Stop(); clock?.Stop();
        pendingWatcher?.Dispose();
        PermissionBridge.ClearHeartbeat();
        Store.Dispose();
    }

    // ---------- navigation ----------
    partial void OnPageIndexChanged(int value)
    {
        foreach (var p in new[] { nameof(IsOverviewPage), nameof(IsSessionPage), nameof(IsReportsPage), nameof(IsSearchPage), nameof(IsSettingsPage), nameof(IsExtensionsPage) })
            OnPropertyChanged(p);
        if (value == 0) Overview.Update();
        if (value == 2) Reports.Update();
        if (value == 4) { Control.Load(); Configure.Load(); }
        UpdateTitle();
    }

    [RelayCommand] void Go(string page) { PaletteOpen = false; BellOpen = false; PageIndex = int.Parse(page, CultureInfo.InvariantCulture); }

    partial void OnSelectedItemChanged(SessionItemVM? value)
    {
        if (value == null) return;
        var s = Store.Find(value.FilePath);
        Detail = s == null ? null : new SessionDetailViewModel(this, s);
        Detail?.SetPending(value.Pending, stopping.Contains(value.FilePath));
        OnPropertyChanged(nameof(NoSession));
        if (!autoPicking) PageIndex = 1;
        UpdateTitle();
    }

    public void SelectSession(string filePath, bool showConversation = false)
    {
        var item = Sessions.FirstOrDefault(x => x.FilePath == filePath);
        if (item == null) return;
        if (SelectedItem == item) PageIndex = 1; else SelectedItem = item;
        if (showConversation) Detail?.LoadConversationCommand.Execute(null);
    }

    [RelayCommand] void OpenSession(SessionItemVM s) { BellOpen = false; SelectSession(s.FilePath); }
    [RelayCommand] void OpenFromMini(SessionItemVM s) { ShowMainRequested?.Invoke(); SelectSession(s.FilePath); }

    void UpdateTitle()
    {
        var top = Sessions.ToList();
        int needs = top.Count(s => s.NeedsYou), working = top.Count(s => s.State == SessionState.Working), turn = top.Count(s => s.State == SessionState.Idle);
        switch (PageIndex)
        {
            case 0:
                ViewTitle = "Overview";
                ViewSub = string.Join(" · ", new[] { needs > 0 ? needs + " need you" : null, working > 0 ? working + " working" : null, turn > 0 ? turn + " waiting for your reply" : null }.Where(x => x != null));
                break;
            case 1:
                ViewTitle = Detail?.Title ?? "Session";
                ViewSub = Detail != null ? Detail.StateText + " · " + Detail.ModelName : "";
                break;
            case 2: ViewTitle = "Reports"; ViewSub = "tokens and API-equivalent cost"; break;
            case 3: ViewTitle = "Search conversations"; ViewSub = ""; break;
            case 4: ViewTitle = "Settings"; ViewSub = ""; break;
            default: ViewTitle = "Extensions"; ViewSub = ""; break;
        }
    }

    // ---------- toggles ----------
    partial void OnRedactChanged(bool value)
    {
        Redactor.Enabled = value;
        Config.Redact = value;
        Config.Save();
        Refresh();
        Detail?.Update(true);
        UpdateTitle();
    }

    partial void OnHideDoneChanged(bool value)
    {
        Config.HideDone = value;
        Config.Save();
        Refresh();
    }

    [RelayCommand] void ToggleRedact() => Redact = !Redact;
    [RelayCommand] void ToggleHideDone() => HideDone = !HideDone;
    [RelayCommand] void ToggleDark() => IsDark = !IsDark;
    bool syncingTheme;
    partial void OnIsDarkChanged(bool value) { if (!syncingTheme) SetTheme(value ? "dark" : "light"); }
    void SyncDark() { syncingTheme = true; IsDark = Application.Current?.ActualThemeVariant == ThemeVariant.Dark; syncingTheme = false; }
    [RelayCommand] void GoPrivacy() { Control.Tab = 8; Go("4"); }
    [RelayCommand] public void GoUpdates() { Control.Tab = 10; Go("4"); }
    [RelayCommand] void CheckForUpdates() { GoUpdates(); Updates.CheckNowCommand.Execute(null); }
    /// <summary>The installer is running: close for real (not to the tray) so it can replace the files.</summary>
    public void QuitForUpdate() => QuitRequested?.Invoke();
    [RelayCommand] void OpenGitHub() => ProcessControl.Open("https://github.com/Haidarsf/overseer");
    [RelayCommand]
    void ShowShortcuts() => Toast("Ctrl+K search and commands · Ctrl+N new session · Ctrl+1–4 views · Ctrl+, settings · Ctrl+M mini window · " +
        "Ctrl+Shift+D dark theme · Ctrl+Shift+H hide private info · A / D allow or deny a focused permission request · Esc closes", true);
    [RelayCommand] void ToggleMini() { PaletteOpen = false; ToggleMiniRequested?.Invoke(); }
    [RelayCommand] void Quit() => QuitRequested?.Invoke();
    [RelayCommand] void CloseToTray() => HideWindowRequested?.Invoke();
    [RelayCommand] void ToggleBell() => BellOpen = !BellOpen;

    public void SetTheme(string theme) { Config.Theme = theme; Config.Save(); ThemeChanged?.Invoke(theme); }
    public void ReloadPrices() => Prices = PriceTable.Load();

    // ---------- refresh ----------
    public void Refresh()
    {
        try { RefreshCore(); }
        catch (Exception ex)
        {
            // one odd session file must not take the whole window down
            ErrorLog.Write(ex, "refresh");
            ReportError();
        }
    }

    DateTime lastErrorToast;
    /// <summary>Tells the user (at most once a minute) that something failed and where the details are.</summary>
    public void ReportError()
    {
        if (DateTime.Now - lastErrorToast < TimeSpan.FromMinutes(1)) return;
        lastErrorToast = DateTime.Now;
        Toast("Something went wrong showing part of Overseer. Details are in ~/.overseer/errors.log.", false, () => ProcessControl.Open(ErrorLog.File), "Open log");
    }

    void RefreshCore()
    {
        var top = Store.TopLevel();
        var pending = SyncPending(top);
        Sync(Sessions, top.Take(80).ToList(), pending);
        var visible = Sessions.Where(s => !HideDone || !s.IsDone).Select(s => s.FilePath).ToHashSet();
        SyncItems(VisibleSessions, Sessions.Where(s => visible.Contains(s.FilePath)).OrderBy(Rank).ToList());
        SyncItems(LiveSessions, Sessions.Where(s => s.IsLive || s.HasPending).OrderBy(Rank).ToList());

        if (SelectedItem == null && Sessions.Count > 0)
        {
            var pick = Sessions.OrderBy(Rank).First();
            autoPicking = true;
            SelectedItem = pick;
            autoPicking = false;
        }
        if (SelectedItem != null) Detail?.SetPending(SelectedItem.Pending, stopping.Contains(SelectedItem.FilePath));
        Detail?.Update(false);
        if (IsOverviewPage) Overview.Update();
        else Overview.UpdateUsage();

        int live = top.Count(s => s.IsLive);
        int working = top.Count(s => s.State == SessionState.Working);
        int waiting = Sessions.Count(s => s.NeedsYou);
        int inTerminal = Sessions.Count(s => s.NeedsYou && !s.HasPending);
        NeedsCount = waiting.ToString(CultureInfo.InvariantCulture);
        HasNeeds = waiting > 0;
        BellNote = inTerminal == 0 ? "" : Integration.PermissionHookInstalled()
            ? $"{inTerminal} more waiting in the terminal (asked before Overseer started, or Overseer stopped waiting)."
            : $"{inTerminal} waiting in the terminal. To answer here, turn on Settings → Live data → Answer permission prompts here.";
        StatusText = waiting > 0 ? $"{waiting} need{(waiting == 1 ? "s" : "")} you · {working} working · {live} open"
                   : $"{live} open · {working} working";
        StatusBrush = Res.Brush(waiting > 0 ? "Accent" : working > 0 ? "WorkDot" : live > 0 ? "Green" : "DoneDot");
        TrayStateChanged?.Invoke(waiting > 0 ? "attention" : working > 0 ? "working" : "idle", "Overseer · " + StatusText);
        UpdateStatusBar();
        UpdateTitle();

        DetectFinished(top);
        foreach (var a in alerts.Evaluate(Store, Config))
        {
            Toast(a.Title + ": " + a.Message, !a.Urgent);
            if (!IsWindowActive) Notifier.Show(a.Title, a.Message);
        }
    }

    static int Rank(SessionItemVM s) => s.State switch
    {
        SessionState.NeedsPermission => 0, SessionState.Working => 1, SessionState.Idle => 2, _ => 3,
    };

    Dictionary<string, PendingVM> SyncPending(List<Session> top)
    {
        var onDisk = PermissionBridge.Pending();
        answered.IntersectWith(onDisk.Select(p => p.Id));
        var now = onDisk.Where(p => !answered.Contains(p.Id)).ToList();
        foreach (var id in pendingById.Keys.Where(k => now.All(p => p.Id != k)).ToList()) pendingById.Remove(id);
        foreach (var p in now)
        {
            var s = top.FirstOrDefault(x => x.Id == p.SessionId);
            if (!pendingById.TryGetValue(p.Id, out var vm)) pendingById[p.Id] = vm = new PendingVM(p);
            vm.Update(p, s);
            if (notifiedPending.Add(p.Id) && Config.NotifyOnPermission && !IsWindowActive)
                Notifier.Show("Claude needs your OK", $"{vm.Title}: {vm.Ask}. Answer in Overseer or the terminal.");
        }
        for (int i = Pending.Count - 1; i >= 0; i--) if (!pendingById.ContainsKey(Pending[i].Id)) Pending.RemoveAt(i);
        foreach (var vm in pendingById.Values) if (!Pending.Contains(vm)) Pending.Add(vm);
        if (Pending.Count == 0) BellOpen = false;
        // one per session: the oldest is answered first
        return now.GroupBy(p => p.SessionId).ToDictionary(g => g.Key, g => pendingById[g.First().Id]);
    }

    static void Sync(ObservableCollection<SessionItemVM> target, List<Session> source, Dictionary<string, PendingVM> pending)
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
            item.Update(s, pending.GetValueOrDefault(s.Id));
        }
    }

    static void SyncItems(ObservableCollection<SessionItemVM> target, List<SessionItemVM> source)
    {
        for (int i = target.Count - 1; i >= 0; i--) if (!source.Contains(target[i])) target.RemoveAt(i);
        for (int i = 0; i < source.Count; i++)
        {
            var idx = target.IndexOf(source[i]);
            if (idx < 0) target.Insert(i, source[i]);
            else if (idx != i) target.Move(idx, i);
        }
    }

    void UpdateStatusBar()
    {
        var hooks = Integration.HooksInstalled();
        var answering = Integration.PermissionHookInstalled();
        LiveText = "Live events " + (hooks ? "on" : "off") + " · status line " + (Integration.StatusLineInstalled() ? "on" : "off");
        LiveBrush = Res.Brush(hooks ? "Green" : "DoneDot");
        AnsweringText = answering ? "Answering prompts here" : "Prompts answered in the terminal";
        DefaultsText = "New sessions: " + DefaultsSummary();
        var lim = Store.LatestLimits();
        PlanText = lim == null ? "Plan limits: turn on the status line"
            : "Plan: 5 h " + Pct(lim.FiveHourUsedPercent) + " · week " + Pct(lim.WeeklyUsedPercent);
    }

    static string Pct(double? v) => v is { } x ? Math.Round(x).ToString(CultureInfo.InvariantCulture) + "%" : "–";

    public static string DefaultsSummary()
    {
        var model = SettingsFile.GetString("model");
        var mode = SettingsFile.GetString("permissions", "defaultMode");
        var modelName = model == null ? "plan default model" : Text.PrettyModel(model) is { Length: > 0 } p && p != "?" ? p : model;
        var eff = model != null ? SettingsFile.GetString("modelSettings", model, "effortLevel") : null;
        eff ??= SettingsFile.GetString("effortLevel");
        return modelName + (eff != null ? ", " + eff + " effort" : "") + ", " + ModeName(mode);
    }

    public static string ModeName(string? mode) => mode switch
    {
        "acceptEdits" => "accept edits", "plan" => "plan mode", "auto" => "auto mode", "dontAsk" => "don't ask", "bypassPermissions" => "bypass (every check skipped)", _ => "ask first",
    };

    bool IsWindowActive => window is { IsActive: true, IsVisible: true };

    /// <summary>Notifies when a session goes from working to waiting, from either hooks or the live session file.</summary>
    void DetectFinished(List<Session> top)
    {
        foreach (var s in top.Where(x => x.IsLive))
        {
            var now = s.State;
            if (lastStates.TryGetValue(s.Id, out var before) && before == SessionState.Working && now == SessionState.Idle && Config.NotifyOnFinish)
            {
                var msg = $"\"{Redactor.Title(s.Title, s.Id)}\" is waiting for you.";
                if (!IsWindowActive) Notifier.Show("Claude finished", msg);
                Toast("Claude finished: " + msg, true);
            }
            lastStates[s.Id] = now;
        }
        foreach (var f in stopping.ToList())
            if (top.FirstOrDefault(x => x.FilePath == f) is not { IsLive: true }) stopping.Remove(f);
    }

    void OnHook(Session s, string ev)
    {
        if (ev is "PermissionRequest" or "Notification" or "PreToolUse" or "Stop") Refresh();
    }

    // ---------- answering permission prompts ----------
    [RelayCommand]
    void AllowOnce(PendingVM p)
    {
        if (!Answer(p, PermissionAnswer.AllowOnce())) return;
        Toast("Allowed once. Claude is going ahead.", true);
    }

    [RelayCommand]
    void AllowAlways(PendingVM p)
    {
        var dest = p.Destination;
        var rule = p.Rule;
        var cwd = p.Cwd;
        if (!Answer(p, PermissionAnswer.AllowAlways(dest))) return;
        var where = dest == "userSettings" ? "all your projects (~/.claude/settings.json)" : "this project (.claude/settings.local.json)";
        Toast($"Allowed. {(Redact ? "The rule" : rule)} is now always allowed in {where}.", true, () =>
        {
            var file = PermissionBridge.FileFor(dest, cwd);
            var results = rule.Split(", ").Select(r => PermissionBridge.RemoveAllowRule(file, r)).ToList();
            Toast(results.All(r => r.Ok) ? "Rule removed. The command already ran; next time Claude will ask again." : results.First(r => !r.Ok).Message, results.All(r => r.Ok));
        }, "Remove rule");
    }

    [RelayCommand] void OpenDeny(PendingVM p) => p.DenyOpen = true;
    [RelayCommand] void CancelDeny(PendingVM p) => p.DenyOpen = false;

    [RelayCommand]
    void Deny(PendingVM p)
    {
        var note = p.DenyNote;
        if (!Answer(p, PermissionAnswer.Deny(note))) return;
        Toast(string.IsNullOrWhiteSpace(note) ? "Denied. Claude was told not to do it." : "Denied, and your note was sent to Claude.", true);
    }

    /// <summary>Deny… from the bell or the mini window: opens the session with the note field ready.</summary>
    [RelayCommand]
    void DenyWithNote(PendingVM p)
    {
        BellOpen = false;
        p.DenyOpen = true;
        if (p.SessionFile != null) { ShowMainRequested?.Invoke(); SelectSession(p.SessionFile); }
        else Deny(p);
    }

    [RelayCommand]
    void OpenPending(PendingVM p)
    {
        BellOpen = false;
        if (p.SessionFile != null) { ShowMainRequested?.Invoke(); SelectSession(p.SessionFile); }
    }

    public event Action? ShowMainRequested;

    bool Answer(PendingVM p, PermissionAnswer a)
    {
        var r = PermissionBridge.Answer(p.Id, a);
        if (r.Ok) answered.Add(p.Id);
        if (!r.Ok) Toast(r.Message, false);
        pendingById.Remove(p.Id);
        Pending.Remove(p);
        Refresh();
        return r.Ok;
    }

    // ---------- stopping ----------
    public async Task StopSession(Session s, string title)
    {
        if (!s.IsLive || s.Pid <= 0) return;
        if (!await ConfirmAsync($"Stop \"{title}\"?\n\nOverseer asks Claude Code (pid {s.Pid}) to stop and only force-closes it if it doesn't respond. The conversation stays on disk and can be resumed.", "Stop session")) return;
        stopping.Add(s.FilePath);
        Refresh();
        var r = await ProcessControl.StopGracefully(s.Pid, TimeSpan.FromSeconds(6));
        Toast(r);
        Store.ForceRescan();
        Refresh();
    }

    // ---------- toasts & confirmations ----------
    public void Toast(ActionResult r) => Toast(r.Message, r.Ok);

    public void Toast(string message, bool ok, Action? undo = null, string undoLabel = "Undo")
    {
        var t = new ToastVM { Message = message, Ok = ok, Undo = undo, UndoLabel = undoLabel };
        Toasts.Add(t);
        while (Toasts.Count > 3) Toasts.RemoveAt(0);
        DispatcherTimer.RunOnce(() => Toasts.Remove(t), TimeSpan.FromSeconds(undo != null ? 8 : ok ? 5 : 9));
    }

    [RelayCommand] void DismissToast(ToastVM t) => Toasts.Remove(t);
    [RelayCommand] void UndoToast(ToastVM t) { Toasts.Remove(t); t.Undo?.Invoke(); }

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

    /// <summary>Esc: closes whatever is open on top.</summary>
    [RelayCommand]
    void Escape()
    {
        if (ConfirmVisible) ConfirmCancel();
        else if (PaletteOpen) PaletteOpen = false;
        else if (NewOpen) NewOpen = false;
        else if (BellOpen) BellOpen = false;
        else if (Detail?.Pending is { DenyOpen: true } p) p.DenyOpen = false;
    }

    // ---------- command palette ----------
    [RelayCommand]
    void OpenPalette()
    {
        BellOpen = false;
        PaletteQuery = "";
        FillPalette();
        PaletteOpen = true;
    }

    partial void OnPaletteQueryChanged(string value) => FillPalette();

    void FillPalette()
    {
        var items = new List<PaletteItemVM>();
        foreach (var p in Pending)
            items.Add(new($"Review: {p.Title} {p.Ask}", "needs you", () => OpenPending(p)));
        items.Add(new("Go to overview", "view", () => Go("0")));
        items.Add(new("Go to the selected session", "view", () => Go("1")));
        items.Add(new("Go to reports", "view", () => Go("2")));
        items.Add(new("Go to settings", "view", () => Go("4")));
        items.Add(new("New session…", "session", OpenNew));
        foreach (var (id, name) in new[] { ("fable", "Fable"), ("opus", "Opus"), ("sonnet", "Sonnet"), ("haiku", "Haiku") })
            items.Add(new($"Use {name} for new sessions", "model", () => SetDefaultModel(id, name)));
        if (Detail != null)
        {
            var d = Detail;
            items.Add(new($"Continue \"{d.Title}\" as a copy on Fable", "session", () => { PaletteOpen = false; d.Fork("fable"); }));
            items.Add(new($"Resume \"{d.Title}\" in a terminal", "session", () => { PaletteOpen = false; d.ResumeCommand.Execute(null); }));
        }
        items.Add(new(LiveSessions.Count > 0 ? "Open the mini window" : "Open the mini window (nothing running now)", "window", () => ToggleMini()));
        items.Add(new(IsDark ? "Switch to the light theme" : "Switch to the dark theme", "view", () => { PaletteOpen = false; ToggleDark(); }));
        items.Add(new(Redact ? "Show private info" : "Hide private info", "privacy", () => { PaletteOpen = false; ToggleRedact(); }));
        items.Add(new(HideDone ? "Show done sessions" : "Hide done sessions", "view", () => { PaletteOpen = false; ToggleHideDone(); }));
        items.Add(new("Check for updates", "overseer", () => { PaletteOpen = false; CheckForUpdates(); }));
        foreach (var s in Sessions.Take(30))
            items.Add(new("Open: " + s.Title, s.StateText.ToLowerInvariant(), () => { PaletteOpen = false; SelectSession(s.FilePath); }));

        var q = PaletteQuery.Trim();
        PaletteItems.Clear();
        foreach (var i in items.Where(i => q.Length == 0 || (i.Label + " " + i.Kind).Contains(q, StringComparison.OrdinalIgnoreCase)).Take(40))
            PaletteItems.Add(i);
        if (q.Length >= 2)
            PaletteItems.Add(new($"Search all conversations for \"{q}\"", "search", () => { PaletteOpen = false; Search.Query = q; PageIndex = 3; }));
        PaletteSelected = PaletteItems.FirstOrDefault();
    }

    [RelayCommand]
    void RunPalette(PaletteItemVM? item)
    {
        item ??= PaletteSelected ?? PaletteItems.FirstOrDefault();
        if (item == null) return;
        PaletteOpen = false;
        item.Run();
    }

    [RelayCommand]
    void MovePalette(string delta)
    {
        if (PaletteItems.Count == 0) return;
        var i = PaletteSelected == null ? -1 : PaletteItems.IndexOf(PaletteSelected);
        i = Math.Clamp(i + int.Parse(delta, CultureInfo.InvariantCulture), 0, PaletteItems.Count - 1);
        PaletteSelected = PaletteItems[i];
    }

    void SetDefaultModel(string id, string name)
    {
        PaletteOpen = false;
        var prev = SettingsFile.GetString("model");
        if (string.Equals(prev, id, StringComparison.OrdinalIgnoreCase)) { Toast(name + " is already the default for new sessions.", true); return; }
        var r = SettingsFile.SetString(id, "model");
        if (!r.Ok) { Toast(r); return; }
        Toast($"New sessions will start on {name}. Running sessions keep their model.", true, () => { Toast(SettingsFile.SetString(prev, "model")); Refresh(); });
        Refresh();
        if (IsSettingsPage) Control.Load();
    }

    // ---------- new session ----------
    [RelayCommand]
    void OpenNew()
    {
        PaletteOpen = false;
        BellOpen = false;
        RecentFolders = Store.TopLevel().Where(s => s.Cwd != null && Directory.Exists(s.Cwd)).Select(s => s.Cwd!).Distinct().Take(8).ToList();
        if (RecentFolders.Count > 0 && (NewFolder == Paths.Home || !Directory.Exists(NewFolder))) NewFolder = RecentFolders[0];
        if (!RecentFolders.Contains(NewFolder)) RecentFolders = new List<string> { NewFolder }.Concat(RecentFolders).ToList();
        var opts = new List<StartOptionVM> { new("Your defaults", DefaultsSummary(), null) };
        opts.AddRange(Core.Presets.Load().Select(p => new StartOptionVM(p.Name, string.Join(" ", ClaudeCli.SessionArgs(p.Model, p.Effort, p.PermissionMode, null)), p)));
        StartOptions = opts;
        StartPick = opts[0];
        NewOpen = true;
    }

    [RelayCommand] void CloseNew() => NewOpen = false;

    [RelayCommand]
    async Task BrowseNewFolder()
    {
        var f = await PickFolderAsync("Folder for the new Claude session");
        if (f == null) return;
        if (!RecentFolders.Contains(f)) RecentFolders = new List<string> { f }.Concat(RecentFolders).ToList();
        NewFolder = f;
    }

    [RelayCommand]
    void StartNew()
    {
        if (!Directory.Exists(NewFolder)) { Toast("That folder doesn't exist.", false); return; }
        var p = StartPick?.Preset;
        NewOpen = false;
        Toast(Terminal.LaunchClaude(NewFolder, p == null ? new List<string>() : ClaudeCli.SessionArgs(p.Model, p.Effort, p.PermissionMode, null)));
    }

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
