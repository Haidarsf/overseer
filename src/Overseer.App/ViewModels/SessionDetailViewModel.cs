using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Overseer.Core;

namespace Overseer.App.ViewModels;

public sealed record TimelineItemVM(string Time, string Name, string Detail, IBrush Brush, IBrush Tint, Geometry? Icon, bool Mono, bool HasDetail, bool ShowLine, string? DayHeader)
{
    public bool HasDayHeader => DayHeader != null;
}

public sealed record SubagentVM(string Title, string Meta, string FilePath);
public sealed record FileVM(string Name, string Path, string Count);
public sealed record ConversationVM(string Who, string Time, string Text, IBrush Brush, bool IsTool);

public sealed partial class SessionDetailViewModel : ObservableObject
{
    readonly MainViewModel main;
    readonly Session s;
    int lastEventCount = -1;
    DateTime lastLast;

    public SessionDetailViewModel(MainViewModel main, Session s)
    {
        this.main = main;
        this.s = s;
        Update(true);
    }

    public ObservableCollection<TimelineItemVM> Timeline { get; } = new();
    public ObservableCollection<ConversationVM> Conversation { get; } = new();
    public Option[] ForkModels { get; } = Options.Of(("Fable", "fable"), ("Opus", "opus"), ("Sonnet", "sonnet"), ("Haiku", "haiku"));

    [ObservableProperty] string title = "";
    [ObservableProperty] string cwd = "";
    [ObservableProperty] List<ChipVM> chips = new();
    [ObservableProperty] string stateText = "";
    [ObservableProperty] IBrush stateBrush = Brushes.Gray;
    [ObservableProperty] IBrush heroBg = Brushes.Transparent;
    [ObservableProperty] string heroHead = "";
    [ObservableProperty] string heroDetail = "";
    [ObservableProperty] Geometry? heroIcon;
    [ObservableProperty] bool isWorking;
    [ObservableProperty] string modelName = "";
    [ObservableProperty] string modelId = "";
    [ObservableProperty] string sourceNote = "";

    [ObservableProperty] double contextFraction;
    [ObservableProperty] string contextPercent = "";
    [ObservableProperty] string contextUsed = "";
    [ObservableProperty] string contextOf = "";
    [ObservableProperty] string contextLeft = "";
    [ObservableProperty] IBrush contextBrush = Brushes.Gray;
    [ObservableProperty] string contextSource = "";

    [ObservableProperty] string totalTokens = "";
    [ObservableProperty] List<BarVM> tokenParts = new();
    [ObservableProperty] string costText = "";

    [ObservableProperty] string toolCalls = "";
    [ObservableProperty] string calls = "";
    [ObservableProperty] string prompts = "";
    [ObservableProperty] string errors = "";
    [ObservableProperty] IBrush errorsBrush = Brushes.Gray;

    [ObservableProperty] string span = "";
    [ObservableProperty] string started = "";
    [ObservableProperty] string lastActivity = "";

    [ObservableProperty] List<BarVM> toolBars = new();
    [ObservableProperty] List<BarVM> mcpBars = new();
    [ObservableProperty] List<string> skillsInvoked = new();
    [ObservableProperty] List<string> skillsAvailable = new();
    [ObservableProperty] string skillsAvailableTitle = "";
    [ObservableProperty] List<SubagentVM> subagents = new();
    [ObservableProperty] List<FileVM> filesChanged = new();
    [ObservableProperty] bool isLive;
    [ObservableProperty] bool showConversation;
    [ObservableProperty] Option? forkPick;

    public bool HasTools => ToolBars.Count > 0;
    public bool HasMcp => McpBars.Count > 0;
    public bool HasSkills => SkillsInvoked.Count > 0;
    public bool HasSubagents => Subagents.Count > 0;
    public bool HasFiles => FilesChanged.Count > 0;

