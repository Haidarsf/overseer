using System.Globalization;
using System.Text.Json;

namespace Overseer.Core;

/// <summary>
/// Applies one line of a Claude Code transcript (.jsonl) to a <see cref="Session"/>.
/// The transcript format is internal to Claude Code and can change; everything here is defensive,
/// and unknown records are ignored.
/// </summary>
public static class TranscriptParser
{
    public const int MaxEvents = 500;
    static readonly string[] EditTools = { "Edit", "Write", "MultiEdit", "NotebookEdit" };

    public static void ApplyLine(Session s, string line)
    {
        if (line.Length < 10) return;
        bool isA = line.Contains("\"type\":\"assistant\"", StringComparison.Ordinal);
        bool isU = line.Contains("\"type\":\"user\"", StringComparison.Ordinal);
        bool isAtt = line.Contains("\"type\":\"attachment\"", StringComparison.Ordinal);
        if (!isA && !isU && !isAtt) return;

        using var doc = JsonDocument.Parse(line);
        var o = doc.RootElement;
        if (o.ValueKind != JsonValueKind.Object) return;
        var type = Str(o, "type");
        var t = Time(Str(o, "timestamp"));
        if (t != DateTime.MinValue)
        {
            if (s.First == DateTime.MinValue || t < s.First) s.First = t;
            if (t > s.Last) s.Last = t;
        }
        s.Cwd ??= Str(o, "cwd");
        s.Version = Str(o, "version") ?? s.Version;
        s.Entrypoint = Str(o, "entrypoint") ?? s.Entrypoint;
        s.PermissionMode = Str(o, "permissionMode") ?? s.PermissionMode;

        switch (type)
        {
            case "assistant": Assistant(s, o, t); break;
            case "user": User(s, o, t); break;
            case "attachment": Attachment(s, o); break;
        }
        if (s.Events.Count > MaxEvents) s.Events.RemoveRange(0, s.Events.Count - MaxEvents);
    }

