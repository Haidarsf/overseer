namespace Overseer.Core;

/// <summary>Where Claude Code and Overseer keep their files.</summary>
public static class Paths
{
    public static string Home { get; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Claude Code's config folder: $CLAUDE_CONFIG_DIR or ~/.claude.</summary>
    public static string ClaudeDir
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            return !string.IsNullOrWhiteSpace(env) && Directory.Exists(env) ? env : Path.Combine(Home, ".claude");
        }
    }

    public static string ProjectsDir => Path.Combine(ClaudeDir, "projects");
    public static string LiveSessionsDir => Path.Combine(ClaudeDir, "sessions");
    public static string SkillsDir => Path.Combine(ClaudeDir, "skills");
    public static string AgentsDir => Path.Combine(ClaudeDir, "agents");
    public static string CommandsDir => Path.Combine(ClaudeDir, "commands");
    public static string PluginsDir => Path.Combine(ClaudeDir, "plugins");
    public static string SettingsFile => Path.Combine(ClaudeDir, "settings.json");
    public static string UserMemoryFile => Path.Combine(ClaudeDir, "CLAUDE.md");

    /// <summary>Overseer's own folder: $OVERSEER_HOME or ~/.overseer.</summary>
    public static string OverseerDir
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("OVERSEER_HOME");
            var dir = !string.IsNullOrWhiteSpace(env) ? env : Path.Combine(Home, ".overseer");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string EventsDir => Ensure(Path.Combine(OverseerDir, "events"));
    public static string StatusLineDir => Ensure(Path.Combine(OverseerDir, "statusline"));
    public static string CacheDir => Ensure(Path.Combine(OverseerDir, "cache"));
    public static string PluginsHostDir => Ensure(Path.Combine(OverseerDir, "plugins"));
    public static string TrashDir => Ensure(Path.Combine(OverseerDir, "trash"));
    public static string ConfigFile => Path.Combine(OverseerDir, "config.json");
    public static string PresetsFile => Path.Combine(OverseerDir, "presets.json");
    public static string PricesFile => Path.Combine(OverseerDir, "prices.json");

    static string Ensure(string d) { Directory.CreateDirectory(d); return d; }

    /// <summary>Shortens a path for display: C:\…\last-folder.</summary>
    public static string Short(string? p, int max = 48)
    {
        if (string.IsNullOrEmpty(p) || p.Length <= max) return p ?? "";
        var parts = p.Split('\\', '/');
        return parts.Length > 2 ? parts[0] + Path.DirectorySeparatorChar + "…" + Path.DirectorySeparatorChar + parts[^1] : p[..(max - 1)] + "…";
    }
}

/// <summary>
/// Errors Overseer recovered from, for bug reports: ~/.overseer/errors.log (kept under 1 MB).
/// Holds exception messages and stack traces only.
/// </summary>
public static class ErrorLog
{
    static readonly object Gate = new();
    public static string File => Path.Combine(Paths.OverseerDir, "errors.log");

    public static void Write(Exception ex, string where = "")
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Paths.OverseerDir);
                var f = new FileInfo(File);
                if (f.Exists && f.Length > 1_000_000) f.Delete();
                System.IO.File.AppendAllText(File, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} Overseer {Updater.CurrentText} {where}\n{ex}\n\n");
            }
        }
        catch { /* logging must never throw */ }
    }
}
