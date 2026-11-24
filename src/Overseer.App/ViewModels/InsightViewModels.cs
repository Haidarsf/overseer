using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Overseer.Core;

namespace Overseer.App.ViewModels;

public sealed record StatCardVM(string Title, string Icon, string IconKey, string Big, string Unit, string Line1, string Line2, List<ColumnVM> Spark, string? Tip)
{
    public Geometry? IconGeometry => Res.Icon(Icon);
    public IBrush IconBrush => Res.Brush(IconKey);
    public IBrush IconBg => Res.Tint(IconKey, 0x26);
}

public sealed record LimitVM(string Title, double Fraction, string Percent, string Resets, IBrush Brush);

// ================= Overview =================
public sealed partial class OverviewViewModel : ObservableObject
{
    readonly MainViewModel main;
    public OverviewViewModel(MainViewModel main) => this.main = main;

    [ObservableProperty] List<StatCardVM> cards = new();
    [ObservableProperty] List<LimitVM> limits = new();
    [ObservableProperty] bool hasLimits;
    [ObservableProperty] string limitsNote = "";
    [ObservableProperty] List<ColumnVM> hours = new();
    [ObservableProperty] string hoursTotal = "";
    [ObservableProperty] string hoursPeak = "";
    [ObservableProperty] List<BarVM> models = new();
    [ObservableProperty] List<BarVM> tools = new();
    [ObservableProperty] List<BarVM> mcp = new();
    [ObservableProperty] List<BarVM> skills = new();
    [ObservableProperty] List<ChipVM> installedSkills = new();
    [ObservableProperty] string installedSkillsTitle = "";

    public void Update()
    {
        var now = DateTime.Now;
        var all = main.Store.AllUsage();
        var h5 = all.Where(u => u.Time > now.AddHours(-5)).ToList();
        var d7 = all.Where(u => u.Time > now.AddDays(-7)).ToList();
        var today = all.Where(u => u.Time.Date == now.Date).ToList();
        var top = main.Store.TopLevel();

        List<ColumnVM> Spark(long[] v, string key)
        {
            var mx = Math.Max(1, v.Max());
            return v.Select((x, i) => new ColumnVM(x == 0 ? 3 : Math.Max(5, 32.0 * x / mx), i == v.Length - 1 ? Res.Brush(key) : Res.Tint(key, 0x90), Text.Tokens(x) + " tokens", null)).ToList();
        }

        var ages = h5.Count > 0 ? "oldest call ages out in " + Text.Duration(h5.Min(u => u.Time).AddHours(5) - now) : "no calls in this window";
        int live = top.Count(s => s.IsLive), working = top.Count(s => s.State == SessionState.Working);
        Cards = new()
        {
            new("Last 5 hours", "Clock", "Accent", Text.Tokens(h5.Sum(u => u.Billable)), "tokens · excl. cache reads", $"{h5.Count} model calls", ages,
                Spark(Reports.Buckets(all, now.AddHours(-5), TimeSpan.FromMinutes(30), 10), "Accent"), "Rolling 5-hour window, the span Claude's session limits use."),
            new("Today", "Chart", "Blue", Text.Tokens(today.Sum(u => u.Billable)), "tokens · excl. cache reads", $"{today.Count} model calls", Text.Tokens(today.Sum(u => u.Output)) + " output tokens",
                Spark(Reports.Buckets(all, now.Date, TimeSpan.FromHours(1), Math.Max(1, now.Hour + 1)), "Blue"), null),
            new("Last 7 days", "Chart", "Purple", Text.Tokens(d7.Sum(u => u.Billable)), "tokens · excl. cache reads", $"{d7.Count} model calls", d7.Select(u => u.SessionId).Distinct().Count() + " sessions",
                Spark(Reports.Buckets(all, now.Date.AddDays(-6), TimeSpan.FromDays(1), 7), "Purple"), "Rolling 7-day window, the span Claude's weekly limits use."),
            new("Right now", "Pulse", "Green", live.ToString(CultureInfo.InvariantCulture), "live sessions", $"{working} working · {live - working} idle", top.Count + " sessions on disk", new(), null),
        };

        var lim = main.Store.LatestLimits();
        HasLimits = lim != null;
        if (lim != null)
        {
            LimitVM L(string t, double? pct, DateTime? resets) =>
                new(t, (pct ?? 0) / 100, Math.Round(pct ?? 0) + "%", resets is { } r ? "resets " + r.ToString(r.Date == now.Date ? "HH:mm" : "ddd HH:mm", CultureInfo.InvariantCulture) : "",
                    Res.Brush((pct ?? 0) >= 90 ? "Red" : (pct ?? 0) >= 70 ? "Yellow" : "Accent"));
            Limits = new() { L("5-hour limit", lim.FiveHourUsedPercent, lim.FiveHourResetsAt), L("Weekly limit", lim.WeeklyUsedPercent, lim.WeeklyResetsAt) };
            LimitsNote = "Exact, reported by Claude Code's status line " + Text.Ago(lim.Updated) + ".";
        }
        else LimitsNote = "Your exact plan limits appear here once Overseer's status line is on (Control → Live data). It only runs in terminal sessions.";

        var start = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0).AddHours(-23);
        var buckets = new long[24]; var calls = new int[24];
        foreach (var u in all)
        {
            if (u.Time < start) continue;
            var i = (int)(u.Time - start).TotalHours;
            if (i is < 0 or > 23) continue;
            buckets[i] += u.Billable; calls[i]++;
        }
        var mx = Math.Max(1, buckets.Max());
        Hours = buckets.Select((b, i) => new ColumnVM(b == 0 ? 3 : Math.Max(6, 150.0 * b / mx),
            b == 0 ? Res.Brush("Line") : i == 23 ? Res.Brush("Accent2") : Res.Brush("Accent"),
            $"{start.AddHours(i):HH}:00 – {start.AddHours(i + 1):HH}:00\n{Text.Tokens(b)} tokens · {calls[i]} model calls",
            i == 23 ? "now" : i % 3 == 2 ? start.AddHours(i).ToString("HH:00", CultureInfo.InvariantCulture) : null)).ToList();
        HoursTotal = Text.Tokens(buckets.Sum());
        HoursPeak = "peak " + Text.Tokens(mx) + "/h";

