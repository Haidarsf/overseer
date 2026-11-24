using System.Text.Json.Nodes;
using Overseer.Core;
using Xunit;

namespace Overseer.Core.Tests;

public class TranscriptParserTests
{
    static Session Parse()
    {
        var s = new Session { Id = "abc", FilePath = "x" };
        foreach (var line in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "session.jsonl")))
        {
            try { TranscriptParser.ApplyLine(s, line); } catch { /* the store skips bad lines the same way */ }
        }
        return s;
    }

    [Fact]
    public void Reads_metadata()
    {
        var s = Parse();
        Assert.Equal("/home/dev/project", s.Cwd);
        Assert.Equal("2.1.330", s.Version);
        Assert.Equal("cli", s.Entrypoint);
        Assert.Equal("claude-opus-5-5", s.Model);
        Assert.Equal("Opus 5.5", s.ModelName);
        Assert.Equal(new[] { "pdf", "docs", "my-plugin:deploy" }, s.SkillList);
    }

    [Fact]
    public void Counts_usage_once_per_message_and_keeps_the_final_output_count()
    {
        var s = Parse();
        Assert.Equal(2, s.Calls);
        Assert.Equal(30, s.Input);            // 10 + 20, the repeated msg_1 chunk is not double counted
        Assert.Equal(100, s.Output);          // msg_1 grows from 5 to 40, plus 60
        Assert.Equal(2500, s.CacheRead);
        Assert.Equal(200, s.CacheWrite);
        Assert.Equal(20 + 1500 + 0, s.LastContext);
        Assert.Equal(2, s.Usages.Count);
        Assert.Equal(40, s.Usages[0].Output);
    }

    [Fact]
    public void Records_tools_mcp_skills_files_and_errors()
    {
        var s = Parse();
        Assert.Equal(4, s.ToolCalls);
        Assert.Equal(1, s.Tools["Bash"]);
        Assert.Equal(1, s.Mcp["github"]);
        Assert.Equal(1, s.Skills["pdf"]);
        Assert.Equal(1, s.FilesChanged["/home/dev/project/src/app.ts"]);
        Assert.Equal(1, s.Errors);
        Assert.Contains(s.Events, e => e.Kind == EventKind.Tool && e.Name == "Bash" && e.Detail == "Run the tests");
        Assert.Contains(s.Events, e => e.Kind == EventKind.Tool && e.Name == "github · create_issue");
    }

    [Fact]
    public void Ignores_system_wrappers_and_meta_messages()
    {
        var s = Parse();
        Assert.Equal(1, s.Prompts);
        Assert.Equal("Please fix the failing test in src/app.ts", s.FirstPrompt);
        Assert.DoesNotContain(s.Events, e => e.Detail.Contains("compact"));
    }

    [Fact]
    public void Infers_context_window()
    {
        var s = new Session { Model = "claude-sonnet-4-6", LastContext = 150_000 };
        Assert.Equal(200_000, s.ContextWindow);
        s.LastContext = 250_000;
        Assert.Equal(1_000_000, s.ContextWindow);
        s.Status = new StatusSnapshot { ContextWindowSize = 400_000 };
        Assert.Equal(400_000, s.ContextWindow);
        Assert.True(s.ContextWindowIsExact);
    }
}

