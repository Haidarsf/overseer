using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Overseer.Core;

static class JsonFile
{
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static T Load<T>(string path, Func<T> fallback)
    {
        try { if (File.Exists(path)) return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Opts) ?? fallback(); }
        catch { }
        return fallback();
    }

    public static void Save<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Opts), new UTF8Encoding(false));
        File.Move(tmp, path, true);
    }
}

// ================= Overseer's own settings (~/.overseer/config.json) =================
public sealed class OverseerConfig
{
    public bool NotifyOnFinish { get; set; } = true;
    public bool NotifyOnPermission { get; set; } = true;
    public bool NotifyOnErrors { get; set; } = true;
    public int ContextAlertPercent { get; set; } = 85;
    public int LimitAlertPercent { get; set; } = 80;
    /// <summary>Optional token budget for the rolling 5-hour window when the status line isn't available (0 = off).</summary>
    public long FiveHourTokenBudget { get; set; }
    public long WeeklyTokenBudget { get; set; }
    public bool Redact { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool StartMinimized { get; set; }
    public string Theme { get; set; } = "dark";
    /// <summary>Load extension DLLs from ~/.overseer/plugins (off by default; they run with your permissions).</summary>
    public bool LoadExtensions { get; set; }

    public static OverseerConfig Load() => JsonFile.Load(Paths.ConfigFile, () => new OverseerConfig());
    public void Save() => JsonFile.Save(Paths.ConfigFile, this);
}

// ================= presets =================
public sealed class Preset
{
    public string Name { get; set; } = "";
    public string? Model { get; set; }
    public string? Effort { get; set; }
    public string? PermissionMode { get; set; }
    public string? Description { get; set; }
}

public static class Presets
{
    public static List<Preset> Defaults() => new()
    {
        new() { Name = "Quick & cheap", Model = "haiku", Effort = "low", Description = "Small edits, questions, lookups" },
        new() { Name = "Everyday", Model = "sonnet", Effort = "medium", Description = "Most coding work" },
        new() { Name = "Deep work", Model = "opus", Effort = "high", PermissionMode = "plan", Description = "Hard problems: plans first, then builds" },
        new() { Name = "Maximum", Model = "fable", Effort = "xhigh", Description = "The most capable model for the hardest tasks" },
    };

    public static List<Preset> Load() => JsonFile.Load(Paths.PresetsFile, Defaults);
    public static void Save(List<Preset> list) => JsonFile.Save(Paths.PresetsFile, list);

    /// <summary>Makes a preset the default for new sessions (settings.json: model, effortLevel, permissions.defaultMode).</summary>
    public static ActionResult ApplyAsDefault(Preset p)
    {
        var results = new[]
        {
            SettingsFile.SetString(p.Model, "model"),
            SettingsFile.SetString(p.Effort, "effortLevel"),
            SettingsFile.SetString(p.PermissionMode, "permissions", "defaultMode"),
        };
        var bad = results.FirstOrDefault(r => !r.Ok);
        return bad ?? ActionResult.Good($"\"{p.Name}\" is now the default for new sessions. Per-model effort settings still take priority.");
    }
}

// ================= prices & reports =================
public sealed class ModelPrice
{
    public string Match { get; set; } = "";   // prefix of the model id, e.g. "claude-opus-5-5"
    public double Input { get; set; }        // $ per million tokens
    public double Output { get; set; }
    public double CacheWrite { get; set; }
    public double CacheRead { get; set; }
}

public sealed class PriceTable
{
    public string Note { get; set; } = "USD per million tokens, Anthropic API list prices as of 2026-11. Edit freely; check anthropic.com/pricing. Subscription plans are not billed per token, so treat costs as 'API-equivalent'.";
    public List<ModelPrice> Models { get; set; } = new();