    public void Update(bool force)
    {
        if (!force && s.Events.Count == lastEventCount && s.Last == lastLast && s.Hook?.Updated == null) { UpdateHero(); return; }
        lastEventCount = s.Events.Count;
        lastLast = s.Last;

        Title = Redactor.Text(s.Title);
        Cwd = Redactor.Text(s.Cwd ?? "");
        IsLive = s.IsLive;
        var chips = new List<ChipVM>();
        if (s.Version != null) chips.Add(new ChipVM("Claude Code " + s.Version, Res.Brush("Fg2"), Res.Icon("Sliders")));
        if (s.Entrypoint != null) chips.Add(new ChipVM(s.Entrypoint, Res.Brush("Fg2"), Res.Icon("Plug")));
        if (s.PermissionMode != null) chips.Add(new ChipVM("permissions: " + s.PermissionMode, Res.Brush("Yellow"), Res.Icon("Shield")));
        if (s.IsLive) chips.Add(new ChipVM("pid " + s.Pid, Res.Brush("Fg2"), Res.Icon("Pulse")));
        if (s.Cwd != null) chips.Add(new ChipVM(Redactor.Text(Paths.Short(s.Cwd)), Res.Brush("Fg2"), Res.Icon("Folder"), Redactor.Text(s.Cwd)));
        Chips = chips;

        UpdateHero();
        ModelName = s.Status?.ModelName ?? s.ModelName ?? Text.PrettyModel(s.Model);
        ModelId = s.Status?.ModelId ?? s.Model ?? "";

        // context
        double frac = s.Status?.ContextUsedPercent is { } p ? p / 100.0 : s.ContextWindow > 0 ? (double)s.LastContext / s.ContextWindow : 0;
        ContextFraction = frac;
        ContextPercent = Math.Round(frac * 100).ToString(CultureInfo.InvariantCulture) + "%";
        ContextUsed = Text.Tokens(s.LastContext);
        ContextOf = "of " + Text.Tokens(s.ContextWindow) + " tokens";
        ContextLeft = Text.Tokens(Math.Max(0, s.ContextWindow - s.LastContext)) + " left";
        ContextBrush = Res.Brush(frac > 0.85 ? "Red" : frac > 0.6 ? "Yellow" : "Accent");
        ContextSource = s.ContextWindowIsExact ? "exact, from the status line" : "estimated from the transcript";

        // tokens
        TotalTokens = Text.Tokens(s.TotalTokens);
        var max = Math.Sqrt(Math.Max(1, new[] { s.Output, s.Input, s.CacheWrite, s.CacheRead }.Max()));
        BarVM Part(string label, long v, string key) => new(label, Text.Tokens(v), Math.Max(0.02, Math.Sqrt(v) / max), Res.Brush(key), null, $"{label}: {v:N0} tokens");
        TokenParts = new() { Part("Output", s.Output, "Accent"), Part("Input", s.Input, "Fg2"), Part("Cache write", s.CacheWrite, "Blue"), Part("Cache read", s.CacheRead, "Purple") };
        var cost = s.Usages.Sum(main.Prices.Cost) + main.Store.SubagentsOf(s.Id).SelectMany(x => x.Usages).Sum(main.Prices.Cost);
        CostText = cost > 0 ? "≈ $" + cost.ToString("0.00", CultureInfo.InvariantCulture) + " API-equivalent" : "";

        ToolCalls = s.ToolCalls.ToString(CultureInfo.InvariantCulture);
        Calls = s.Calls.ToString(CultureInfo.InvariantCulture);
        Prompts = s.Prompts.ToString(CultureInfo.InvariantCulture);
        Errors = s.Errors.ToString(CultureInfo.InvariantCulture);
        ErrorsBrush = Res.Brush(s.Errors > 0 ? "Red" : "Faint");
        Span = Text.Duration(s.Last - s.First);
        Started = s.First == DateTime.MinValue ? "—" : s.First.ToString("MMM d, HH:mm", CultureInfo.InvariantCulture);
        LastActivity = Text.Ago(s.Last);

        ToolBars = BarVM.From(s.Tools.Select(kv => new KeyValuePair<string, long>(kv.Key, kv.Value)), "Blue", 12, true, Tools.Label);
        McpBars = BarVM.From(s.Mcp.Select(kv => new KeyValuePair<string, long>(kv.Key, kv.Value)), "Purple", 8);
        SkillsInvoked = s.Skills.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}  ×{kv.Value}").ToList();
        SkillsAvailable = s.SkillList.ToList();
        SkillsAvailableTitle = "Skills available · " + s.SkillList.Count;
        Subagents = main.Store.SubagentsOf(s.Id).Select(x => new SubagentVM(
            Redactor.Text(x.AgentDescription ?? x.Id), $"{x.ToolCalls} tool calls · {Text.Tokens(x.TotalTokens)} tokens · {Text.Ago(x.Last)}", x.FilePath)).ToList();
        FilesChanged = s.FilesChanged.OrderByDescending(kv => kv.Value).Take(40)
            .Select(kv => new FileVM(Path.GetFileName(kv.Key), Redactor.Text(kv.Key), "×" + kv.Value)).ToList();
        foreach (var n in new[] { nameof(HasTools), nameof(HasMcp), nameof(HasSkills), nameof(HasSubagents), nameof(HasFiles) }) OnPropertyChanged(n);