[Collection("settings-file")]
public sealed class SettingsFileTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("overseer-test-").FullName;
    string FilePath => Path.Combine(dir, "settings.json");

    public SettingsFileTests() => SettingsFile.PathOverride = FilePath;
    public void Dispose() { SettingsFile.PathOverride = null; try { Directory.Delete(dir, true); } catch { } }

    [Fact]
    public void Set_keeps_other_keys_and_order()
    {
        File.WriteAllText(FilePath, "{\"theme\":\"dark\",\"modelSettings\":{\"claude-opus-5-5\":{\"effortLevel\":\"medium\"}},\"z\":1}");
        Assert.True(SettingsFile.SetString("opus", "model").Ok);
        Assert.True(SettingsFile.SetString("high", "modelSettings", "claude-opus-5-5", "effortLevel").Ok);
        var o = JsonNode.Parse(File.ReadAllText(FilePath))!.AsObject();
        Assert.Equal(new[] { "theme", "modelSettings", "z", "model" }, o.Select(kv => kv.Key).ToArray());
        Assert.Equal("high", o["modelSettings"]!["claude-opus-5-5"]!["effortLevel"]!.GetValue<string>());
        Assert.Equal(1, o["z"]!.GetValue<int>());
    }

    [Fact]
    public void Removing_a_value_prunes_empty_parents()
    {
        File.WriteAllText(FilePath, "{\"theme\":\"dark\"}");
        SettingsFile.SetString("plan", "permissions", "defaultMode");
        SettingsFile.SetString(null, "permissions", "defaultMode");
        var o = JsonNode.Parse(File.ReadAllText(FilePath))!.AsObject();
        Assert.False(o.ContainsKey("permissions"));
        Assert.Equal("dark", o["theme"]!.GetValue<string>());
    }

    [Fact]
    public void Refuses_to_touch_invalid_json()
    {
        File.WriteAllText(FilePath, "{ this is not json");
        var r = SettingsFile.SetString("opus", "model");
        Assert.False(r.Ok);
        Assert.Equal("{ this is not json", File.ReadAllText(FilePath));
    }

    [Fact]
    public void Permission_rules_add_and_remove()
    {
        File.WriteAllText(FilePath, "{}");
        var first = SettingsFile.AddRule("allow", "Bash(npm test:*)");
        Assert.True(first.Ok, first.Message);
        var again = SettingsFile.AddRule("allow", "Bash(npm test:*)"); // no duplicates
        Assert.True(again.Ok, again.Message);
        Assert.Equal(new[] { "Bash(npm test:*)" }, SettingsFile.Rules("allow"));
        SettingsFile.RemoveRule("allow", "Bash(npm test:*)");
        Assert.Empty(SettingsFile.Rules("allow"));
        Assert.Equal("{}", File.ReadAllText(FilePath).Trim());
    }

    [Fact]
    public void Hooks_install_and_uninstall_leave_other_hooks_alone()
    {
        File.WriteAllText(FilePath, "{\"hooks\":{\"Stop\":[{\"hooks\":[{\"type\":\"command\",\"command\":\"say done\"}]}]}}");
        Assert.True(Integration.InstallHooks("/opt/overseer/Overseer").Ok);
        Assert.True(Integration.HooksInstalled());
        var rows = SettingsFile.HookRows();
        Assert.Contains(rows, r => r.Event == "Stop" && r.Command == "say done");
        Assert.Contains(rows, r => r.Event == "PreToolUse" && r.Matcher == "*" && r.Command.EndsWith(" hook"));

        Integration.InstallHooks("/opt/overseer/Overseer"); // idempotent
        Assert.Equal(Integration.HookEvents.Length + 1, SettingsFile.HookRows().Count);

        Assert.True(Integration.UninstallHooks().Ok);
        Assert.False(Integration.HooksInstalled());
        Assert.Equal(new[] { ("Stop", (string?)null, "say done") }, SettingsFile.HookRows());
    }

    [Fact]
    public void Status_line_is_not_replaced_without_consent()
    {
        File.WriteAllText(FilePath, "{\"statusLine\":{\"type\":\"command\",\"command\":\"my-line.sh\"}}");
        Assert.False(Integration.InstallStatusLine("/opt/overseer/Overseer", replaceExisting: false).Ok);
        Assert.Equal("my-line.sh", Integration.CurrentStatusLineCommand());
        Assert.True(Integration.InstallStatusLine("/opt/overseer/Overseer", replaceExisting: true).Ok);
        Assert.True(Integration.StatusLineInstalled());
    }

    [Fact]
    public void Skill_overrides()
    {
        File.WriteAllText(FilePath, "{}");
        SettingsFile.SetSkillState("pdf", "off");
        Assert.Equal("off", SettingsFile.SkillState("pdf"));
        SettingsFile.SetSkillState("pdf", "on");
        Assert.Equal("on", SettingsFile.SkillState("pdf"));
        Assert.Equal("{}", File.ReadAllText(FilePath).Trim());
    }

    [Fact]
    public void Backup_is_written_before_the_first_change()
    {
        File.WriteAllText(FilePath, "{\"original\":true}");
        SettingsFile.SetString("opus", "model");
        Assert.True(File.Exists(SettingsFile.BackupPath));
        Assert.Contains("original", File.ReadAllText(SettingsFile.BackupPath));
    }
}

[Collection("overseer-home")]
public class HookAndStatusLineTests
{
    [Fact]
    public void Hook_writes_one_line_and_prints_nothing()
    {
        var home = Directory.CreateTempSubdirectory("overseer-home-").FullName;
        Environment.SetEnvironmentVariable("OVERSEER_HOME", home);
        try
        {
            var json = "{\"session_id\":\"s1\",\"hook_event_name\":\"PreToolUse\",\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"ls\"},\"tool_response\":\"big\"}";
            Assert.Equal(0, Integration.RunHook(new StringReader(json)));
            var lines = File.ReadAllLines(Path.Combine(home, "events", "s1.jsonl"));
            Assert.Single(lines);
            var o = JsonNode.Parse(lines[0])!;
            Assert.Equal("Bash", o["tool_name"]!.GetValue<string>());
            Assert.Null(o["tool_response"]);
            Assert.NotNull(o["_overseer_time"]);

            Assert.Equal(0, Integration.RunHook(new StringReader("not json")));        // never fails
            Assert.Equal(0, Integration.RunHook(new StringReader("{\"session_id\":\"../evil\"}"))); // never escapes the folder
        }
        finally { Environment.SetEnvironmentVariable("OVERSEER_HOME", null); }
    }