    public static PriceTable Defaults()
    {
        // cache write = 1.25 × input (5-minute cache); cache read as published where known, else 0.1 × input
        static ModelPrice P(string m, double i, double o, double? read = null) => new() { Match = m, Input = i, Output = o, CacheWrite = Math.Round(i * 1.25, 4), CacheRead = read ?? Math.Round(i * 0.1, 4) };
        return new PriceTable
        {
            Models =
            {
                P("claude-fable-5-2", 8, 40, 0.20), P("claude-fable-5-1", 10, 50), P("claude-fable-5", 10, 50),
                P("claude-opus-5-5", 4, 20, 0.20), P("claude-opus-5", 5, 25), P("claude-opus-4-8", 5, 25), P("claude-opus-4-7", 5, 25), P("claude-opus-4-6", 5, 25),
                P("claude-sonnet-5-5", 2, 10, 0.10), P("claude-sonnet-5", 2, 10), P("claude-sonnet-4-6", 3, 15),
                P("claude-haiku-5-5", 0.10, 0.50), P("claude-haiku-4-5", 1, 5),
            }
        };
    }

    public static PriceTable Load() => JsonFile.Load(Paths.PricesFile, () => { var d = Defaults(); JsonFile.Save(Paths.PricesFile, d); return d; });

    public ModelPrice? For(string? model)
    {
        if (string.IsNullOrEmpty(model)) return null;
        var m = model.Replace("[1m]", "");
        return Models.Where(p => m.StartsWith(p.Match, StringComparison.OrdinalIgnoreCase)).OrderByDescending(p => p.Match.Length).FirstOrDefault();
    }

    public double Cost(UsageRecord u)
    {
        var p = For(u.Model);
        if (p == null) return 0;
        return (u.Input * p.Input + u.Output * p.Output + u.CacheWrite * p.CacheWrite + u.CacheRead * p.CacheRead) / 1_000_000.0;
    }
}

public sealed record ReportRow(string Key, long Calls, long Input, long Output, long CacheWrite, long CacheRead, double Cost)
{
    public long Billable => Input + Output + CacheWrite;
}

public enum ReportGroup { Day, Project, Model, Session }

public static class Reports
{
    public static List<ReportRow> Build(IEnumerable<UsageRecord> usage, DateTime from, DateTime to, ReportGroup by, PriceTable prices, Func<string, string>? sessionName = null)
    {
        return usage.Where(u => u.Time >= from && u.Time < to)
            .GroupBy(u => by switch
            {
                ReportGroup.Day => u.Time.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ReportGroup.Project => u.Project ?? "(unknown)",
                ReportGroup.Model => Text.PrettyModel(u.Model),
                _ => sessionName?.Invoke(u.SessionId) ?? u.SessionId,
            })
            .Select(g => new ReportRow(g.Key, g.Count(), g.Sum(x => x.Input), g.Sum(x => x.Output), g.Sum(x => x.CacheWrite), g.Sum(x => x.CacheRead), g.Sum(prices.Cost)))
            .OrderBy(r => by == ReportGroup.Day ? r.Key : "").ThenByDescending(r => by == ReportGroup.Day ? 0 : r.Billable)
            .ToList();
    }

    public static string ToCsv(IEnumerable<ReportRow> rows, string keyName)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{keyName},model_calls,input_tokens,output_tokens,cache_write_tokens,cache_read_tokens,api_equivalent_cost_usd");
        foreach (var r in rows)
            sb.AppendLine(string.Join(',', Csv(r.Key), r.Calls, r.Input, r.Output, r.CacheWrite, r.CacheRead, r.Cost.ToString("0.0000", CultureInfo.InvariantCulture)));
        return sb.ToString();
    }