    static void Assistant(Session s, JsonElement o, DateTime t)
    {
        if (!o.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) return;
        var model = Str(msg, "model");
        if (!string.IsNullOrEmpty(model) && model != "<synthetic>") s.Model = model;
        var id = Str(msg, "id");
        if (id != null && msg.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
        {
            long i = Num(u, "input_tokens"), ou = Num(u, "output_tokens"), cr = Num(u, "cache_read_input_tokens"), cc = Num(u, "cache_creation_input_tokens");
            if (!s.RecentMessages.TryGetValue(id, out var seenOut))
            {
                s.RecentMessages[id] = ou;
                if (s.RecentMessages.Count > 64) s.RecentMessages.Remove(s.RecentMessages.Keys.First());
                s.Usages.Add(new UsageRecord { Time = t, Model = s.Model, SessionId = s.ParentId ?? s.Id, Project = s.Cwd, Input = i, Output = ou, CacheRead = cr, CacheWrite = cc });
                s.Input += i; s.Output += ou; s.CacheRead += cr; s.CacheWrite += cc;
                s.Calls++;
                s.LastContext = i + cr + cc;
            }
            else if (ou > seenOut)
            {
                // later streaming chunks of the same message carry the final output count
                s.Output += ou - seenOut;
                s.RecentMessages[id] = ou;
                var last = s.Usages.LastOrDefault();
                if (last != null) last.Output += ou - seenOut;
            }
        }
        if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return;
        foreach (var b in content.EnumerateArray())
        {
            var bt = Str(b, "type");
            if (bt == "tool_use")
            {
                var name = Str(b, "name") ?? "?";
                b.TryGetProperty("input", out var input);
                s.ToolCalls++;
                Inc(s.Tools, name);
                var srv = Tools.McpServer(name);
                if (srv != null) Inc(s.Mcp, srv);
                if (name == "Skill")
                {
                    var sk = Str(input, "skill") ?? Str(input, "command");
                    if (sk != null) Inc(s.Skills, sk);
                }
                if (name is "Agent" or "Task") s.AgentCalls++;
                if (EditTools.Contains(name))
                {
                    var fp = Str(input, "file_path") ?? Str(input, "notebook_path");
                    if (fp != null) Inc(s.FilesChanged, fp);
                }
                s.Events.Add(new ActivityEvent(t, EventKind.Tool, Tools.Label(name), Tools.Detail(name, input), name));
            }
            else if (bt == "text")
            {
                var tx = Str(b, "text");
                if (!string.IsNullOrWhiteSpace(tx)) s.Events.Add(new ActivityEvent(t, EventKind.Reply, "Reply", Text.OneLine(tx, 200)));
            }
        }
    }

    static void User(Session s, JsonElement o, DateTime t)
    {
        if (o.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True) return;
        if (!o.TryGetProperty("message", out var msg) || !msg.TryGetProperty("content", out var c)) return;
        if (c.ValueKind == JsonValueKind.String) { Prompt(s, c.GetString(), t); return; }
        if (c.ValueKind != JsonValueKind.Array) return;
        foreach (var b in c.EnumerateArray())
        {
            var bt = Str(b, "type");
            if (bt == "text") Prompt(s, Str(b, "text"), t);
            else if (bt == "tool_result" && b.TryGetProperty("is_error", out var err) && err.ValueKind == JsonValueKind.True)
            {
                s.Errors++;
                string? txt = null;
                if (b.TryGetProperty("content", out var rc))
                {
                    if (rc.ValueKind == JsonValueKind.String) txt = rc.GetString();
                    else if (rc.ValueKind == JsonValueKind.Array && rc.GetArrayLength() > 0) txt = Str(rc[0], "text");
                }
                s.Events.Add(new ActivityEvent(t, EventKind.Error, "Tool error", Text.OneLine(txt, 200)));
            }
        }
    }

    static void Prompt(Session s, string? text, DateTime t)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var tt = text.TrimStart();
        if (tt.StartsWith('<') || tt.StartsWith("[Request interrupted", StringComparison.Ordinal)) return; // system / command wrappers
        s.Prompts++;
        s.FirstPrompt ??= Text.OneLine(tt, 80);
        if (s.IsSubagent) s.AgentDescription ??= Text.OneLine(tt, 120);
        s.Events.Add(new ActivityEvent(t, EventKind.Prompt, "You", Text.OneLine(tt, 200)));
    }

    static void Attachment(Session s, JsonElement o)
    {
        if (!o.TryGetProperty("attachment", out var a)) return;
        switch (Str(a, "type"))
        {
            case "skill_listing":
                var list = new List<string>();
                foreach (var ln in (Str(a, "content") ?? "").Split('\n'))
                {
                    if (!ln.StartsWith("- ", StringComparison.Ordinal)) continue;
                    var rest = ln[2..];
                    var idx = rest.IndexOf(": ", StringComparison.Ordinal);
                    var nm = idx > 0 ? rest[..idx] : rest;
                    if (nm.Length < 80) list.Add(nm.Trim());
                }
                if (list.Count > 0) s.SkillList = list;
                break;
            case "model":
                if (a.TryGetProperty("identity", out var idn)) s.ModelName = Str(idn, "marketingName") ?? s.ModelName;
                break;
        }
    }

    // ---------- helpers ----------
    public static string? Str(JsonElement e, string k)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(k, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    public static long Num(JsonElement e, string k) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    public static DateTime Time(string? ts) =>
        ts != null && DateTime.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d.ToLocalTime() : DateTime.MinValue;

    static void Inc(Dictionary<string, int> d, string k) => d[k] = d.GetValueOrDefault(k) + 1;
}

public static class Tools
{
    public static string? McpServer(string tool)
    {
        if (!tool.StartsWith("mcp__", StringComparison.Ordinal)) return null;
        var parts = tool.Split("__");
        if (parts.Length < 2) return null;
        var srv = parts[1];
        return Guid.TryParse(srv, out _) ? "connector " + srv[..8] : srv;
    }