    [Fact]
    public void Status_line_parses_limits_and_renders()
    {
        var raw = """
        {"session_id":"s1","model":{"id":"claude-opus-5-5","display_name":"Opus 5.5"},"cost":{"total_cost_usd":1.234},
         "context_window":{"used_percentage":42.4,"context_window_size":1000000},
         "rate_limits":{"five_hour":{"used_percentage":63,"resets_at":1795600000},"seven_day":{"used_percentage":12.5,"resets_at":1796000000}}}
        """;
        var s = StatusLine.Parse(raw)!;
        Assert.Equal(42.4, s.ContextUsedPercent);
        Assert.Equal(1_000_000, s.ContextWindowSize);
        Assert.Equal(63, s.FiveHourUsedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1795600000).LocalDateTime, s.FiveHourResetsAt);
        Assert.Equal("Opus 5.5 · ctx 42% · 5h 63% · 7d 13% · $1.23", StatusLine.Render(s));
    }
}

public class ReportAndHelperTests
{
    [Fact]
    public void Price_table_matches_the_longest_prefix()
    {
        var p = PriceTable.Defaults();
        Assert.Equal(4, p.For("claude-opus-5-5")!.Input);
        Assert.Equal(5, p.For("claude-opus-5")!.Input);
        Assert.Equal(4, p.For("claude-opus-5-5[1m]")!.Input);
        Assert.Null(p.For("gpt-something"));
        var u = new UsageRecord { Model = "claude-sonnet-5-5", Input = 1_000_000, Output = 1_000_000 };
        Assert.Equal(12, p.Cost(u), 3);
    }

    [Fact]
    public void Reports_group_and_export_csv()
    {
        var d = new DateTime(2026, 11, 24, 10, 0, 0);
        var usage = new[]
        {
            new UsageRecord { Time = d, Model = "claude-opus-5-5", Project = "/a", SessionId = "1", Input = 100, Output = 10 },
            new UsageRecord { Time = d.AddHours(1), Model = "claude-opus-5-5", Project = "/a", SessionId = "1", Input = 50, Output = 5 },
            new UsageRecord { Time = d.AddDays(-1), Model = "claude-haiku-5-5", Project = "/b, c", SessionId = "2", Input = 7 },
        };
        var byProject = Reports.Build(usage, d.AddDays(-2), d.AddDays(1), ReportGroup.Project, PriceTable.Defaults());
        Assert.Equal("/a", byProject[0].Key);
        Assert.Equal(165, byProject[0].Billable);
        var csv = Reports.ToCsv(byProject, "project");
        Assert.Contains("\"/b, c\"", csv);
        var byDay = Reports.Build(usage, d.AddDays(-2), d.AddDays(1), ReportGroup.Day, PriceTable.Defaults());
        Assert.Equal(new[] { "2026-11-23", "2026-11-24" }, byDay.Select(r => r.Key));
    }

    [Fact]
    public void Redactor_hides_paths_emails_and_prompts()
    {
        Redactor.Enabled = true;
        try
        {
            Assert.Equal("open …\\notes.txt now", Redactor.Text(@"open C:\Users\me\secret\notes.txt now"));
            Assert.Equal("edit …/app.ts", Redactor.Text("edit /home/me/project/app.ts"));
            Assert.Equal("mail •••@•••", Redactor.Text("mail me@example.com"));
            Assert.Equal("••••••••", Redactor.Prompt("my private prompt"));
        }
        finally { Redactor.Enabled = false; }
        Assert.Equal("my prompt", Redactor.Prompt("my prompt"));
    }

    [Theory]
    [InlineData(999, "999")]
    [InlineData(1500, "1.5k")]
    [InlineData(2_340_000, "2.34M")]
    public void Formats_tokens(long n, string expected) => Assert.Equal(expected, Text.Tokens(n));

    [Theory]
    [InlineData("claude-opus-5-5", "Opus 5.5")]
    [InlineData("claude-sonnet-4-6[1m]", "Sonnet 4.6")]
    [InlineData("custom-model", "custom-model")]
    public void Pretty_model_names(string id, string expected) => Assert.Equal(expected, Text.PrettyModel(id));

