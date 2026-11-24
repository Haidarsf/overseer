using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Overseer.Core;

/// <summary>A permission prompt that a running Claude Code session has handed to Overseer to answer.</summary>
public sealed class PendingPermission
{
    public string Id { get; init; } = "";
    public string SessionId { get; init; } = "";
    public string? Cwd { get; init; }
    public string ToolName { get; init; } = "";
    /// <summary>What the tool wants to do: the command, file path, URL…</summary>
    public string Detail { get; init; } = "";
    /// <summary>Claude's own one-line reason, when the tool has one (Bash's description).</summary>
    public string? Reason { get; init; }
    /// <summary>The rule "Always allow" would add, e.g. Bash(npm test:*).</summary>
    public string Rule { get; init; } = "";
    public DateTime Asked { get; init; }
}

/// <summary>How the user answered a <see cref="PendingPermission"/>.</summary>
public sealed record PermissionAnswer(string Behavior, string? Message = null, string? AlwaysDestination = null)
{
    public static PermissionAnswer AllowOnce() => new("allow");
    /// <summary><paramref name="destination"/>: localSettings (this project, .claude/settings.local.json) or userSettings (~/.claude/settings.json).</summary>
    public static PermissionAnswer AllowAlways(string destination) => new("allow", null, destination);
    public static PermissionAnswer Deny(string? message) => new("deny", string.IsNullOrWhiteSpace(message) ? null : message.Trim());
}

/// <summary>
/// Lets you answer permission prompts from Overseer, using Claude Code's documented PermissionRequest hook.
///
///   `overseer permission` (a synchronous hook) writes the request to ~/.overseer/pending and waits for an answer
///   file from the app. It only waits while the Overseer window is running (heartbeat file), and gives up after
///   the configured number of seconds, so Claude Code then asks in the terminal as usual.
/// </summary>
public static class PermissionBridge
{
    public static string PendingDir => Ensure(Path.Combine(Paths.OverseerDir, "pending"));
    public static string AnswersDir => Ensure(Path.Combine(Paths.OverseerDir, "answers"));
    public static string AliveFile => Path.Combine(Paths.OverseerDir, "app.alive");
    static string Ensure(string d) { Directory.CreateDirectory(d); return d; }

    // ---------- app side ----------