        Models = BarVM.From(d7.GroupBy(u => Text.PrettyModel(u.Model)).Select(g => new KeyValuePair<string, long>(g.Key, g.Count())), "Accent", 8);
        var tools = new Dictionary<string, long>(); var mcp = new Dictionary<string, long>(); var skills = new Dictionary<string, long>();
        foreach (var s in main.Store.All.Where(x => x.Last > now.AddDays(-7)))
        {
            foreach (var kv in s.Tools) tools[kv.Key] = tools.GetValueOrDefault(kv.Key) + kv.Value;
            foreach (var kv in s.Mcp) mcp[kv.Key] = mcp.GetValueOrDefault(kv.Key) + kv.Value;
            foreach (var kv in s.Skills) skills[kv.Key] = skills.GetValueOrDefault(kv.Key) + kv.Value;
        }
        Tools = BarVM.From(tools, "Blue", 14, true, Core.Tools.Label);
        Mcp = BarVM.From(mcp, "Purple", 10);
        Skills = BarVM.From(skills, "Accent2", 10);
        InstalledSkills = main.Store.InstalledSkills.Select(x => new ChipVM(x.Name, Res.Brush(x.Source == "personal" ? "Green" : "Fg2"), Res.Icon("Bolt"), x.Description)).ToList();
        InstalledSkillsTitle = "Installed skills · " + InstalledSkills.Count;
    }
}

// ================= Reports =================
public sealed record ReportRowVM(string Key, string Calls, string Billable, string Output, string CacheRead, string Cost, double Fraction, IBrush Brush);

public sealed partial class ReportsViewModel : ObservableObject
{
    readonly MainViewModel main;
    List<ReportRow> rows = new();
    public ReportsViewModel(MainViewModel main)
    {
        this.main = main;
        range = Ranges[1];
        group = Groups[0];
    }

    public Option[] Ranges { get; } = Options.Of(("7 days", "7"), ("30 days", "30"), ("90 days", "90"), ("All time", "0"));
    public Option[] Groups { get; } = Options.Of(("By day", "day"), ("By project", "project"), ("By model", "model"), ("By session", "session"));

    [ObservableProperty] Option range;
    [ObservableProperty] Option group;
    [ObservableProperty] List<ReportRowVM> items = new();
    [ObservableProperty] string totalTokens = "";
    [ObservableProperty] string totalCalls = "";
    [ObservableProperty] string totalCost = "";
    [ObservableProperty] string keyHeader = "Day";

    partial void OnRangeChanged(Option value) => Update();
    partial void OnGroupChanged(Option value) => Update();