    static string Csv(string s) => s.IndexOfAny(new[] { ',', '"', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    /// <summary>Token sums per bucket, oldest first (for sparklines and the hourly chart).</summary>
    public static long[] Buckets(IEnumerable<UsageRecord> all, DateTime start, TimeSpan step, int n)
    {
        var r = new long[n];
        foreach (var u in all)
        {
            if (u.Time < start) continue;
            var i = (int)((u.Time - start).Ticks / step.Ticks);
            if (i >= 0 && i < n) r[i] += u.Billable;
        }
        return r;
    }
}

// ================= alerts =================
public sealed record Alert(string Key, string Title, string Message, Session? Session, bool Urgent);

/// <summary>Turns session state into alerts. Each alert fires once until its condition clears.</summary>
public sealed class AlertEngine
{
    readonly HashSet<string> active = new();

    public List<Alert> Evaluate(SessionStore store, OverseerConfig cfg)
    {
        var fired = new List<Alert>();
        var current = new HashSet<string>();
        void Check(bool cond, string key, Func<Alert> make)
        {
            if (!cond) return;
            current.Add(key);
            if (active.Add(key)) fired.Add(make());
        }

        foreach (var s in store.TopLevel().Where(x => x.IsLive))
        {
            var pct = s.Status?.ContextUsedPercent ?? (s.ContextWindow > 0 ? 100.0 * s.LastContext / s.ContextWindow : 0);
            Check(pct >= cfg.ContextAlertPercent, "ctx:" + s.Id,
                () => new Alert("ctx:" + s.Id, "Context almost full", $"\"{s.Title}\" is at {Math.Round(pct)}% of its context window. Consider /compact or a fresh session.", s, false));
            Check(cfg.NotifyOnPermission && s.State == SessionState.NeedsPermission, "perm:" + s.Id + ":" + s.Hook?.Updated.Ticks,
                () => new Alert("perm", "Claude needs your permission", $"\"{s.Title}\": {s.Hook?.Message ?? "waiting for approval"}", s, true));
            var recent = s.Events.Skip(Math.Max(0, s.Events.Count - 10)).ToList();
            Check(cfg.NotifyOnErrors && recent.Count(e => e.Kind == EventKind.Error) >= 3, "err:" + s.Id + ":" + s.Errors / 3,
                () => new Alert("err", "Tools keep failing", $"\"{s.Title}\" hit {recent.Count(e => e.Kind == EventKind.Error)} tool errors in its last 10 steps.", s, false));
        }

        var limits = store.LatestLimits();
        if (limits != null)
        {
            Check(limits.FiveHourUsedPercent >= cfg.LimitAlertPercent, "5h:" + limits.FiveHourResetsAt,
                () => new Alert("5h", "Near your 5-hour limit", $"{Math.Round(limits.FiveHourUsedPercent ?? 0)}% used. Resets {limits.FiveHourResetsAt:HH:mm}.", null, false));
            Check(limits.WeeklyUsedPercent >= cfg.LimitAlertPercent, "7d:" + limits.WeeklyResetsAt,
                () => new Alert("7d", "Near your weekly limit", $"{Math.Round(limits.WeeklyUsedPercent ?? 0)}% used. Resets {limits.WeeklyResetsAt:ddd HH:mm}.", null, false));
        }
        else if (cfg.FiveHourTokenBudget > 0)
        {
            var used = store.AllUsage().Where(u => u.Time > DateTime.Now.AddHours(-5)).Sum(u => u.Billable);
            Check(used * 100 >= cfg.FiveHourTokenBudget * cfg.LimitAlertPercent, "5hb:" + DateTime.Now.Date,
                () => new Alert("5h", "Near your 5-hour budget", $"{Text.Tokens(used)} of your {Text.Tokens(cfg.FiveHourTokenBudget)} token budget.", null, false));
        }

        active.RemoveWhere(k => !current.Contains(k) && !k.StartsWith("perm:") && !k.StartsWith("err:"));
        return fired;
    }
}

// ================= search & conversation =================
public sealed record SearchHit(Session Session, DateTime Time, string Role, string Snippet);
public sealed record ConversationItem(DateTime Time, string Role, string Text, string? Tool);

public static class TranscriptSearch
{
    public static List<SearchHit> Search(IEnumerable<Session> sessions, string query, int max, CancellationToken ct)
    {
        var hits = new List<SearchHit>();
        if (string.IsNullOrWhiteSpace(query)) return hits;
        foreach (var s in sessions.OrderByDescending(x => x.Last))
        {
            if (ct.IsCancellationRequested || hits.Count >= max) break;
            try
            {
                using var fs = new FileStream(s.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var r = new StreamReader(fs, Encoding.UTF8);
                string? line;
                while ((line = r.ReadLine()) != null && hits.Count < max && !ct.IsCancellationRequested)
                {
                    if (line.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    foreach (var item in Conversation.FromLine(line))
                    {
                        var idx = item.Text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                        if (idx < 0) continue;
                        var start = Math.Max(0, idx - 60);
                        var snippet = (start > 0 ? "…" : "") + Text.OneLine(item.Text.Substring(start, Math.Min(item.Text.Length - start, 220)), 220);
                        hits.Add(new SearchHit(s, item.Time, item.Role, snippet));
                        break;
                    }
                }
            }
            catch { }
        }
        return hits;
    }
}

public static class Conversation
{
    public static List<ConversationItem> Read(string path, int max = 2000)
    {
        var list = new List<ConversationItem>();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(fs, Encoding.UTF8);
            string? line;
            while ((line = r.ReadLine()) != null)
            {
                list.AddRange(FromLine(line));
                if (list.Count > max) list.RemoveRange(0, list.Count - max);
            }
        }
        catch { }
        return list;
    }

    public static IEnumerable<ConversationItem> FromLine(string line)
    {
        bool isA = line.Contains("\"type\":\"assistant\"", StringComparison.Ordinal);
        bool isU = line.Contains("\"type\":\"user\"", StringComparison.Ordinal);
        if (!isA && !isU) yield break;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); } catch { yield break; }
        using (doc)
        {
            var o = doc.RootElement;
            if (o.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True) yield break;
            var t = TranscriptParser.Time(TranscriptParser.Str(o, "timestamp"));
            if (!o.TryGetProperty("message", out var msg) || !msg.TryGetProperty("content", out var c)) yield break;
            if (c.ValueKind == JsonValueKind.String)
            {
                var s = c.GetString() ?? "";
                if (!s.TrimStart().StartsWith('<')) yield return new ConversationItem(t, isA ? "Claude" : "You", s, null);
                yield break;
            }
            if (c.ValueKind != JsonValueKind.Array) yield break;
            foreach (var b in c.EnumerateArray())
            {
                var bt = TranscriptParser.Str(b, "type");
                if (bt == "text")
                {
                    var s = TranscriptParser.Str(b, "text") ?? "";
                    if (isU && s.TrimStart().StartsWith('<')) continue;
                    if (s.Length > 0) yield return new ConversationItem(t, isA ? "Claude" : "You", s, null);
                }
                else if (bt == "tool_use" && isA)
                {
                    var name = TranscriptParser.Str(b, "name") ?? "?";
                    b.TryGetProperty("input", out var input);
                    yield return new ConversationItem(t, "Tool", Tools.Detail(name, input), Tools.Label(name));
                }
            }
        }
    }
}

// ================= redaction (for screenshots and streaming) =================
public static class Redactor
{
    static readonly Regex WinPath = new(@"[A-Za-z]:\\[^\s""'<>|]*", RegexOptions.Compiled);
    static readonly Regex UnixPath = new(@"(?<![\w.])/(?:Users|home|var|opt|mnt|tmp|private)/[^\s""'<>|]*", RegexOptions.Compiled);
    static readonly Regex Email = new(@"[\w.+-]+@[\w-]+\.[\w.-]+", RegexOptions.Compiled);