    [Fact]
    public void Shell_quoting_is_safe() => Assert.Equal("'it'\\''s; rm -rf /'", Terminal.Sh("it's; rm -rf /"));
}

public class SearchAndConversationTests
{
    static Session Fixture() => new() { Id = "abc", FilePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "session.jsonl") };

    [Fact]
    public void Finds_prompts_replies_and_tool_calls()
    {
        var s = Fixture();
        var hits = TranscriptSearch.Search(new[] { s }, "failing test", 10, CancellationToken.None);
        Assert.Contains(hits, h => h.Role == "You" && h.Snippet.Contains("failing test"));
        var tool = TranscriptSearch.Search(new[] { s }, "Run the tests", 10, CancellationToken.None);
        Assert.Contains(tool, h => h.Role == "Tool");
        Assert.Empty(TranscriptSearch.Search(new[] { s }, "nothing-like-this", 10, CancellationToken.None));
    }

    [Fact]
    public void Conversation_skips_system_wrappers()
    {
        var items = Conversation.Read(Fixture().FilePath);
        Assert.Equal("You", items[0].Role);
        Assert.DoesNotContain(items, i => i.Text.Contains("system-reminder") || i.Text.Contains("command-name"));
        Assert.Contains(items, i => i.Role == "Tool" && i.Tool == "github · create_issue");
    }
}

[Collection("settings-file")]
public sealed class IntegrationRemovalTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("overseer-rm-").FullName;
    string FilePath => Path.Combine(dir, "settings.json");
    public IntegrationRemovalTests() => SettingsFile.PathOverride = FilePath;
    public void Dispose() { SettingsFile.PathOverride = null; try { Directory.Delete(dir, true); } catch { } }

    [Fact]
    public void Removes_a_partial_install_and_keeps_quotes_readable()
    {
        File.WriteAllText(FilePath, "{\"hooks\":{\"Stop\":[{\"hooks\":[{\"type\":\"command\",\"command\":\"say done\"}]},{\"hooks\":[{\"type\":\"command\",\"command\":\"\\\"C:/x/Overseer.exe\\\" hook\"}]}]}}");
        Assert.True(Integration.AnyHookInstalled());
        Assert.False(Integration.HooksInstalled());
        Assert.True(Integration.UninstallHooks().Ok);
        Assert.False(Integration.AnyHookInstalled());
        var text = File.ReadAllText(FilePath);
        Assert.Contains("say done", text);
        Assert.DoesNotContain(((char)92) + "u0022", text); // the escaped form of a quote must not appear
    }
}

[Collection("overseer-home")]
public sealed class PermissionBridgeTests : IDisposable
{
    readonly string home = Directory.CreateTempSubdirectory("overseer-perm-").FullName;
    public PermissionBridgeTests() => Environment.SetEnvironmentVariable("OVERSEER_HOME", home);
    public void Dispose() { Environment.SetEnvironmentVariable("OVERSEER_HOME", null); try { Directory.Delete(home, true); } catch { } }

    const string Request = "{\"session_id\":\"s1\",\"cwd\":\"C:/code/web\",\"hook_event_name\":\"PermissionRequest\",\"tool_name\":\"Bash\"," +
        "\"tool_input\":{\"command\":\"npm test -- --watch=false\",\"description\":\"Run the tests\"}," +
        "\"permission_suggestions\":[{\"type\":\"addRules\",\"rules\":[{\"toolName\":\"Bash\",\"ruleContent\":\"npm test:*\"}],\"behavior\":\"allow\",\"destination\":\"localSettings\"}]}";

    [Fact]
    public void Does_nothing_when_the_app_is_not_running()
    {
        var output = new StringWriter();
        Assert.Equal(0, PermissionBridge.RunHook(new StringReader(Request), output, TimeSpan.FromSeconds(5)));
        Assert.Equal("", output.ToString());
        Assert.Empty(PermissionBridge.Pending());
    }

