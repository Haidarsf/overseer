using System.Text.Json.Serialization;

namespace Overseer.Core;

public enum EventKind { Tool, Prompt, Reply, Error, Notification, Permission, Stop }

public sealed record ActivityEvent(DateTime Time, EventKind Kind, string Name, string Detail, string? Tool = null);

public sealed class UsageRecord
{
    public DateTime Time { get; set; }
    public string? Model { get; set; }
    public string SessionId { get; set; } = "";
    public string? Project { get; set; }
    public long Input { get; set; }
    public long Output { get; set; }
    public long CacheRead { get; set; }
    public long CacheWrite { get; set; }
    [JsonIgnore] public long Billable => Input + Output + CacheWrite;
    [JsonIgnore] public long Total => Input + Output + CacheWrite + CacheRead;
}

/// <summary>Values reported by Claude Code's status line (exact, when the Overseer status line is installed).</summary>
public sealed class StatusSnapshot
{
    public DateTime Updated { get; set; }
    public string? ModelId { get; set; }
    public string? ModelName { get; set; }
    public double? CostUsd { get; set; }
    public double? ContextUsedPercent { get; set; }
    public long? ContextWindowSize { get; set; }
    public double? FiveHourUsedPercent { get; set; }
    public DateTime? FiveHourResetsAt { get; set; }
    public double? WeeklyUsedPercent { get; set; }
    public DateTime? WeeklyResetsAt { get; set; }
}

/// <summary>Latest state reported by Overseer's hooks for one session.</summary>
public sealed class LiveState
{
    public DateTime Updated { get; set; }
    public string? LastHookEvent { get; set; }
    public string? RunningTool { get; set; }
    public string? RunningDetail { get; set; }
    public bool WaitingForPermission { get; set; }
    public bool WaitingForInput { get; set; }
    public string? Message { get; set; }
    public bool Ended { get; set; }
}

public sealed class Session
{
    public string Id { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string? ParentId { get; set; }
    public bool IsSubagent { get; set; }
    public string? AgentDescription { get; set; }

    public string? CustomTitle { get; set; }
    public string? FirstPrompt { get; set; }
    public string? Cwd { get; set; }
    public string? Model { get; set; }
    public string? ModelName { get; set; }
    public string? Version { get; set; }
    public string? Entrypoint { get; set; }
    public string? PermissionMode { get; set; }

    public long Offset { get; set; }
    public DateTime First { get; set; }
    public DateTime Last { get; set; }

    public long Input { get; set; }
    public long Output { get; set; }
    public long CacheRead { get; set; }
    public long CacheWrite { get; set; }
    public long LastContext { get; set; }
    public int Calls { get; set; }
    public int Prompts { get; set; }
    public int Errors { get; set; }
    public int ToolCalls { get; set; }
    public int AgentCalls { get; set; }

    public Dictionary<string, int> Tools { get; set; } = new();
    public Dictionary<string, int> Skills { get; set; } = new();
    public Dictionary<string, int> Mcp { get; set; } = new();
    public Dictionary<string, int> FilesChanged { get; set; } = new();
    public List<UsageRecord> Usages { get; set; } = new();
    public List<ActivityEvent> Events { get; set; } = new();
    public List<string> SkillList { get; set; } = new();
    /// <summary>message id → output tokens, for the last few messages (streaming chunks repeat usage).</summary>
    public Dictionary<string, long> RecentMessages { get; set; } = new();

    // live, never cached
    [JsonIgnore] public bool IsLive { get; set; }
    [JsonIgnore] public string? LiveStatus { get; set; }
    [JsonIgnore] public string? LiveName { get; set; }
    [JsonIgnore] public int Pid { get; set; }
    [JsonIgnore] public LiveState? Hook { get; set; }
    [JsonIgnore] public StatusSnapshot? Status { get; set; }

    [JsonIgnore] public long TotalTokens => Input + Output + CacheRead + CacheWrite;

    [JsonIgnore]
    public string Title => !string.IsNullOrWhiteSpace(LiveName) ? LiveName!
        : !string.IsNullOrWhiteSpace(CustomTitle) ? CustomTitle!
        : !string.IsNullOrWhiteSpace(FirstPrompt) ? FirstPrompt!
        : Id;

    /// <summary>Context window: exact from the status line when available, otherwise inferred.</summary>
    [JsonIgnore]
    public long ContextWindow => Status?.ContextWindowSize is > 0 ? Status.ContextWindowSize.Value
        : (Model ?? "").Contains("[1m]") || LastContext > 200_000 ? 1_000_000 : 200_000;

    [JsonIgnore] public bool ContextWindowIsExact => Status?.ContextWindowSize is > 0;

    [JsonIgnore]
    public SessionState State
    {
        get
        {
            if (IsLive)
            {
                if (Hook is { WaitingForPermission: true } h && h.Updated > Last.AddSeconds(-1)) return SessionState.NeedsPermission;
                if (LiveStatus == "busy") return SessionState.Working;
                return SessionState.Idle;
            }
            return Last > DateTime.Now.AddMinutes(-2) ? SessionState.Active : SessionState.Ended;
        }
    }
}

public enum SessionState { Working, NeedsPermission, Idle, Active, Ended }

public sealed record SkillInfo(string Name, string? Description, string Source, string Folder);
public sealed record MarkdownItem(string Name, string? Description, string FilePath);

public sealed class PluginRow
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? Scope { get; set; }
    public string? Version { get; set; }
    public string? Marketplace { get; set; }
    public bool Installed { get; set; }
    public bool Enabled { get; set; }
}

public sealed record ActionResult(bool Ok, string Message, string? Output = null)
{
    public static ActionResult Good(string m) => new(true, m);
    public static ActionResult Bad(string m) => new(false, m);
}
