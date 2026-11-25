using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Overseer.Core;

/// <summary>
/// Keeps an up-to-date picture of every Claude Code session on this machine.
/// Reads transcripts incrementally (only new bytes), persists an index so restarts are instant,
/// watches the folders for changes, and merges live data from Overseer's hooks and status line.
/// Read-only with respect to Claude Code's files.
/// </summary>
public sealed class SessionStore : IDisposable
{
    const int CacheVersion = 2;
    readonly object gate = new();
    readonly Dictionary<string, Session> sessions = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, long> eventOffsets = new(StringComparer.OrdinalIgnoreCase);
    readonly List<FileSystemWatcher> watchers = new();
    DateTime lastStatic = DateTime.MinValue, lastCacheSave = DateTime.Now, lastFullScan = DateTime.MinValue;
    volatile bool dirty = true;
    Timer? timer;

    public List<SkillInfo> InstalledSkills { get; private set; } = new();
    public List<MarkdownItem> Agents { get; private set; } = new();
    public List<MarkdownItem> Commands { get; private set; } = new();

    /// <summary>Raised (on a background thread) after a scan that changed something.</summary>
    public event Action? Changed;
    /// <summary>Raised for each new hook event: (session, hook event name, raw json).</summary>
    public event Action<Session, string, JsonObject>? HookEvent;

    public object Gate => gate;

    public IReadOnlyCollection<Session> All { get { lock (gate) return sessions.Values.ToList(); } }

    public List<Session> TopLevel()
    {
        lock (gate)
            return sessions.Values.Where(s => !s.IsSubagent && s.Calls + s.Prompts > 0)
                .OrderByDescending(s => s.IsLive).ThenByDescending(s => s.Last).ToList();
    }

    /// <summary>Top-level sessions by id. See <see cref="ById"/>.</summary>
    public Dictionary<string, Session> TopLevelById() => ById(TopLevel());

    /// <summary>
    /// Sessions keyed by session id. Ids aren't unique across files: resuming or copying a session
    /// can leave two transcripts with the same id. The first one wins, so pass them most relevant first.
    /// </summary>
    public static Dictionary<string, Session> ById(IEnumerable<Session> ordered)
    {
        var d = new Dictionary<string, Session>(StringComparer.Ordinal);
        foreach (var s in ordered) d.TryAdd(s.Id, s);
        return d;
    }

    public List<Session> SubagentsOf(string id)
    {
        lock (gate) return sessions.Values.Where(s => s.IsSubagent && s.ParentId == id).OrderBy(s => s.First).ToList();
    }

    public Session? Find(string filePath) { lock (gate) return sessions.GetValueOrDefault(filePath); }

    /// <summary>Every usage record: from the transcripts on disk, plus Overseer's history of transcripts Claude Code has deleted.</summary>
    public List<UsageRecord> AllUsage()
    {
        lock (gate)
        {
            var all = sessions.Values.SelectMany(s => s.Usages);
            if (history != null) all = all.Concat(history.Gone(sessions.Keys).SelectMany(e => e.Usages));
            return all.Where(u => u.Time != DateTime.MinValue).ToList();
        }
    }

    /// <summary>Session titles by id, including sessions only the usage history remembers.</summary>
    public Dictionary<string, string> SessionTitles()
    {
        var d = TopLevelById().ToDictionary(p => p.Key, p => p.Value.Title, StringComparer.Ordinal);
        lock (gate)
            if (history != null)
                foreach (var e in history.Gone(sessions.Keys).Where(e => !e.IsSubagent && e.Title != null)) d.TryAdd(e.Id, e.Title!);
        return d;
    }

    /// <summary>When the oldest usage record is from (null when there's none).</summary>
    public DateTime? OldestUsage()
    {
        var all = AllUsage();
        return all.Count == 0 ? null : all.Min(u => u.Time);
    }

    UsageHistory? history;
    public UsageHistory? History => history;