    public static string Label(string tool)
    {
        var srv = McpServer(tool);
        if (srv == null) return tool;
        return srv + " · " + tool[(tool.LastIndexOf("__", StringComparison.Ordinal) + 2)..];
    }

    public static string Detail(string name, JsonElement input)
    {
        if (name == "Skill") return TranscriptParser.Str(input, "skill") ?? "";
        foreach (var k in new[] { "description", "command", "file_path", "path", "pattern", "url", "query", "prompt", "skill", "action", "title", "recap" })
        {
            var v = TranscriptParser.Str(input, k);
            if (!string.IsNullOrWhiteSpace(v)) return Text.OneLine(v, 200);
        }
        return "";
    }

    /// <summary>A coarse category used to pick an icon.</summary>
    public static string Category(string? tool)
    {
        var n = (tool ?? "").ToLowerInvariant();
        if (n.Contains("bash") || n.Contains("powershell") || n.Contains("terminal")) return "terminal";
        if (n is "read" || n.Contains("read_page") || n.Contains("get_page_text")) return "read";
        if (n is "edit" or "write" or "multiedit" or "notebookedit") return "edit";
        if (n.Contains("grep") || n.Contains("glob") || n.Contains("search") || n.Contains("find")) return "search";
        if (n.Contains("web") || n.Contains("browser") || n.Contains("chrome") || n.Contains("navigate")) return "web";
        if (n is "agent" or "task" || n.Contains("session")) return "agent";
        if (n == "skill") return "skill";
        if (n.Contains("plan") || n.Contains("todo") || n.Contains("wrap_up")) return "plan";
        if (n.StartsWith("mcp__", StringComparison.Ordinal) || n.Contains(" · ")) return "mcp";
        return "tool";
    }
}

public static class Text
{
    public static string OneLine(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new System.Text.StringBuilder(Math.Min(s.Length, max + 8));
        bool space = false;
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch)) { if (!space && sb.Length > 0) sb.Append(' '); space = true; }
            else { sb.Append(ch); space = false; }
            if (sb.Length > max) break;
        }
        var r = sb.ToString().Trim();
        return r.Length > max ? r[..(max - 1)] + "…" : r;
    }

    public static string Tokens(long n) =>
        n >= 1_000_000_000 ? (n / 1e9).ToString("0.0#", CultureInfo.InvariantCulture) + "B"
        : n >= 1_000_000 ? (n / 1e6).ToString("0.0#", CultureInfo.InvariantCulture) + "M"
        : n >= 1_000 ? (n / 1e3).ToString("0.#", CultureInfo.InvariantCulture) + "k"
        : n.ToString(CultureInfo.InvariantCulture);

    public static string Ago(DateTime t)
    {
        if (t == DateTime.MinValue) return "never";
        var d = DateTime.Now - t;
        if (d.TotalSeconds < 60) return Math.Max(0, (int)d.TotalSeconds) + "s ago";
        if (d.TotalMinutes < 60) return (int)d.TotalMinutes + "m ago";
        if (d.TotalHours < 24) return (int)d.TotalHours + "h ago";
        return (int)d.TotalDays + "d ago";
    }

    public static string Duration(TimeSpan d)
    {
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        if (d.TotalMinutes < 1) return (int)d.TotalSeconds + "s";
        if (d.TotalHours < 1) return (int)d.TotalMinutes + "m";
        if (d.TotalDays < 1) return (int)d.TotalHours + "h " + d.Minutes + "m";
        return (int)d.TotalDays + "d " + d.Hours + "h";
    }

    public static string PrettyModel(string? m)
    {
        if (string.IsNullOrEmpty(m)) return "Unknown model";
        var parts = m.Replace("claude-", "").Replace("[1m]", "").Split('-');
        if (parts.Length >= 3 && int.TryParse(parts[1], out var a) && int.TryParse(parts[2], out var b))
            return char.ToUpper(parts[0][0]) + parts[0][1..] + " " + a + "." + b;
        return m;
    }
}
