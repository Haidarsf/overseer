using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Overseer.Core;

/// <summary>
/// Overseer's two optional integrations with Claude Code, both documented features:
///   * hooks       → `overseer hook` appends each event to ~/.overseer/events/&lt;session&gt;.jsonl (async, prints nothing, exits 0)
///   * status line → `overseer statusline` saves the status JSON to ~/.overseer/statusline/&lt;session&gt;.json and prints a short line
/// The same executable is used, so there is nothing extra to install.
/// </summary>
public static class Integration
{
    public static readonly string[] HookEvents =
    {
        "SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "PermissionRequest", "Notification", "Stop", "SubagentStop", "SessionEnd"
    };
    const string Marker = "overseer";

    /// <summary>Command string Claude Code runs. Hooks use bash on Windows when Git Bash exists, so forward slashes and quotes keep it portable.</summary>
    public static string CommandFor(string exePath, string mode) => "\"" + exePath.Replace('\\', '/') + "\" " + mode;

    // ---------- runtime side (called as `overseer hook` / `overseer statusline`) ----------

    /// <summary>Entry for `overseer hook`. Never throws, never prints, always exit code 0, so Claude Code is never affected.</summary>
    public static int RunHook(TextReader stdin)
    {
        try
        {
            var raw = stdin.ReadToEnd();
            if (JsonNode.Parse(raw) is not JsonObject o) return 0;
            var sid = o["session_id"]?.GetValue<string>();
            if (string.IsNullOrEmpty(sid) || sid.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return 0;
            // keep events small: drop large tool payloads, keep what the UI shows
            o.Remove("tool_response");
            o.Remove("last_assistant_message");
            if (o["tool_input"] is JsonObject ti)
                foreach (var key in ti.Select(kv => kv.Key).ToList())
                    if (ti[key] is JsonValue jv && jv.TryGetValue<string>(out var str) && str.Length > 400) ti[key] = str[..400];
            o["_overseer_time"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            var file = Path.Combine(Paths.EventsDir, sid + ".jsonl");
            File.AppendAllText(file, o.ToJsonString() + "\n", new UTF8Encoding(false));
            TrimIfLarge(file);
        }
        catch { }
        return 0;
    }

    static void TrimIfLarge(string file)
    {
        try
        {
            var fi = new FileInfo(file);
            if (fi.Length < 2_000_000) return;
            var lines = File.ReadAllLines(file);
            File.WriteAllLines(file, lines.Skip(lines.Length / 2));
        }
        catch { }
    }

    /// <summary>Entry for `overseer statusline`. Saves the JSON and prints one short line for Claude Code to show.</summary>
    public static int RunStatusLine(TextReader stdin, TextWriter stdout)
    {
        try
        {
            var raw = stdin.ReadToEnd();
            var o = JsonNode.Parse(raw) as JsonObject;
            var sid = o?["session_id"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(sid) && sid.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
            {
                var file = Path.Combine(Paths.StatusLineDir, sid + ".json");
                File.WriteAllText(file + ".tmp", raw);
                File.Move(file + ".tmp", file, true);
            }
            var snap = StatusLine.Parse(raw);
            stdout.Write(StatusLine.Render(snap));
        }
        catch { }
        return 0;
    }

    // ---------- install / uninstall (edits settings.json through SettingsFile) ----------

    public static bool HooksInstalled()
    {
        var hooks = SettingsFile.Get("hooks") as JsonObject;
        return hooks != null && HookEvents.All(ev => hooks[ev]?.ToJsonString().Contains(Marker, StringComparison.OrdinalIgnoreCase) == true);
    }

    /// <summary>True if at least one Overseer hook is registered (even a partial install).</summary>
    public static bool AnyHookInstalled() =>
        SettingsFile.HookRows().Any(r => r.Command.Contains(Marker, StringComparison.OrdinalIgnoreCase) && r.Command.EndsWith(" hook", StringComparison.Ordinal));

    public static ActionResult InstallHooks(string exePath)
    {
        return SettingsFile.Update(root =>
        {
            var hooks = root["hooks"] as JsonObject ?? new JsonObject();
            root["hooks"] = hooks;
            foreach (var ev in HookEvents)
            {
                var groups = hooks[ev] as JsonArray ?? new JsonArray();
                hooks[ev] = groups;
                RemoveOurs(groups);
                var handler = new JsonObject { ["type"] = "command", ["command"] = CommandFor(exePath, "hook"), ["async"] = true, ["timeout"] = 10 };
                var group = new JsonObject { ["hooks"] = new JsonArray(handler) };
                if (ev is "PreToolUse" or "PostToolUse" or "PermissionRequest") group["matcher"] = "*";
                groups.Add(group);
            }
        }, "Live events turned on. New sessions will report what they're doing to Overseer.");
    }

    public static ActionResult UninstallHooks()
    {
        return SettingsFile.Update(root =>
        {
            if (root["hooks"] is not JsonObject hooks) return;
            foreach (var ev in hooks.Select(kv => kv.Key).ToList())
            {
                if (hooks[ev] is not JsonArray groups) continue;
                RemoveOurs(groups);
                if (groups.Count == 0) hooks.Remove(ev);
            }
            if (hooks.Count == 0) root.Remove("hooks");
        }, "Live events turned off.");
    }

    static void RemoveOurs(JsonArray groups)
    {
        for (int i = groups.Count - 1; i >= 0; i--)
        {
            if (groups[i] is not JsonObject g || g["hooks"] is not JsonArray hs) continue;
            for (int j = hs.Count - 1; j >= 0; j--)
                if (hs[j]?["command"]?.GetValue<string>()?.Contains(Marker, StringComparison.OrdinalIgnoreCase) == true &&
                    hs[j]?["command"]?.GetValue<string>()?.EndsWith(" hook", StringComparison.Ordinal) == true) hs.RemoveAt(j);
            if (hs.Count == 0) groups.RemoveAt(i);
        }
    }

    public static string? CurrentStatusLineCommand() => SettingsFile.Get("statusLine")?["command"]?.GetValue<string>();

    public static bool StatusLineInstalled() => CurrentStatusLineCommand()?.EndsWith(" statusline", StringComparison.Ordinal) == true
                                                && CurrentStatusLineCommand()!.Contains(Marker, StringComparison.OrdinalIgnoreCase);

    public static ActionResult InstallStatusLine(string exePath, bool replaceExisting)
    {
        var cur = CurrentStatusLineCommand();
        if (cur != null && !StatusLineInstalled() && !replaceExisting)
            return ActionResult.Bad("You already have a status line (" + cur + "). Overseer won't replace it unless you confirm.");
        return SettingsFile.Update(root =>
        {
            root["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = CommandFor(exePath, "statusline"), ["padding"] = 0 };
        }, "Status line turned on. Exact context use and plan limits will show for terminal sessions.");
    }

    public static ActionResult UninstallStatusLine()
    {
        if (!StatusLineInstalled()) return ActionResult.Good("Overseer's status line isn't installed.");
        return SettingsFile.Update(root => root.Remove("statusLine"), "Status line removed.");
    }
}

public static class StatusLine
{
    public static StatusSnapshot? Parse(string raw)
    {
        if (JsonNode.Parse(raw) is not JsonObject o) return null;
        var s = new StatusSnapshot
        {
            ModelId = o["model"]?["id"]?.GetValue<string>(),
            ModelName = o["model"]?["display_name"]?.GetValue<string>(),
            CostUsd = Dbl(o["cost"]?["total_cost_usd"]),
            ContextUsedPercent = Dbl(o["context_window"]?["used_percentage"]),
            ContextWindowSize = (long?)Dbl(o["context_window"]?["context_window_size"]),
            FiveHourUsedPercent = Dbl(o["rate_limits"]?["five_hour"]?["used_percentage"]),
            FiveHourResetsAt = Epoch(o["rate_limits"]?["five_hour"]?["resets_at"]),
            WeeklyUsedPercent = Dbl(o["rate_limits"]?["seven_day"]?["used_percentage"]),
            WeeklyResetsAt = Epoch(o["rate_limits"]?["seven_day"]?["resets_at"]),
        };
        return s;
    }

    public static string Render(StatusSnapshot? s)
    {
        if (s == null) return "Overseer";
        var parts = new List<string> { s.ModelName ?? Text.PrettyModel(s.ModelId) };
        if (s.ContextUsedPercent is { } c) parts.Add("ctx " + Pct(c));
        if (s.FiveHourUsedPercent is { } f) parts.Add("5h " + Pct(f));
        if (s.WeeklyUsedPercent is { } w) parts.Add("7d " + Pct(w));
        if (s.CostUsd is { } cost && cost > 0) parts.Add("$" + cost.ToString("0.00", CultureInfo.InvariantCulture));
        return string.Join(" · ", parts);
    }

    static string Pct(double v) => Math.Round(v, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture) + "%";

    static double? Dbl(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p)) return p;
        return null;
    }

    static DateTime? Epoch(JsonNode? n)
    {
        var d = Dbl(n);
        if (d is null or <= 0) return null;
        return DateTimeOffset.FromUnixTimeSeconds((long)d.Value).LocalDateTime;
    }
}
