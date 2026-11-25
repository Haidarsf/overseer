using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Overseer.Core;

/// <summary>The usage numbers of one transcript, kept after Claude Code deletes the transcript.</summary>
public sealed class ArchivedSession
{
    public string FilePath { get; set; } = "";
    public string Id { get; set; } = "";
    public bool IsSubagent { get; set; }
    public string? Title { get; set; }
    public long Output { get; set; }
    public List<UsageRecord> Usages { get; set; } = new();
}

/// <summary>
/// Overseer's own copy of token usage: ~/.overseer/usage-history.json.
/// Claude Code deletes transcripts after 30 days by default (cleanupPeriodDays), which would make
/// Reports forget them. While a transcript exists its entry here is refreshed from it; once it's
/// gone, the last copy stays. Only numbers, model names, project folders and session titles are kept,
/// never the conversation itself.
/// </summary>
public sealed class UsageHistory
{
    const int FormatVersion = 1;
    const int MaxTitle = 120;
    readonly Dictionary<string, ArchivedSession> entries = new(StringComparer.OrdinalIgnoreCase);
    bool dirty;

    public UsageHistory(string file) => FilePath = file;

    public string FilePath { get; }

    /// <summary>~/.overseer/usage-history.json for the default ~/.claude; a separate file for any other Claude folder.</summary>
    public static string DefaultFile()
    {
        var claude = Path.GetFullPath(Paths.ClaudeDir).TrimEnd(Path.DirectorySeparatorChar);
        var standard = Path.GetFullPath(Path.Combine(Paths.Home, ".claude")).TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(claude, standard, StringComparison.OrdinalIgnoreCase)) return Path.Combine(Paths.OverseerDir, "usage-history.json");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(claude.ToLowerInvariant())))[..8].ToLowerInvariant();
        return Path.Combine(Paths.OverseerDir, $"usage-history-{hash}.json");
    }

    public int Count => entries.Count;

    sealed class FileFormat
    {
        public int Version { get; set; }
        public List<ArchivedSession> Sessions { get; set; } = new();
    }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public void Load()
    {
        entries.Clear();
        try
        {
            if (!File.Exists(FilePath)) return;
            var f = JsonSerializer.Deserialize<FileFormat>(File.ReadAllText(FilePath), Json);
            if (f == null || f.Version != FormatVersion) return;
            foreach (var s in f.Sessions)
                if (!string.IsNullOrEmpty(s.FilePath)) entries[s.FilePath] = s;
        }
        catch (Exception ex)
        {
            // keep the unreadable file for a bug report instead of overwriting the history
            ErrorLog.Write(ex, "usage history");
            try { File.Copy(FilePath, FilePath + ".bad", true); } catch { }
        }
    }

    /// <summary>Copies the usage of every transcript that still exists. Returns true if anything changed.</summary>
    public bool Update(IEnumerable<Session> current)
    {
        bool changed = false;
        foreach (var s in current)
        {
            if (s.Usages.Count == 0) continue;
            var title = s.IsSubagent ? null : Text.OneLine(s.Title, MaxTitle);
            if (entries.TryGetValue(s.FilePath, out var e) && e.Usages.Count == s.Usages.Count && e.Output == s.Output && e.Title == title) continue;
            entries[s.FilePath] = new ArchivedSession
            {
                FilePath = s.FilePath, Id = s.Id, IsSubagent = s.IsSubagent, Title = title, Output = s.Output,
                Usages = s.Usages.Select(Copy).ToList(),
            };
            changed = true;
        }
        dirty |= changed;
        return changed;
    }

    static UsageRecord Copy(UsageRecord u) => new()
    {
        Time = u.Time, Model = u.Model, SessionId = u.SessionId, Project = u.Project,
        Input = u.Input, Output = u.Output, CacheRead = u.CacheRead, CacheWrite = u.CacheWrite,
    };

    /// <summary>Entries whose transcript Overseer no longer sees (deleted by Claude Code, or moved away).</summary>
    public List<ArchivedSession> Gone(ICollection<string> currentFiles) =>
        entries.Values.Where(e => !currentFiles.Contains(e.FilePath)).ToList();

    /// <summary>Writes the file if something changed since the last save. Never throws.</summary>
    public void Save()
    {
        if (!dirty) return;
        try
        {
            var f = new FileFormat { Version = FormatVersion, Sessions = entries.Values.ToList() };
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(f, Json));
            File.Move(tmp, FilePath, true);
            dirty = false;
        }
        catch (Exception ex) { ErrorLog.Write(ex, "usage history save"); }
    }

    /// <summary>Forgets everything and deletes the file.</summary>
    public void Clear()
    {
        entries.Clear();
        dirty = false;
        try { File.Delete(FilePath); } catch { }
    }
}
