using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Overseer.Core;

namespace Overseer.App.ViewModels;

public sealed record UsedByVM(string Name, string Share, double Fraction);

// ================= Overview =================
public sealed partial class OverviewViewModel : ObservableObject
{
    readonly MainViewModel main;
    public OverviewViewModel(MainViewModel main)
    {
        this.main = main;
        window = Options.Pick(Windows, main.Config.UsageWindow) ?? Windows[0];
    }

    public Option[] Windows { get; } = Options.Of(("5 hours", "5h"), ("This week", "week"));
    [ObservableProperty] Option window;

    // plan usage dial
    [ObservableProperty] double usageFraction;
    [ObservableProperty] string usagePercent = "";
    [ObservableProperty] string usageLabel = "";
    [ObservableProperty] string usageReset = "";
    [ObservableProperty] string usageNote = "";
    [ObservableProperty] IBrush usageBrush = Brushes.Gray;
    [ObservableProperty] List<UsedByVM> usedBy = new();
    [ObservableProperty] string usedByTitle = "";

    // activity
    [ObservableProperty] List<ColumnVM> hours = new();
    [ObservableProperty] string todayLine = "";
    [ObservableProperty] List<BarVM> models = new();
    [ObservableProperty] List<BarVM> tools = new();
    [ObservableProperty] List<BarVM> skills = new();

    /// <summary>One line for the mini window: "46% of your 5 hours · resets 18:09".</summary>
    public string MiniLine => UsagePercent + " " + UsageLabel + (UsageReset.Length > 0 ? " · " + UsageReset : "");

    partial void OnWindowChanged(Option value)
    {
        main.Config.UsageWindow = value.Value ?? "5h";
        main.Config.Save();
        UpdateUsage();
    }

    /// <summary>The dial and its "used by" list. Cheap, so it also runs while the overview isn't showing (the mini window uses it).</summary>
    public void UpdateUsage()
    {
        var now = DateTime.Now;
        var week = Window.Value == "week";
        var from = week ? now.AddDays(-7) : now.AddHours(-5);
        var lim = main.Store.LatestLimits();
        double? pct = week ? lim?.WeeklyUsedPercent : lim?.FiveHourUsedPercent;
        DateTime? resets = week ? lim?.WeeklyResetsAt : lim?.FiveHourResetsAt;
        var usage = main.Store.AllUsage().Where(u => u.Time > from).ToList();

        if (pct is { } p)
        {
            UsageFraction = p / 100;
            UsagePercent = Math.Round(p).ToString(CultureInfo.InvariantCulture) + "%";
            UsageLabel = week ? "of this week" : "of your 5 hours";
            UsageReset = resets is { } r ? "resets " + (r.Date == now.Date ? "today at " + r.ToString("HH:mm", CultureInfo.InvariantCulture) : r.ToString("dddd 'at' HH:mm", CultureInfo.InvariantCulture)) : "";
            UsageNote = "Exact, from Claude Code's status line " + Text.Ago(lim!.Updated) + ".";
            UsageBrush = Res.Brush(p >= 90 ? "Red" : p >= 70 ? "Accent" : week ? "Green" : "Fg");
        }
        else
        {
            var budget = week ? main.Config.WeeklyTokenBudget : main.Config.FiveHourTokenBudget;
            var used = usage.Sum(u => u.Billable);
            UsageFraction = budget > 0 ? Math.Min(1, (double)used / budget) : 0;
            UsagePercent = Text.Tokens(used);
            UsageLabel = week ? "tokens this week" : "tokens in 5 hours";
            UsageReset = budget > 0 ? Math.Round(100.0 * used / budget) + "% of your own budget" : "";
            UsageNote = "Your exact plan limits show here once the status line is on (Settings → Live data). It only runs in terminal sessions.";
            UsageBrush = Res.Brush(week ? "Green" : "Fg");
        }

        // which sessions used it: share of tokens in the window (subagents count toward their parent)
        var all = main.Store.All.ToList();
        string Parent(string id)
        {
            var s = all.FirstOrDefault(x => x.Id == id);
            return s is { IsSubagent: true, ParentId: { } pid } ? pid : id;
        }
        var top = main.Store.TopLevel().ToDictionary(s => s.Id);
        var groups = usage.GroupBy(u => Parent(u.SessionId)).Select(g => (Id: g.Key, Tokens: g.Sum(u => u.Billable))).Where(g => g.Tokens > 0).ToList();
        if (main.HideDone) groups = groups.Where(g => top.TryGetValue(g.Id, out var s) && !Res.IsDone(s.State)).ToList();
        var total = Math.Max(1, groups.Sum(g => g.Tokens));
        UsedBy = groups.OrderByDescending(g => g.Tokens).Take(6).Select(g =>
        {
            var name = top.TryGetValue(g.Id, out var s) ? Redactor.Title(s.Title, s.Id) : "Other sessions";
            var f = (double)g.Tokens / total;
            return new UsedByVM(name, Math.Round(f * 100).ToString(CultureInfo.InvariantCulture) + "%", f);
        }).ToList();
        UsedByTitle = UsedBy.Count == 0 ? "No Claude use in this window yet." : "Used by (share of tokens in this window)";
        OnPropertyChanged(nameof(MiniLine));
    }

    public void Update()
    {
        UpdateUsage();
        var now = DateTime.Now;
        var all = main.Store.AllUsage();
        var today = all.Where(u => u.Time.Date == now.Date).ToList();
        var d7 = all.Where(u => u.Time > now.AddDays(-7)).ToList();

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
        Hours = buckets.Select((b, i) => new ColumnVM(b == 0 ? 2 : Math.Max(4, 84.0 * b / mx),
            b == 0 ? Res.Brush("Track") : Res.Brush("WorkDot"),
            $"{start.AddHours(i):HH}:00 – {start.AddHours(i + 1):HH}:00\n{Text.Tokens(b)} new tokens · {calls[i]} model calls",
            i == 23 ? "now" : i % 6 == 5 ? start.AddHours(i).ToString("HH:00", CultureInfo.InvariantCulture) : null)).ToList();

        var cost = today.Sum(main.Prices.Cost);
        TodayLine = $"Today: {Text.Tokens(today.Sum(u => u.Billable))} new tokens (input and output, not counting cached context re-read on each turn) · {today.Count} model calls"
            + (cost > 0 ? " · about $" + cost.ToString("0.00", CultureInfo.InvariantCulture) + " at API prices" : "");

        Models = BarVM.From(d7.GroupBy(u => Text.PrettyModel(u.Model)).Select(g => new KeyValuePair<string, long>(g.Key, g.Count())), "Fg2", 6);
        var tools = new Dictionary<string, long>(); var skills = new Dictionary<string, long>();
        foreach (var s in main.Store.All.Where(x => x.Last > now.AddDays(-7)))
        {
            foreach (var kv in s.Tools) tools[kv.Key] = tools.GetValueOrDefault(kv.Key) + kv.Value;
            foreach (var kv in s.Skills) skills[kv.Key] = skills.GetValueOrDefault(kv.Key) + kv.Value;
        }
        Tools = BarVM.From(tools, "WorkDot", 8, false, Core.Tools.Label);
        Skills = BarVM.From(skills, "Accent", 8);
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
