using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Overseer.Core;

/// <summary>
/// Safe edits to Claude Code's user settings.json:
///   * re-reads the file before every change (never writes a stale copy)
///   * keeps every other key and their order
///   * writes a one-time backup (settings.json.overseer-backup) before the first change
///   * refuses to touch a file that isn't valid JSON
///   * writes atomically (temp file + replace)
/// </summary>
public static class SettingsFile
{
    static readonly object WriteLock = new();
    static readonly HashSet<string> backedUp = new(StringComparer.OrdinalIgnoreCase);
    internal static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
        // keep quotes, <, > and non-ASCII readable in a file people edit by hand
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Override for tests.</summary>
    public static string? PathOverride { get; set; }
    public static string FilePath => PathOverride ?? Paths.SettingsFile;
    public static string BackupPath => FilePath + ".overseer-backup";

    public static JsonObject? Read()
    {
        try
        {
            if (!File.Exists(FilePath)) return new JsonObject();
            var text = File.ReadAllText(FilePath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
            return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject;
        }
        catch { return null; }
    }

    public static JsonNode? Get(params string[] path)
    {
        JsonNode? cur = Read();
        foreach (var k in path)
        {
            if (cur is not JsonObject o || !o.TryGetPropertyValue(k, out var next)) return null;
            cur = next;
        }
        return cur;
    }

    public static string? GetString(params string[] path) =>
        Get(path) is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static bool? GetBool(params string[] path) =>
        Get(path) is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    /// <summary>Sets a nested value. Passing null removes the key (and any parents left empty).</summary>
    public static ActionResult Set(JsonNode? value, params string[] path)
    {
        return Update(root =>
        {
            var cur = root;
            for (int i = 0; i < path.Length - 1; i++)
            {
                if (cur[path[i]] is not JsonObject next)
                {
                    if (value == null) return;
                    next = new JsonObject();
                    cur[path[i]] = next;
                }
                cur = next;
            }
            if (value == null) cur.Remove(path[^1]); else cur[path[^1]] = value;
            if (value == null) Prune(root, path, 0);
        }, "Saved " + string.Join(".", path));
    }

    public static ActionResult SetString(string? value, params string[] path) => Set(value == null ? null : JsonValue.Create(value), path);

    static bool Prune(JsonObject o, string[] path, int i)
    {
        if (i >= path.Length - 1) return o.Count == 0;
        if (o[path[i]] is JsonObject child && Prune(child, path, i + 1)) o.Remove(path[i]);
        return o.Count == 0;
    }

    /// <summary>Applies <paramref name="edit"/> to a fresh copy of the file and saves it.</summary>
    public static ActionResult Update(Action<JsonObject> edit, string okMessage)
    {
        lock (WriteLock)
        {
            try
            {
                var root = Read();
                if (root == null) return ActionResult.Bad("settings.json isn't valid JSON, so Overseer won't touch it. Fix it by hand first.");
                if (!backedUp.Contains(FilePath) && File.Exists(FilePath))
                {
                    if (!File.Exists(BackupPath)) File.Copy(FilePath, BackupPath);
                    backedUp.Add(FilePath);
                }
                edit(root);
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var tmp = FilePath + ".overseer-tmp";
                File.WriteAllText(tmp, root.ToJsonString(Pretty) + Environment.NewLine, new UTF8Encoding(false));
                File.Move(tmp, FilePath, true);
                return ActionResult.Good(okMessage);
            }
            catch (Exception ex) { return ActionResult.Bad("Couldn't save settings: " + ex.Message); }
        }
    }

    /// <summary>Restores the backup made before Overseer's first change.</summary>
    public static ActionResult RestoreBackup()
    {
        lock (WriteLock)
        {
            if (!File.Exists(BackupPath)) return ActionResult.Bad("There's no backup yet. One is made before Overseer's first change.");
            File.Copy(BackupPath, FilePath, true);
            return ActionResult.Good("Restored settings.json from the backup.");
        }
    }

    // ---------- typed helpers for the Control tab ----------
    public static string SkillState(string name) => GetString("skillOverrides", name) ?? "on";
    public static ActionResult SetSkillState(string name, string state)
    {
        var r = SetString(state == "on" ? null : state, "skillOverrides", name);
        return r.Ok ? ActionResult.Good($"{name} → {state} (applies to new sessions)") : r;
    }

    public static List<string> Rules(string kind) =>
        (Get("permissions", kind) as JsonArray)?.Select(n => n?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToList() ?? new();

    public static ActionResult AddRule(string kind, string rule)
    {
        rule = rule.Trim();
        if (rule.Length == 0) return ActionResult.Bad("Type a rule first, e.g. Bash(npm test:*)");
        return Update(root =>
        {
            var perms = root["permissions"] as JsonObject ?? new JsonObject();
            root["permissions"] = perms;
            var arr = perms[kind] as JsonArray ?? new JsonArray();
            perms[kind] = arr;
            if (!arr.Any(n => n?.GetValue<string>() == rule)) arr.Add(JsonValue.Create(rule));
        }, $"Added to {kind}: {rule}");
    }

    public static ActionResult RemoveRule(string kind, string rule) => Update(root =>
    {
        if (root["permissions"]?[kind] is not JsonArray arr) return;
        for (int i = arr.Count - 1; i >= 0; i--) if (arr[i]?.GetValue<string>() == rule) arr.RemoveAt(i);
        if (arr.Count == 0) ((JsonObject)root["permissions"]!).Remove(kind);
        if (root["permissions"] is JsonObject p && p.Count == 0) root.Remove("permissions");
    }, $"Removed from {kind}: {rule}");

    /// <summary>All configured hooks as (event, matcher, command) rows.</summary>
    public static List<(string Event, string? Matcher, string Command)> HookRows()
    {
        var rows = new List<(string, string?, string)>();
        if (Get("hooks") is not JsonObject hooks) return rows;
        foreach (var (ev, node) in hooks)
        {
            if (node is not JsonArray groups) continue;
            foreach (var g in groups.OfType<JsonObject>())
                if (g["hooks"] is JsonArray hs)
                    foreach (var h in hs.OfType<JsonObject>())
                        rows.Add((ev, g["matcher"]?.GetValue<string>(), h["command"]?.GetValue<string>() ?? h["type"]?.GetValue<string>() ?? "?"));
        }
        return rows;
    }

    public static ActionResult RemoveHook(string ev, string command) => Update(root =>
    {
        if (root["hooks"]?[ev] is not JsonArray groups) return;
        for (int i = groups.Count - 1; i >= 0; i--)
        {
            if (groups[i]?["hooks"] is not JsonArray hs) continue;
            for (int j = hs.Count - 1; j >= 0; j--) if (hs[j]?["command"]?.GetValue<string>() == command) hs.RemoveAt(j);
            if (hs.Count == 0) groups.RemoveAt(i);
        }
        if (groups.Count == 0) ((JsonObject)root["hooks"]!).Remove(ev);
        if (root["hooks"] is JsonObject h && h.Count == 0) root.Remove("hooks");
    }, $"Removed {ev} hook");
}