    public static bool Enabled { get; set; }

    /// <summary>Paths become …/last-name, emails are masked.</summary>
    public static string Text(string? s)
    {
        if (!Enabled || string.IsNullOrEmpty(s)) return s ?? "";
        s = WinPath.Replace(s, m => "…\\" + Path.GetFileName(m.Value.TrimEnd('\\')));
        s = UnixPath.Replace(s, m => "…/" + Path.GetFileName(m.Value.TrimEnd('/')));
        return Email.Replace(s, "•••@•••");
    }

    /// <summary>Your own prompts are hidden entirely.</summary>
    public static string Prompt(string? s) => Enabled ? "••••••••" : s ?? "";
}

// ================= files: skills, agents, commands, memory =================
public static class ClaudeFiles
{
    static string Slug(string name) =>
        new string((name ?? "").Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');

    public static ActionResult CreateSkill(string name)
    {
        var slug = Slug(name);
        if (slug.Length == 0) return ActionResult.Bad("Type a name for the skill first.");
        var dir = Path.Combine(Paths.SkillsDir, slug);
        if (Directory.Exists(dir)) return ActionResult.Bad($"A skill named '{slug}' already exists.");
        Directory.CreateDirectory(dir);
        var md = Path.Combine(dir, "SKILL.md");
        File.WriteAllText(md, $"---\nname: {slug}\ndescription: Describe when Claude should use this skill.\n---\n\n# {slug}\n\nWrite the instructions Claude should follow here.\n", new UTF8Encoding(false));
        ProcessControl.Open(md);
        return new ActionResult(true, $"Created skill {slug} and opened its SKILL.md", md);
    }

    public static ActionResult InstallSkillFromFolder(string src)
    {
        try
        {
            if (!File.Exists(Path.Combine(src, "SKILL.md"))) return ActionResult.Bad("That folder has no SKILL.md, so it isn't a skill.");
            var name = Path.GetFileName(src.TrimEnd('\\', '/'));
            var target = Path.Combine(Paths.SkillsDir, name);
            if (Directory.Exists(target)) return ActionResult.Bad($"A skill named '{name}' is already installed.");
            CopyDir(src, target);
            return ActionResult.Good($"Installed skill {name} (available in new sessions)");
        }
        catch (Exception ex) { return ActionResult.Bad("Couldn't copy the skill: " + ex.Message); }
    }

    /// <summary>Creates ~/.claude/agents/&lt;name&gt;.md or ~/.claude/commands/&lt;name&gt;.md from a template.</summary>
    public static ActionResult CreateMarkdown(string kind, string name)
    {
        var slug = Slug(name);
        if (slug.Length == 0) return ActionResult.Bad("Type a name first.");
        var dir = kind == "agent" ? Paths.AgentsDir : Paths.CommandsDir;
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, slug + ".md");
        if (File.Exists(file)) return ActionResult.Bad($"'{slug}' already exists.");
        var body = kind == "agent"
            ? $"---\nname: {slug}\ndescription: When Claude should hand work to this agent.\ntools: Read, Grep, Glob\n---\n\nYou are a focused helper. Describe the agent's job and how it should report back.\n"
            : "---\ndescription: What this command does.\n---\n\nThe prompt Claude runs when you type /" + slug + ". Use $ARGUMENTS for anything typed after it.\n";
        File.WriteAllText(file, body, new UTF8Encoding(false));
        ProcessControl.Open(file);
        return new ActionResult(true, $"Created {kind} '{slug}' and opened it", file);
    }

    /// <summary>Moves a file into ~/.overseer/trash instead of deleting it.</summary>
    public static ActionResult Trash(string file)
    {
        try
        {
            var dest = Path.Combine(Paths.TrashDir, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Path.GetFileName(file));
            File.Move(file, dest);
            return ActionResult.Good($"Moved {Path.GetFileName(file)} to Overseer's trash ({Paths.TrashDir})");
        }
        catch (Exception ex) { return ActionResult.Bad(ex.Message); }
    }

    public static string ReadText(string file) { try { return File.Exists(file) ? File.ReadAllText(file) : ""; } catch { return ""; } }

    public static ActionResult SaveText(string file, string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            if (File.Exists(file)) File.Copy(file, file + ".overseer-backup", true);
            File.WriteAllText(file, text, new UTF8Encoding(false));
            return ActionResult.Good("Saved " + Path.GetFileName(file) + " (previous version kept as .overseer-backup)");
        }
        catch (Exception ex) { return ActionResult.Bad(ex.Message); }
    }

    static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(src)) CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
    }
}