    /// <summary>Deletes Overseer's usage history. Reports then only cover the transcripts on disk.</summary>
    public void ClearHistory()
    {
        lock (gate) history?.Clear();
    }

    // ---------- lifecycle ----------
    public void Start(TimeSpan fallbackInterval)
    {
        OpenHistory();
        LoadCache();
        Watch(Paths.ProjectsDir, "*.jsonl", true);
        Watch(Paths.LiveSessionsDir, "*.json", false);
        Watch(Paths.EventsDir, "*.jsonl", false);
        Watch(Paths.StatusLineDir, "*.json", false);
        // the tick is cheap when nothing is dirty; a periodic full pass covers anything a watcher missed
        timer = new Timer(_ => Tick(fallbackInterval), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(700));
    }

    void Watch(string dir, string filter, bool recursive)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var w = new FileSystemWatcher(dir, filter) { IncludeSubdirectories = recursive, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
            FileSystemEventHandler h = (_, _) => dirty = true;
            w.Changed += h; w.Created += h; w.Deleted += h;
            w.Renamed += (_, _) => dirty = true;
            w.Error += (_, _) => dirty = true;
            w.EnableRaisingEvents = true;
            watchers.Add(w);
        }
        catch { /* folder not watchable: the fallback scan still runs */ }
    }

    int ticking;
    void Tick(TimeSpan fallback)
    {
        if (Interlocked.Exchange(ref ticking, 1) == 1) return;
        try
        {
            bool due = DateTime.Now - lastFullScan > fallback;
            if (!dirty && !due) return;
            dirty = false;
            if (Scan()) Changed?.Invoke();
            if (due) lastFullScan = DateTime.Now;
            if (DateTime.Now - lastCacheSave > TimeSpan.FromSeconds(60)) SaveCache();
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
        finally { Interlocked.Exchange(ref ticking, 0); }
    }

    public void ForceRescan() { dirty = true; lastStatic = DateTime.MinValue; }

    public void Dispose()
    {
        timer?.Dispose();
        foreach (var w in watchers) w.Dispose();
        SaveCache();
    }

    // ---------- scanning ----------
    /// <summary>Runs one incremental pass. Returns true if anything changed.</summary>
    public bool Scan()
    {
        bool changed = false;
        lock (gate)
        {
            var proj = Paths.ProjectsDir;
            if (Directory.Exists(proj))
            {
                foreach (var pdir in SafeDirs(proj))
                    foreach (var f in SafeFiles(pdir, "*.jsonl"))
                    {
                        var s = Get(f, false, null);
                        changed |= ReadTitle(s, Path.Combine(pdir, s.Id, "custom-title.json"));
                        changed |= ReadNew(s);
                        var sub = Path.Combine(pdir, s.Id, "subagents");
                        if (Directory.Exists(sub))
                            foreach (var sf in SafeFiles(sub, "*.jsonl")) changed |= ReadNew(Get(sf, true, s.Id));
                    }
            }
            changed |= ScanLive();
            changed |= ScanHookEvents();
            changed |= ScanStatusLines();
            if (DateTime.Now - lastStatic > TimeSpan.FromSeconds(60)) { ScanStatic(); lastStatic = DateTime.Now; changed = true; }
        }
        return changed;
    }

    Session Get(string file, bool sub, string? parent)
    {
        if (!sessions.TryGetValue(file, out var s))
        {
            s = new Session { FilePath = file, Id = Path.GetFileNameWithoutExtension(file), IsSubagent = sub, ParentId = parent };
            sessions[file] = s;
        }
        return s;
    }

    static string[] SafeDirs(string d) { try { return Directory.GetDirectories(d); } catch { return Array.Empty<string>(); } }
    static string[] SafeFiles(string d, string p) { try { return Directory.GetFiles(d, p); } catch { return Array.Empty<string>(); } }

    public static string ReadShared(string f)
    {
        using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(fs, Encoding.UTF8);
        return r.ReadToEnd();
    }

    static bool ReadTitle(Session s, string file)
    {
        if (!File.Exists(file)) return false;
        try
        {
            var t = JsonNode.Parse(ReadShared(file))?["customTitle"]?.GetValue<string>();
            if (t == s.CustomTitle) return false;
            s.CustomTitle = t;
            return true;
        }
        catch { return false; }
    }

    /// <summary>Reads only the bytes appended since the last pass. Complete lines only.</summary>
    static bool ReadNew(Session s)
    {
        try
        {
            using var fs = new FileStream(s.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long len = fs.Length;
            if (len < s.Offset) Reset(s); // rewritten or truncated
            if (len == s.Offset) return false;
            fs.Seek(s.Offset, SeekOrigin.Begin);
            var buf = new byte[len - s.Offset];
            int read = 0;
            while (read < buf.Length) { int n = fs.Read(buf, read, buf.Length - read); if (n <= 0) break; read += n; }
            int last = Array.LastIndexOf(buf, (byte)'\n', read - 1);
            if (last < 0) return false;
            var text = Encoding.UTF8.GetString(buf, 0, last + 1);
            s.Offset += last + 1;
            foreach (var line in text.Split('\n'))
            {
                try { TranscriptParser.ApplyLine(s, line); } catch { /* skip malformed or unknown records */ }
            }
            return true;
        }
        catch { return false; }
    }

    static void Reset(Session s)
    {
        var fresh = new Session { Id = s.Id, FilePath = s.FilePath, IsSubagent = s.IsSubagent, ParentId = s.ParentId };
        foreach (var p in typeof(Session).GetProperties().Where(p => p.CanWrite)) p.SetValue(s, p.GetValue(fresh));
    }

    bool ScanLive()
    {
        bool changed = false;
        var seen = new HashSet<Session>();
        foreach (var f in SafeFiles(Paths.LiveSessionsDir, "*.json"))
        {
            try
            {
                var o = JsonNode.Parse(ReadShared(f)) as JsonObject;
                var sid = o?["sessionId"]?.GetValue<string>();
                int pid = o?["pid"]?.GetValue<int>() ?? 0;
                if (sid == null || !ProcessAlive(pid)) continue;
                var s = sessions.Values.FirstOrDefault(x => !x.IsSubagent && x.Id == sid);
                if (s == null) continue;
                var st = o!["status"]?.GetValue<string>();
                var nm = o["name"]?.GetValue<string>();
                if (!s.IsLive || s.LiveStatus != st || s.LiveName != nm) changed = true;
                s.IsLive = true; s.Pid = pid; s.LiveStatus = st; s.LiveName = nm;
                s.Entrypoint ??= o["entrypoint"]?.GetValue<string>();
                s.Version ??= o["version"]?.GetValue<string>();
                seen.Add(s);
            }
            catch { }
        }
        foreach (var s in sessions.Values)
            if (s.IsLive && !seen.Contains(s)) { s.IsLive = false; s.LiveStatus = null; changed = true; }
        return changed;
    }

    static bool ProcessAlive(int pid)
    {
        if (pid <= 0) return false;
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; } catch { return false; }
    }

    /// <summary>Reads events appended by `overseer hook` (one JSON object per line, per session file).</summary>
    bool ScanHookEvents()
    {
        bool changed = false;
        foreach (var f in SafeFiles(Paths.EventsDir, "*.jsonl"))
        {
            try
            {
                var off = eventOffsets.GetValueOrDefault(f, -1);
                using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (off < 0) off = Math.Max(0, fs.Length - 64 * 1024); // first look: only the tail matters
                if (fs.Length < off) off = 0;
                if (fs.Length == off) { eventOffsets[f] = off; continue; }
                fs.Seek(off, SeekOrigin.Begin);
                using var r = new StreamReader(fs, Encoding.UTF8);
                var text = r.ReadToEnd();
                int lastNl = text.LastIndexOf('\n');
                if (lastNl < 0) continue;
                eventOffsets[f] = off + Encoding.UTF8.GetByteCount(text[..(lastNl + 1)]);
                foreach (var line in text[..lastNl].Split('\n'))
                {
                    if (line.Length < 2) continue;
                    try { changed |= ApplyHook(JsonNode.Parse(line) as JsonObject); } catch { }
                }
            }
            catch { }
        }
        return changed;
    }

    bool ApplyHook(JsonObject? o)
    {
        if (o == null) return false;
        var sid = o["session_id"]?.GetValue<string>();
        var ev = o["hook_event_name"]?.GetValue<string>();
        if (sid == null || ev == null) return false;
        var s = sessions.Values.FirstOrDefault(x => !x.IsSubagent && x.Id == sid);
        if (s == null) return false;
        var h = s.Hook ??= new LiveState();
        var ts = o["_overseer_time"]?.GetValue<string>();
        h.Updated = TranscriptParser.Time(ts) is var dt && dt != DateTime.MinValue ? dt : DateTime.Now;
        h.LastHookEvent = ev;
        switch (ev)
        {
            case "PreToolUse":
                h.RunningTool = Tools.Label(o["tool_name"]?.GetValue<string>() ?? "?");
                h.RunningDetail = DetailOf(o["tool_input"]);
                h.WaitingForPermission = false; h.WaitingForInput = false;
                break;
            case "PostToolUse":
            case "PostToolUseFailure":
                h.RunningTool = null; h.RunningDetail = null; h.WaitingForPermission = false;
                break;
            case "PermissionRequest":
                h.WaitingForPermission = true;
                h.Message = "Wants to use " + Tools.Label(o["tool_name"]?.GetValue<string>() ?? "a tool");
                break;
            case "Notification":
                var type = o["notification_type"]?.GetValue<string>();
                h.Message = o["message"]?.GetValue<string>();
                if (type == "permission_prompt") h.WaitingForPermission = true;
                if (type == "idle_prompt") h.WaitingForInput = true;
                break;
            case "UserPromptSubmit":
                h.WaitingForInput = false; h.WaitingForPermission = false; h.Message = null;
                break;
            case "Stop":
                h.RunningTool = null; h.WaitingForPermission = false; h.WaitingForInput = true;
                break;
            case "SessionEnd":
                h.Ended = true; h.RunningTool = null;
                break;
            case "SessionStart":
                h.Ended = false;
                break;
        }
        HookEvent?.Invoke(s, ev, o);
        return true;
    }

    static string? DetailOf(JsonNode? input)
    {
        if (input is not JsonObject io) return null;
        foreach (var k in new[] { "description", "command", "file_path", "pattern", "url", "query", "prompt" })
            if (io[k] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)) return Text.OneLine(s, 200);
        return null;
    }

    /// <summary>Reads snapshots written by `overseer statusline`.</summary>
    bool ScanStatusLines()
    {
        bool changed = false;
        foreach (var f in SafeFiles(Paths.StatusLineDir, "*.json"))
        {
            try
            {
                var sid = Path.GetFileNameWithoutExtension(f);
                var s = sessions.Values.FirstOrDefault(x => !x.IsSubagent && x.Id == sid);
                if (s == null) continue;
                var mtime = File.GetLastWriteTime(f);
                if (s.Status != null && s.Status.Updated >= mtime) continue;
                var snap = StatusLine.Parse(ReadShared(f));
                if (snap == null) continue;
                snap.Updated = mtime;
                s.Status = snap;
                changed = true;
            }
            catch { }
        }
        return changed;
    }

    /// <summary>Most recent plan-limit reading from any session's status line.</summary>
    public StatusSnapshot? LatestLimits()
    {
        lock (gate)
            return sessions.Values.Select(s => s.Status).Where(x => x?.FiveHourUsedPercent != null || x?.WeeklyUsedPercent != null)
                .OrderByDescending(x => x!.Updated).FirstOrDefault();
    }

    void ScanStatic()
    {
        var skills = new List<SkillInfo>();
        if (Directory.Exists(Paths.SkillsDir))
        {
            string[] files;
            try { files = Directory.GetFiles(Paths.SkillsDir, "SKILL.md", SearchOption.AllDirectories); } catch { files = Array.Empty<string>(); }
            foreach (var f in files)
            {
                var dir = Path.GetDirectoryName(f)!;
                var (name, desc) = Frontmatter.Read(f);
                var source = f.Contains(Path.DirectorySeparatorChar + "synced" + Path.DirectorySeparatorChar) ? "synced" : "personal";
                skills.Add(new SkillInfo(name ?? Path.GetFileName(dir), desc, source, dir));
            }
        }
        InstalledSkills = skills.GroupBy(x => x.Name).Select(g => g.First()).OrderBy(x => x.Name).ToList();
        Agents = Markdown(Paths.AgentsDir);
        Commands = Markdown(Paths.CommandsDir);
    }

    static List<MarkdownItem> Markdown(string dir)
    {
        if (!Directory.Exists(dir)) return new();
        return SafeFiles(dir, "*.md").Select(f =>
        {
            var (n, d) = Frontmatter.Read(f);
            return new MarkdownItem(n ?? Path.GetFileNameWithoutExtension(f), d, f);
        }).OrderBy(x => x.Name).ToList();
    }

    // ---------- index cache ----------
    sealed class CacheFile
    {
        public int Version { get; set; }
        public string? ClaudeDir { get; set; }
        public List<Session> Sessions { get; set; } = new();
    }

    static string CachePath => Path.Combine(Paths.CacheDir, "index.json");
    static readonly JsonSerializerOptions CacheJson = new() { WriteIndented = false };

    void LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return;
            var c = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(CachePath), CacheJson);
            if (c == null || c.Version != CacheVersion || c.ClaudeDir != Paths.ClaudeDir) return;
            lock (gate)
            {
                // the index may still hold transcripts deleted since the last run: save their usage first
                history?.Update(c.Sessions);
                foreach (var s in c.Sessions)
                    if (File.Exists(s.FilePath)) sessions[s.FilePath] = s;
            }
        }
        catch { /* corrupt cache: rebuild from scratch */ }
    }

    /// <summary>Loads (or starts) the usage history. Called by <see cref="Start"/>; tests pass their own file.</summary>
    public void OpenHistory(string? file = null)
    {
        lock (gate)
        {
            history = new UsageHistory(file ?? UsageHistory.DefaultFile());
            history.Load();
        }
    }

    /// <summary>Copies the current usage into the history and writes it if anything changed.</summary>
    public void SaveHistory()
    {
        lock (gate)
        {
            if (history == null) return;
            history.Update(sessions.Values);
            history.Save();
        }
    }

    public void SaveCache()
    {
        try
        {
            CacheFile c;
            lock (gate) c = new CacheFile { Version = CacheVersion, ClaudeDir = Paths.ClaudeDir, Sessions = sessions.Values.ToList() };
            var tmp = CachePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(c, CacheJson));
            File.Move(tmp, CachePath, true);
        }
        catch { }
        lastCacheSave = DateTime.Now;
        SaveHistory();
    }
}

public static class Frontmatter
{
    public static (string? name, string? description) Read(string file)
    {
        string? name = null, desc = null;
        try
        {
            foreach (var ln in File.ReadLines(file).Take(40))
            {
                if (ln.StartsWith("name:", StringComparison.Ordinal)) name = ln[5..].Trim().Trim('"', '\'');
                else if (ln.StartsWith("description:", StringComparison.Ordinal)) desc = ln[12..].Trim().Trim('"', '\'');
            }
        }
        catch { }
        return (name, desc);
    }
}