    /// <summary>The app calls this every few seconds while it runs.</summary>
    public static void Heartbeat()
    {
        try { File.WriteAllText(AliveFile, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)); } catch { }
    }

    public static void ClearHeartbeat() { try { File.Delete(AliveFile); } catch { } }

    public static bool AppIsRunning(TimeSpan? within = null)
    {
        try { return File.Exists(AliveFile) && File.GetLastWriteTimeUtc(AliveFile) > DateTime.UtcNow - (within ?? TimeSpan.FromSeconds(20)); }
        catch { return false; }
    }

    /// <summary>Requests still waiting for an answer, oldest first.</summary>
    public static List<PendingPermission> Pending()
    {
        var list = new List<PendingPermission>();
        try
        {
            foreach (var f in Directory.GetFiles(PendingDir, "*.json"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddMinutes(-30)) { File.Delete(f); continue; }
                    if (JsonNode.Parse(File.ReadAllText(f)) is JsonObject o) list.Add(FromInput(o, Path.GetFileNameWithoutExtension(f)));
                }
                catch { }
            }
        }
        catch { }
        return list.OrderBy(p => p.Asked).ToList();
    }

    public static ActionResult Answer(string id, PermissionAnswer answer)
    {
        if (!IsSafeId(id)) return ActionResult.Bad("Unknown request.");
        if (!File.Exists(Path.Combine(PendingDir, id + ".json")))
            return ActionResult.Bad("That request is no longer waiting. It was answered in the terminal or timed out.");
        var o = new JsonObject { ["behavior"] = answer.Behavior };
        if (answer.Message != null) o["message"] = answer.Message;
        if (answer.AlwaysDestination != null) o["always"] = answer.AlwaysDestination;
        var file = Path.Combine(AnswersDir, id + ".json");
        File.WriteAllText(file + ".tmp", o.ToJsonString(), new UTF8Encoding(false));
        File.Move(file + ".tmp", file, true);
        return ActionResult.Good("Sent.");
    }

    // ---------- hook side (`overseer permission`) ----------

    /// <summary>Entry for `overseer permission`. Prints a decision only when you answered in Overseer; otherwise prints nothing.</summary>
    public static int RunHook(TextReader stdin, TextWriter stdout, TimeSpan wait, TimeSpan? poll = null)
    {
        string? pending = null, answerFile = null;
        try
        {
            if (JsonNode.Parse(stdin.ReadToEnd()) is not JsonObject input) return 0;
            if (!AppIsRunning()) return 0;
            var id = Guid.NewGuid().ToString("N")[..16];
            pending = Path.Combine(PendingDir, id + ".json");
            answerFile = Path.Combine(AnswersDir, id + ".json");
            var copy = (JsonObject)input.DeepClone();
            copy["_overseer_time"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            File.WriteAllText(pending + ".tmp", copy.ToJsonString(), new UTF8Encoding(false));
            File.Move(pending + ".tmp", pending, true);

            var deadline = DateTime.UtcNow + wait;
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(answerFile))
                {
                    var a = JsonNode.Parse(File.ReadAllText(answerFile)) as JsonObject;
                    var answer = new PermissionAnswer(a?["behavior"]?.GetValue<string>() ?? "", a?["message"]?.GetValue<string>(), a?["always"]?.GetValue<string>());
                    if (answer.Behavior is "allow" or "deny") stdout.Write(BuildOutput(input, answer));
                    return 0;
                }
                if (!File.Exists(pending)) return 0;   // dismissed in the app
                Thread.Sleep(poll ?? TimeSpan.FromMilliseconds(200));
            }
        }
        catch { }
        finally
        {
            try { if (pending != null) File.Delete(pending); } catch { }
            try { if (answerFile != null) File.Delete(answerFile); } catch { }
        }
        return 0;
    }

    /// <summary>The JSON Claude Code expects back from a PermissionRequest hook.</summary>
    public static string BuildOutput(JsonObject input, PermissionAnswer answer)
    {
        var decision = new JsonObject { ["behavior"] = answer.Behavior };
        if (answer.Behavior == "deny")
            decision["message"] = answer.Message ?? "The user denied this in Overseer.";
        if (answer.Behavior == "allow" && answer.AlwaysDestination != null)
        {
            var update = RuleUpdate(input);
            update["destination"] = answer.AlwaysDestination;
            decision["updatedPermissions"] = new JsonArray(update);
        }
        return new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = "PermissionRequest", ["decision"] = decision }
        }.ToJsonString();
    }

    /// <summary>Claude Code's own suggested rule when it gives one, otherwise a rule for exactly this call.</summary>
    static JsonObject RuleUpdate(JsonObject input)
    {
        if (input["permission_suggestions"] is JsonArray sugg)
            foreach (var s in sugg)
                if (s is JsonObject so && so["type"]?.GetValue<string>() == "addRules" && so["rules"] is JsonArray { Count: > 0 })
                    return (JsonObject)so.DeepClone();
        var tool = input["tool_name"]?.GetValue<string>() ?? "";
        var content = tool == "Bash" ? input["tool_input"]?["command"]?.GetValue<string>() : null;
        var rule = new JsonObject { ["toolName"] = tool };
        if (content != null) rule["ruleContent"] = content;
        return new JsonObject { ["type"] = "addRules", ["rules"] = new JsonArray(rule), ["behavior"] = "allow" };
    }

    public static string RuleText(JsonObject input)
    {
        var u = RuleUpdate(input);
        return string.Join(", ", (u["rules"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(r =>
        {
            var t = r["toolName"]?.GetValue<string>() ?? "";
            var c = r["ruleContent"]?.GetValue<string>();
            return c == null ? t : t + "(" + c + ")";
        }));
    }

    public static PendingPermission FromInput(JsonObject o, string id)
    {
        var tool = o["tool_name"]?.GetValue<string>() ?? "a tool";
        var ti = o["tool_input"] as JsonObject;
        string? Str(string k) => ti?[k] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
        var detail = Str("command") ?? Str("file_path") ?? Str("url") ?? Str("pattern") ?? Str("query") ?? Str("prompt") ?? "";
        var reason = tool == "Bash" ? Str("description") : null;
        var t = o["_overseer_time"]?.GetValue<string>();
        return new PendingPermission
        {
            Id = id,
            SessionId = o["session_id"]?.GetValue<string>() ?? "",
            Cwd = o["cwd"]?.GetValue<string>(),
            ToolName = tool,
            Detail = Text.OneLine(detail, 400),
            Reason = reason,
            Rule = RuleText(o),
            Asked = DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt) ? dt.ToLocalTime() : DateTime.Now,
        };
    }

    static bool IsSafeId(string id) => id.Length is > 0 and < 64 && id.All(char.IsLetterOrDigit);

    // ---------- undoing "Always allow" ----------

    /// <summary>The settings file a destination writes to.</summary>
    public static string FileFor(string destination, string? cwd) => destination switch
    {
        "userSettings" => SettingsFile.FilePath,
        "projectSettings" when cwd != null => Path.Combine(cwd, ".claude", "settings.json"),
        _ when cwd != null => Path.Combine(cwd, ".claude", "settings.local.json"),
        _ => SettingsFile.FilePath,
    };

    /// <summary>Removes one rule from permissions.allow in <paramref name="file"/>, leaving everything else as it was.</summary>
    public static ActionResult RemoveAllowRule(string file, string rule)
    {
        if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(SettingsFile.FilePath), StringComparison.OrdinalIgnoreCase))
            return SettingsFile.RemoveRule("allow", rule);
        try
        {
            if (!File.Exists(file) || JsonNode.Parse(File.ReadAllText(file)) is not JsonObject root) return ActionResult.Bad("The rule wasn't found in " + file);
            if (root["permissions"]?["allow"] is not JsonArray allow) return ActionResult.Bad("The rule wasn't found in " + file);
            var removed = false;
            for (int i = allow.Count - 1; i >= 0; i--)
                if (allow[i]?.GetValue<string>() == rule) { allow.RemoveAt(i); removed = true; }
            if (!removed) return ActionResult.Bad("The rule wasn't found in " + file);
            File.WriteAllText(file + ".tmp", root.ToJsonString(SettingsFile.Pretty) + Environment.NewLine, new UTF8Encoding(false));
            File.Move(file + ".tmp", file, true);
            return ActionResult.Good("Rule removed. Future runs will ask again.");
        }
        catch (Exception ex) { return ActionResult.Bad("Couldn't remove the rule: " + ex.Message); }
    }
}