    [Fact]
    public async Task Waits_for_an_answer_from_the_app()
    {
        PermissionBridge.Heartbeat();
        var output = new StringWriter();
        var hook = Task.Run(() => PermissionBridge.RunHook(new StringReader(Request), output, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(20)));

        PendingPermission? p = null;
        for (int i = 0; i < 200 && p == null; i++) { p = PermissionBridge.Pending().FirstOrDefault(); if (p == null) Thread.Sleep(20); }
        Assert.NotNull(p);
        Assert.Equal("s1", p!.SessionId);
        Assert.Equal("npm test -- --watch=false", p.Detail);
        Assert.Equal("Run the tests", p.Reason);
        Assert.Equal("Bash(npm test:*)", p.Rule);

        Assert.True(PermissionBridge.Answer(p.Id, PermissionAnswer.Deny("run only the settings tests")).Ok);
        Assert.Equal(0, await hook);
        var o = JsonNode.Parse(output.ToString())!["hookSpecificOutput"]!;
        Assert.Equal("PermissionRequest", o["hookEventName"]!.GetValue<string>());
        Assert.Equal("deny", o["decision"]!["behavior"]!.GetValue<string>());
        Assert.Equal("run only the settings tests", o["decision"]!["message"]!.GetValue<string>());
        Assert.Empty(PermissionBridge.Pending());
        Assert.False(PermissionBridge.Answer(p.Id, PermissionAnswer.AllowOnce()).Ok); // already answered
    }

    [Fact]
    public void Times_out_quietly()
    {
        PermissionBridge.Heartbeat();
        var output = new StringWriter();
        Assert.Equal(0, PermissionBridge.RunHook(new StringReader(Request), output, TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(20)));
        Assert.Equal("", output.ToString());
        Assert.Empty(PermissionBridge.Pending());
    }

    [Fact]
    public void Always_allow_uses_the_suggestion_with_the_chosen_place()
    {
        var input = (JsonObject)JsonNode.Parse(Request)!;
        var d = JsonNode.Parse(PermissionBridge.BuildOutput(input, PermissionAnswer.AllowAlways("userSettings")))!["hookSpecificOutput"]!["decision"]!;
        Assert.Equal("allow", d["behavior"]!.GetValue<string>());
        var u = d["updatedPermissions"]![0]!;
        Assert.Equal("addRules", u["type"]!.GetValue<string>());
        Assert.Equal("userSettings", u["destination"]!.GetValue<string>());
        Assert.Equal("npm test:*", u["rules"]![0]!["ruleContent"]!.GetValue<string>());

        var once = JsonNode.Parse(PermissionBridge.BuildOutput(input, PermissionAnswer.AllowOnce()))!["hookSpecificOutput"]!["decision"]!;
        Assert.Null(once["updatedPermissions"]);

        var noSuggestion = (JsonObject)JsonNode.Parse("{\"tool_name\":\"WebFetch\",\"tool_input\":{\"url\":\"https://x\"}}")!;
        Assert.Equal("WebFetch", PermissionBridge.RuleText(noSuggestion));
    }

    [Fact]
    public void Removes_one_allow_rule_from_a_project_file()
    {
        var file = Path.Combine(home, "settings.local.json");
        File.WriteAllText(file, "{\"permissions\":{\"allow\":[\"Read(./src/**)\",\"Bash(npm test:*)\"]},\"model\":\"opus\"}");
        Assert.True(PermissionBridge.RemoveAllowRule(file, "Bash(npm test:*)").Ok);
        var o = JsonNode.Parse(File.ReadAllText(file))!;
        Assert.Single(o["permissions"]!["allow"]!.AsArray());
        Assert.Equal("opus", o["model"]!.GetValue<string>());
        Assert.False(PermissionBridge.RemoveAllowRule(file, "Bash(npm test:*)").Ok);
    }
}

[Collection("settings-file")]
public sealed class PermissionHookInstallTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("overseer-ph-").FullName;
    string FilePath => Path.Combine(dir, "settings.json");
    public PermissionHookInstallTests() => SettingsFile.PathOverride = FilePath;
    public void Dispose() { SettingsFile.PathOverride = null; try { Directory.Delete(dir, true); } catch { } }

    [Fact]
    public void Installs_a_synchronous_hook_next_to_the_event_hooks()
    {
        File.WriteAllText(FilePath, "{}");
        Assert.True(Integration.InstallHooks("C:/x/Overseer.exe").Ok);
        Assert.True(Integration.InstallPermissionHook("C:/x/Overseer.exe", 60).Ok);
        Assert.True(Integration.PermissionHookInstalled());
        var groups = JsonNode.Parse(File.ReadAllText(FilePath))!["hooks"]!["PermissionRequest"]!.AsArray();
        Assert.Equal(2, groups.Count);
        var mine = groups.Select(g => g!["hooks"]![0]!).Single(h => h["command"]!.GetValue<string>().EndsWith(" permission"));
        Assert.Null(mine["async"]);
        Assert.Equal(75, mine["timeout"]!.GetValue<int>());

        Assert.True(Integration.UninstallPermissionHook().Ok);
        Assert.False(Integration.PermissionHookInstalled());
        Assert.True(Integration.HooksInstalled()); // the event hooks stay
    }
}