    public void Update()
    {
        var days = int.Parse(Range.Value ?? "30", CultureInfo.InvariantCulture);
        var from = days == 0 ? DateTime.MinValue : DateTime.Now.Date.AddDays(-days + 1);
        var by = Group.Value switch { "project" => ReportGroup.Project, "model" => ReportGroup.Model, "session" => ReportGroup.Session, _ => ReportGroup.Day };
        KeyHeader = Group.Label.Replace("By ", "").ToUpperInvariant();
        var names = main.Store.TopLevel().ToDictionary(s => s.Id, s => s.Title);
        rows = Reports.Build(main.Store.AllUsage(), from, DateTime.Now.AddMinutes(1), by, main.Prices, id => names.GetValueOrDefault(id) ?? id);
        var mx = Math.Max(1, rows.Count == 0 ? 1 : rows.Max(r => r.Billable));
        Items = rows.Select(r => new ReportRowVM(
            by == ReportGroup.Project ? Redactor.Text(r.Key) : by == ReportGroup.Session ? Redactor.Text(r.Key) : r.Key,
            r.Calls.ToString("N0", CultureInfo.InvariantCulture), Text.Tokens(r.Billable), Text.Tokens(r.Output), Text.Tokens(r.CacheRead),
            "$" + r.Cost.ToString("0.00", CultureInfo.InvariantCulture), (double)r.Billable / mx, Res.Brush("Accent"))).ToList();
        TotalTokens = Text.Tokens(rows.Sum(r => r.Billable));
        TotalCalls = rows.Sum(r => r.Calls).ToString("N0", CultureInfo.InvariantCulture);
        TotalCost = "$" + rows.Sum(r => r.Cost).ToString("0.00", CultureInfo.InvariantCulture);
    }

    [RelayCommand]
    async Task ExportCsv()
    {
        var path = await main.SaveFileAsync("Export report", $"overseer-{Group.Value}-{DateTime.Now:yyyyMMdd}.csv", "csv");
        if (path == null) return;
        try { await File.WriteAllTextAsync(path, Reports.ToCsv(rows, Group.Value ?? "key")); main.Toast("Exported " + Path.GetFileName(path), true); }
        catch (Exception ex) { main.Toast("Couldn't export: " + ex.Message, false); }
    }

    [RelayCommand] void EditPrices() { PriceTable.Load(); ProcessControl.Open(Paths.PricesFile); }
    [RelayCommand] void ReloadPrices() { main.ReloadPrices(); Update(); main.Toast("Prices reloaded", true); }
}

// ================= Search =================
public sealed record SearchHitVM(string Title, string When, string Role, string Snippet, string FilePath, IBrush RoleBrush);

public sealed partial class SearchViewModel : ObservableObject
{
    readonly MainViewModel main;
    CancellationTokenSource? cts;
    DispatcherTimer? debounce;
    public SearchViewModel(MainViewModel main) => this.main = main;

    public ObservableCollection<SearchHitVM> Results { get; } = new();
    [ObservableProperty] string query = "";
    [ObservableProperty] string status = "Search every conversation on this machine: prompts, replies and tool calls.";
    [ObservableProperty] bool busy;

    partial void OnQueryChanged(string value)
    {
        debounce?.Stop();
        debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        debounce.Tick += (_, _) => { debounce?.Stop(); _ = Run(); };
        debounce.Start();
    }

    async Task Run()
    {
        cts?.Cancel();
        var q = Query.Trim();
        Results.Clear();
        if (q.Length < 2) { Status = "Type at least two characters."; return; }
        cts = new CancellationTokenSource();
        var token = cts.Token;
        Busy = true; Status = "Searching…";
        var sessions = main.Store.All.ToList();
        var hits = await Task.Run(() => TranscriptSearch.Search(sessions, q, 300, token), token).ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : new());
        if (token.IsCancellationRequested) return;
        Busy = false;
        foreach (var h in hits)
        {
            var parent = h.Session.IsSubagent ? main.Store.All.FirstOrDefault(x => x.Id == h.Session.ParentId && !x.IsSubagent) ?? h.Session : h.Session;
            var key = h.Role switch { "You" => "Accent", "Claude" => "Green", _ => "Blue" };
            Results.Add(new SearchHitVM(Redactor.Text(parent.Title), h.Time.ToString("MMM d, HH:mm", CultureInfo.InvariantCulture), h.Role,
                h.Role == "You" ? Redactor.Prompt(h.Snippet) : Redactor.Text(h.Snippet), parent.FilePath, Res.Brush(key)));
        }
        Status = hits.Count == 0 ? "No matches." : hits.Count >= 300 ? "Showing the first 300 matches." : $"{hits.Count} matches.";
    }

    [RelayCommand] void Open(SearchHitVM hit) => main.SelectSession(hit.FilePath, true);
}
