using Overseer.Core;
using Xunit;

namespace Overseer.Core.Tests;

public sealed class UsageHistoryTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "overseer-history-" + Guid.NewGuid().ToString("N"));
    string File => Path.Combine(dir, "usage-history.json");

    public UsageHistoryTests() => Directory.CreateDirectory(dir);
    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

    static Session Session(string file, long output = 20, bool sub = false, string? title = "Fix the login bug")
    {
        var s = new Session { Id = Path.GetFileNameWithoutExtension(file), FilePath = file, IsSubagent = sub, CustomTitle = title, Output = output };
        s.Usages.Add(new UsageRecord { Time = new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Local), Model = "claude-opus-5-5", SessionId = s.Id, Project = @"C:\code\app", Input = 10, Output = output, CacheWrite = 5 });
        return s;
    }

    [Fact]
    public void Saves_and_loads_usage()
    {
        var h = new UsageHistory(File);
        Assert.True(h.Update(new[] { Session("p/a.jsonl") }));
        h.Save();

        var again = new UsageHistory(File);
        again.Load();
        var gone = again.Gone(new List<string>());
        var e = Assert.Single(gone);
        Assert.Equal("a", e.Id);
        Assert.Equal("Fix the login bug", e.Title);
        var u = Assert.Single(e.Usages);
        Assert.Equal(35, u.Billable);
        Assert.Equal("claude-opus-5-5", u.Model);
    }

    [Fact]
    public void Only_transcripts_that_are_gone_come_from_the_history()
    {
        var h = new UsageHistory(File);
        h.Update(new[] { Session("p/a.jsonl"), Session("p/b.jsonl") });

        var gone = h.Gone(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "P/A.jsonl" });

        Assert.Equal("b", Assert.Single(gone).Id);
    }

    [Fact]
    public void Unchanged_sessions_are_not_rewritten_and_growth_is_picked_up()
    {
        var h = new UsageHistory(File);
        h.Update(new[] { Session("p/a.jsonl") });
        Assert.False(h.Update(new[] { Session("p/a.jsonl") }));

        // a streaming reply finishing raises the output count without adding a record
        Assert.True(h.Update(new[] { Session("p/a.jsonl", output: 400) }));
        Assert.Equal(400, h.Gone(new List<string>())[0].Output);
    }

    [Fact]
    public void Keeps_short_titles_and_none_for_subagents()
    {
        var h = new UsageHistory(File);
        h.Update(new[] { Session("p/a.jsonl", title: new string('x', 500)), Session("p/a/subagents/agent-1.jsonl", sub: true) });
        var gone = h.Gone(new List<string>());
        Assert.True(gone.Single(e => !e.IsSubagent).Title!.Length <= 120);
        Assert.Null(gone.Single(e => e.IsSubagent).Title);
    }

    [Fact]
    public void Sessions_without_usage_are_skipped_and_clear_deletes_the_file()
    {
        var h = new UsageHistory(File);
        Assert.False(h.Update(new[] { new Session { Id = "empty", FilePath = "p/empty.jsonl" } }));
        h.Update(new[] { Session("p/a.jsonl") });
        h.Save();
        Assert.True(System.IO.File.Exists(File));

        h.Clear();

        Assert.False(System.IO.File.Exists(File));
        Assert.Equal(0, h.Count);
    }
}

[CollectionDefinition("Environment", DisableParallelization = true)]
public sealed class EnvironmentCollection { }

[Collection("Environment")]
public sealed class StoreHistoryTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "overseer-store-" + Guid.NewGuid().ToString("N"));

    public StoreHistoryTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "claude", "projects", "C--code-app"));
        Directory.CreateDirectory(Path.Combine(root, "overseer"));
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", Path.Combine(root, "claude"));
        Environment.SetEnvironmentVariable("OVERSEER_HOME", Path.Combine(root, "overseer"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", null);
        Environment.SetEnvironmentVariable("OVERSEER_HOME", null);
        try { Directory.Delete(root, true); } catch { }
    }

    [Fact]
    public void Usage_survives_Claude_Code_deleting_the_transcript()
    {
        var transcript = Path.Combine(root, "claude", "projects", "C--code-app", "11111111-2222-3333-4444-555555555555.jsonl");
        System.IO.File.WriteAllText(transcript,
            "{\"type\":\"assistant\",\"timestamp\":\"2026-09-01T10:00:00Z\",\"cwd\":\"C:\\\\code\\\\app\",\"message\":{\"id\":\"msg_1\",\"model\":\"claude-opus-5-5\",\"usage\":{\"input_tokens\":10,\"output_tokens\":20,\"cache_read_input_tokens\":100,\"cache_creation_input_tokens\":5}}}\n");
        var historyFile = Path.Combine(root, "overseer", "usage-history.json");

        var first = new SessionStore();
        first.OpenHistory(historyFile);
        first.Scan();
        first.SaveHistory();
        Assert.Single(first.AllUsage());

        System.IO.File.Delete(transcript);   // what cleanupPeriodDays does after 30 days

        var later = new SessionStore();
        later.OpenHistory(historyFile);
        later.Scan();
        var u = Assert.Single(later.AllUsage());
        Assert.Equal(35, u.Billable);
        Assert.Equal(100, u.CacheRead);
        Assert.Equal(new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc).ToLocalTime(), u.Time);
        Assert.True(later.SessionTitles().ContainsKey("11111111-2222-3333-4444-555555555555"));

        later.ClearHistory();
        Assert.Empty(later.AllUsage());
    }
}
