using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Overseer.Core;

namespace Overseer.App.ViewModels;

/// <summary>A permission prompt waiting for an answer in Overseer.</summary>
public sealed partial class PendingVM : ObservableObject
{
    public PendingVM(PendingPermission p) => Update(p, null);

    public string Id { get; private set; } = "";
    public string SessionId { get; private set; } = "";
    public string? Cwd { get; private set; }
    public string? SessionFile { get; set; }
    public string Rule { get; private set; } = "";
    public DateTime Asked { get; private set; }

    [ObservableProperty] string title = "";
    [ObservableProperty] string project = "";
    [ObservableProperty] string ask = "";
    [ObservableProperty] string detail = "";
    [ObservableProperty] string reason = "";
    [ObservableProperty] string askedAgo = "";
    [ObservableProperty] bool denyOpen;
    [ObservableProperty] string denyNote = "";
    /// <summary>0 = this project (.claude/settings.local.json), 1 = all projects (~/.claude/settings.json).</summary>
    [ObservableProperty] int alwaysScope;

    public bool HasReason => Reason.Length > 0;
    public bool ShowButtons => !DenyOpen;
    public string RuleText => Redactor.Enabled ? "this rule" : Rule;
    public string Destination => AlwaysScope == 1 ? "userSettings" : "localSettings";
    public string[] Scopes { get; } = { "this project only", "all my projects" };

    partial void OnDenyOpenChanged(bool value) => OnPropertyChanged(nameof(ShowButtons));
    partial void OnReasonChanged(string value) => OnPropertyChanged(nameof(HasReason));

    public void Update(PendingPermission p, Session? s)
    {
        Id = p.Id; SessionId = p.SessionId; Cwd = p.Cwd; Rule = p.Rule; Asked = p.Asked;
        SessionFile = s?.FilePath;
        Title = Redactor.Title(s?.Title ?? "A Claude session", p.SessionId);
        Project = Redactor.Name(FolderName(p.Cwd ?? s?.Cwd));
        Ask = p.ToolName switch
        {
            "Bash" => "wants to run a command",
            "Edit" or "Write" or "MultiEdit" or "NotebookEdit" => "wants to change a file",
            "WebFetch" or "WebSearch" => "wants to use the web",
            _ => "wants to use " + Tools.Label(p.ToolName),
        };
        Detail = Redactor.Name(p.Detail);
        Reason = Redactor.Enabled ? "" : p.Reason ?? "";
        OnPropertyChanged(nameof(RuleText));
        Tick();
    }

    public void Tick() => AskedAgo = Ago(DateTime.Now - Asked);

    static string Ago(TimeSpan t) => t.TotalSeconds < 60 ? Math.Max(1, (int)t.TotalSeconds) + " s" : (int)t.TotalMinutes + " min";

    public static string FolderName(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var t = path.TrimEnd('/', '\\');
        var i = t.LastIndexOfAny(new[] { '/', '\\' });
        return i >= 0 ? t[(i + 1)..] : t;
    }
}

/// <summary>One session, as shown in the sidebar, the overview table and the mini window.</summary>
public sealed partial class SessionItemVM : ObservableObject
{
    public string FilePath { get; }
    public SessionItemVM(string file) => FilePath = file;

    public string Id { get; private set; } = "";
    public SessionState State { get; private set; }

    [ObservableProperty] string title = "";
    [ObservableProperty] string project = "";
    [ObservableProperty] string age = "";
    [ObservableProperty] string stateText = "";
    [ObservableProperty] IBrush stateBrush = Brushes.Gray;
    [ObservableProperty] IBrush stateInk = Brushes.Gray;
    [ObservableProperty] IBrush rowBg = Brushes.Transparent;
    [ObservableProperty] bool needsYou;
    [ObservableProperty] bool isDone;
    [ObservableProperty] bool isLive;
    [ObservableProperty] string note = "";
    [ObservableProperty] double contextFraction;
    [ObservableProperty] string contextText = "";
    [ObservableProperty] string tokens = "";
    [ObservableProperty] string tooltip = "";
    [ObservableProperty] PendingVM? pending;

    public bool HasPending => Pending != null;
    partial void OnPendingChanged(PendingVM? value) => OnPropertyChanged(nameof(HasPending));

    public void Update(Session s, PendingVM? pending)
    {
        Id = s.Id;
        Pending = pending;
        var st = pending != null && s.State != SessionState.Ended ? SessionState.NeedsPermission : s.State;
        State = st;
        Title = Redactor.Title(s.Title, s.Id);
        Project = Redactor.Name(PendingVM.FolderName(s.Cwd));
        Age = Short(DateTime.Now - s.Last);
        StateText = Res.StateText(st);
        StateBrush = Res.Brush(Res.StateKey(st));
        StateInk = Res.Brush(Res.StateInkKey(st));
        NeedsYou = st == SessionState.NeedsPermission;
        RowBg = NeedsYou ? Res.Brush("NeedBg") : Brushes.Transparent;
        IsDone = Res.IsDone(st);
        IsLive = s.IsLive;
        Note = NoteFor(s, st, pending);
        double frac = s.Status?.ContextUsedPercent is { } p ? p / 100.0 : s.ContextWindow > 0 ? (double)s.LastContext / s.ContextWindow : 0;
        ContextFraction = Math.Clamp(frac, 0, 1);
        ContextText = Math.Round(frac * 100).ToString(CultureInfo.InvariantCulture) + "%";
        Tokens = Text.Tokens(s.TotalTokens);
        Tooltip = Title + (s.Cwd != null && !Redactor.Enabled ? "\n" + s.Cwd : "") + "\n" + StateText + " · " + (s.ModelName ?? Text.PrettyModel(s.Model));
    }

    static string NoteFor(Session s, SessionState st, PendingVM? pending)
    {
        var h = s.Hook;
        switch (st)
        {
            case SessionState.NeedsPermission:
                return pending != null ? Cap(pending.Ask) + (pending.Detail.Length > 0 ? ": " + pending.Detail : "")
                    : Redactor.Text(h?.Message ?? "Waiting for permission in the terminal");
            case SessionState.Working when h?.RunningTool != null && h.Updated > DateTime.Now.AddMinutes(-10):
                return h.RunningTool + (h.RunningDetail != null ? " · " + Redactor.Name(h.RunningDetail) : "");
            case SessionState.Working:
                var last = s.Events.LastOrDefault();
                return last?.Kind == EventKind.Tool ? last.Name + (last.Detail is { Length: > 0 } d ? " · " + Redactor.Name(d) : "")
                    : last?.Kind == EventKind.Prompt ? "Thinking about your message" : "Writing a reply";
            case SessionState.Idle:
                return "Finished. Waiting for your next message";
            default:
                var tool = s.Events.LastOrDefault(e => e.Kind == EventKind.Tool);
                return "Last active " + Text.Ago(s.Last) + (tool != null ? " · last used " + tool.Name : "");
        }
    }

    static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    public static string Short(TimeSpan t) =>
        t.TotalMinutes < 1 ? "now" : t.TotalHours < 1 ? (int)t.TotalMinutes + " min" : t.TotalDays < 1 ? (int)t.TotalHours + " h" : (int)t.TotalDays + " d";
}