        // timeline, newest first
        Timeline.Clear();
        var evs = s.Events.AsEnumerable().Reverse().Take(60).ToList();
        DateTime? day = null;
        for (int i = 0; i < evs.Count; i++)
        {
            var e = evs[i];
            string? header = day != null && day.Value.Date != e.Time.Date ? e.Time.ToString("dddd, MMM d", CultureInfo.InvariantCulture) : null;
            day = e.Time;
            var key = e.Kind switch { EventKind.Tool => "Blue", EventKind.Prompt => "Accent", EventKind.Error => "Red", _ => "Green" };
            var icon = e.Kind switch { EventKind.Tool => Res.ToolIcon(e.Tool), EventKind.Prompt => "Person", EventKind.Error => "Error", _ => "Chat" };
            var detail = e.Kind == EventKind.Prompt ? Redactor.Prompt(e.Detail) : Redactor.Text(e.Detail);
            Timeline.Add(new TimelineItemVM(e.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture), e.Name, detail, Res.Brush(key), Res.Tint(key, 0x26),
                Res.Icon(icon), e.Kind == EventKind.Tool && (detail.Contains(":\\") || detail.StartsWith('/')), detail.Length > 0, i < evs.Count - 1, header));
        }
        if (ShowConversation) LoadConversation();
    }

    void UpdateHero()
    {
        var st = s.State;
        StateText = Res.StateText(st);
        var key = Res.StateKey(st);
        StateBrush = Res.Brush(key);
        HeroBg = Res.Tint(key, 0x22);
        IsWorking = st is SessionState.Working or SessionState.NeedsPermission;
        var last = s.Events.LastOrDefault();
        var lastTool = s.Events.LastOrDefault(e => e.Kind == EventKind.Tool);
        var h = s.Hook;
        SourceNote = h != null && h.Updated > DateTime.Now.AddMinutes(-30) ? "live from hooks" : "from the transcript";
        string icon;
        switch (st)
        {
            case SessionState.NeedsPermission:
                HeroHead = "Waiting for your permission";
                HeroDetail = Redactor.Text(h?.Message ?? "Claude wants to run a tool. Approve or deny it in the session.");
                icon = "Shield";
                break;
            case SessionState.Working when h?.RunningTool != null && h.Updated > DateTime.Now.AddMinutes(-10):
                HeroHead = "Running " + h.RunningTool;
                HeroDetail = Redactor.Text(h.RunningDetail ?? "Waiting for the tool to finish…");
                icon = Res.ToolIcon(h.RunningTool);
                break;
            case SessionState.Working:
                if (last?.Kind == EventKind.Tool) { HeroHead = "Running " + last.Name; HeroDetail = Redactor.Text(last.Detail is { Length: > 0 } d ? d : "Waiting for the tool to finish…"); icon = Res.ToolIcon(last.Tool); }
                else if (last?.Kind == EventKind.Prompt) { HeroHead = "Thinking"; HeroDetail = "Reading your message and planning the next step…"; icon = "Bulb"; }
                else if (last?.Kind == EventKind.Error) { HeroHead = "Recovering"; HeroDetail = "A tool returned an error: " + Redactor.Text(last.Detail); icon = "Error"; }
                else { HeroHead = "Writing"; HeroDetail = Redactor.Text(last?.Detail ?? "Composing a reply…"); icon = "Chat"; }
                break;
            case SessionState.Idle:
                HeroHead = "Waiting for you";
                HeroDetail = lastTool != null ? $"Last used {lastTool.Name} {Text.Ago(lastTool.Time)}" : "Session is open and idle";
                icon = "Pause";
                break;
            default:
                HeroHead = st == SessionState.Active ? "Recently active" : "Session ended";
                HeroDetail = "Last activity " + Text.Ago(s.Last) + (lastTool != null ? " · last tool " + lastTool.Name : "");
                icon = st == SessionState.Active ? "Pulse" : "Stop";
                break;
        }
        HeroIcon = Res.Icon(icon);
    }

    // ---------- actions ----------
    [RelayCommand]
    async Task Resume()
    {
        if (s.IsLive && !await main.ConfirmAsync("This session is still open somewhere else. Resume it in a terminal anyway?", "Resume")) return;
        main.Toast(Terminal.LaunchClaude(s.Cwd, new[] { "--resume", s.Id }));
    }

    [RelayCommand] void OpenDesktop() => main.Toast(ClaudeCli.OpenInDesktop(s.Id, s.Cwd));
    [RelayCommand] void OpenFolder() { if (s.Cwd != null) ProcessControl.Open(s.Cwd); }
    [RelayCommand] void ShowTranscript() => ProcessControl.Reveal(s.FilePath);

    [RelayCommand]
    async Task Stop()
    {
        if (!s.IsLive || s.Pid <= 0) return;
        if (!await main.ConfirmAsync($"Stop \"{Title}\"?\n\nOverseer asks the Claude process (pid {s.Pid}) to stop, and only force-closes it if it doesn't respond. The conversation stays on disk and can be resumed.", "Stop session")) return;
        main.Toast("Stopping…", true);
        main.Toast(await ProcessControl.StopGracefully(s.Pid, TimeSpan.FromSeconds(6)));
    }

    partial void OnForkPickChanged(Option? value)
    {
        if (value?.Value == null) return;
        main.Toast(Terminal.LaunchClaude(s.Cwd, new[] { "--resume", s.Id, "--fork-session", "--model", value.Value }));
        ForkPick = null;
    }

    [RelayCommand]
    void LoadConversation()
    {
        ShowConversation = true;
        Conversation.Clear();
        foreach (var c in Core.Conversation.Read(s.FilePath, 400))
        {
            var key = c.Role switch { "You" => "Accent", "Claude" => "Green", _ => "Blue" };
            var text = c.Role == "You" ? Redactor.Prompt(c.Text) : Redactor.Text(c.Text);
            Conversation.Add(new ConversationVM(c.Tool ?? c.Role, c.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture), text.Length > 4000 ? text[..4000] + "…" : text, Res.Brush(key), c.Role == "Tool"));
        }
    }

    [RelayCommand] void HideConversation() { ShowConversation = false; Conversation.Clear(); }
    [RelayCommand] void OpenFile(FileVM f) { if (File.Exists(f.Path)) ProcessControl.Reveal(f.Path); }
    [RelayCommand] void OpenSubagent(SubagentVM a) => ProcessControl.Reveal(a.FilePath);
}
