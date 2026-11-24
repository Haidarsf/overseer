using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Overseer.Core;

/// <summary>
/// Writes a realistic but entirely made-up Claude Code folder, so Overseer can be tried (and screenshotted)
/// without real sessions: `overseer --demo`.
/// </summary>
public static class Demo
{
    sealed record Script(string Title, string Project, string Model, int AgeMinutes, int Steps, bool Live, string Status, (string tool, string detail)[] Tools);

    static readonly Script[] Scripts =
    {
        new("Add dark mode to the settings page", "~/code/acme-web", "claude-opus-5-5", 0, 34, true, "busy", new[]
        {
            ("Read", "src/settings/SettingsPage.tsx"), ("Grep", "useTheme"), ("Edit", "src/theme/tokens.ts"), ("Bash", "Run the unit tests"),
            ("Edit", "src/settings/SettingsPage.tsx"), ("mcp__github__create_pull_request", "Dark mode for settings"), ("Bash", "Run the linter"), ("Skill", "frontend-design"),
        }),
        new("Why is the nightly import slow?", "~/code/data-pipeline", "claude-sonnet-5-5", 7, 22, true, "idle", new[]
        {
            ("Bash", "Profile the import job"), ("Read", "pipeline/import.py"), ("Grep", "executemany"), ("Edit", "pipeline/import.py"), ("Bash", "Re-run the import on a sample"),
        }),
        new("Write release notes for v2.4", "~/code/acme-web", "claude-haiku-5-5", 95, 9, false, "", new[]
        {
            ("Bash", "List commits since v2.3"), ("Read", "CHANGELOG.md"), ("Write", "docs/release-notes/2.4.md"),
        }),
        new("Migrate CI to the new runners", "~/code/infra", "claude-opus-5-5", 260, 41, false, "", new[]
        {
            ("Read", ".github/workflows/ci.yml"), ("Edit", ".github/workflows/ci.yml"), ("WebFetch", "Runner migration guide"), ("Agent", "Check every workflow for old labels"),
            ("Bash", "Validate workflow syntax"), ("mcp__github__list_workflow_runs", "Recent runs"),
        }),
        new("Fix flaky checkout test", "~/code/shop-api", "claude-sonnet-5-5", 1500, 28, false, "", new[]
        {
            ("Bash", "Run the checkout tests 20 times"), ("Read", "tests/checkout.test.ts"), ("Edit", "tests/checkout.test.ts"), ("Bash", "Run the checkout tests 20 times"),
        }),
        new("Explain the billing module", "~/code/shop-api", "claude-fable-5-2", 2900, 12, false, "", new[]
        {
            ("Glob", "src/billing/**/*.ts"), ("Read", "src/billing/invoice.ts"), ("Read", "src/billing/tax.ts"),
        }),
    };

    /// <summary>Creates the demo folder and returns (claudeDir, overseerHome).</summary>
    public static (string claudeDir, string overseerHome) Create(string root)
    {
        var claude = Path.Combine(root, "claude");
        var overseer = Path.Combine(root, "overseer");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(Path.Combine(claude, "sessions"));
        Directory.CreateDirectory(Path.Combine(overseer, "statusline"));
        var rnd = new Random(42);
        var now = DateTime.UtcNow;
        int n = 0;
        foreach (var sc in Scripts)
        {
            var id = $"00000000-0000-4000-8000-{++n:D12}";
            var projDir = Path.Combine(claude, "projects", sc.Project.Replace("~/", "-home-demo-").Replace('/', '-'));
            Directory.CreateDirectory(projDir);
            var cwd = sc.Project.Replace("~", "/home/demo");
            var sb = new StringBuilder();
            var t = now.AddMinutes(-sc.AgeMinutes - sc.Steps * 1.5);
            string Ts() => t.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            void Line(JsonObject o) { o["timestamp"] = Ts(); o["cwd"] = cwd; o["version"] = "2.1.330"; o["entrypoint"] = "cli"; o["permissionMode"] = "default"; sb.Append(o.ToJsonString()).Append('\n'); }

            Line(new JsonObject { ["type"] = "attachment", ["attachment"] = new JsonObject { ["type"] = "skill_listing", ["content"] = "- frontend-design: Build polished UIs\n- release-notes: Write release notes\n- pdf: Work with PDF files\n- deep-research: Research a topic" } });
            Line(new JsonObject { ["type"] = "user", ["message"] = new JsonObject { ["role"] = "user", ["content"] = sc.Title } });
            long ctx = 18_000;
            for (int i = 0; i < sc.Steps; i++)
            {
                t = t.AddSeconds(20 + rnd.Next(70));
                var (tool, detail) = sc.Tools[i % sc.Tools.Length];
                ctx += 2_000 + rnd.Next(9_000);
                var input = new JsonObject();
                if (tool is "Read" or "Edit" or "Write") input["file_path"] = cwd + "/" + detail;
                else if (tool == "Skill") input["skill"] = detail;
                else input["description"] = detail;
                var content = new JsonArray();
                if (i % 5 == 0) content.Add(new JsonObject { ["type"] = "text", ["text"] = i == 0 ? "I'll start by looking at how this works today." : "That worked. Moving on to the next part." });
                content.Add(new JsonObject { ["type"] = "tool_use", ["id"] = $"t{i}", ["name"] = tool, ["input"] = input });
                Line(new JsonObject
                {
                    ["type"] = "assistant",
                    ["message"] = new JsonObject
                    {
                        ["model"] = sc.Model, ["id"] = $"msg_{n}_{i}", ["content"] = content,
                        ["usage"] = new JsonObject { ["input_tokens"] = 6 + rnd.Next(40), ["output_tokens"] = 150 + rnd.Next(1400), ["cache_read_input_tokens"] = ctx - 3_000, ["cache_creation_input_tokens"] = 3_000 }
                    }
                });
                t = t.AddSeconds(3 + rnd.Next(20));
                var error = tool == "Bash" && i % 7 == 3;
                Line(new JsonObject { ["type"] = "user", ["message"] = new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = $"t{i}", ["is_error"] = error, ["content"] = error ? "2 tests failed" : "ok" }) } });
            }
            File.WriteAllText(Path.Combine(projDir, id + ".jsonl"), sb.ToString());
            Directory.CreateDirectory(Path.Combine(projDir, id));
            File.WriteAllText(Path.Combine(projDir, id, "custom-title.json"), new JsonObject { ["customTitle"] = sc.Title }.ToJsonString());

            if (sc.Live)
            {
                // a live-session file pointing at this process, so it shows as running
                File.WriteAllText(Path.Combine(claude, "sessions", $"{Environment.ProcessId + n}.json"), new JsonObject
                {
                    ["pid"] = Environment.ProcessId, ["sessionId"] = id, ["cwd"] = cwd, ["version"] = "2.1.330", ["kind"] = "interactive",
                    ["entrypoint"] = "cli", ["name"] = sc.Title, ["status"] = sc.Status
                }.ToJsonString());
                File.WriteAllText(Path.Combine(overseer, "statusline", id + ".json"), new JsonObject
                {
                    ["session_id"] = id, ["model"] = new JsonObject { ["id"] = sc.Model, ["display_name"] = Text.PrettyModel(sc.Model) },
                    ["context_window"] = new JsonObject { ["used_percentage"] = Math.Round(100.0 * ctx / 1_000_000, 1), ["context_window_size"] = 1_000_000 },
                    ["rate_limits"] = new JsonObject
                    {
                        ["five_hour"] = new JsonObject { ["used_percentage"] = 46, ["resets_at"] = DateTimeOffset.UtcNow.AddMinutes(134).ToUnixTimeSeconds() },
                        ["seven_day"] = new JsonObject { ["used_percentage"] = 23, ["resets_at"] = DateTimeOffset.UtcNow.AddDays(3).ToUnixTimeSeconds() },
                    },
                    ["cost"] = new JsonObject { ["total_cost_usd"] = 3.12 },
                }.ToJsonString());
            }
        }

        foreach (var (name, desc) in new[] { ("frontend-design", "Build polished, accessible UIs"), ("release-notes", "Write release notes from commits"), ("pdf", "Read and create PDF files") })
        {
            var d = Path.Combine(claude, "skills", name);
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "SKILL.md"), $"---\nname: {name}\ndescription: {desc}\n---\n\n# {name}\n");
        }
        Directory.CreateDirectory(Path.Combine(claude, "agents"));
        File.WriteAllText(Path.Combine(claude, "agents", "reviewer.md"), "---\nname: reviewer\ndescription: Reviews diffs for bugs before you commit\n---\n");
        File.WriteAllText(Path.Combine(claude, "settings.json"), new JsonObject
        {
            ["model"] = "opus",
            ["modelSettings"] = new JsonObject { ["claude-opus-5-5"] = new JsonObject { ["effortLevel"] = "high" } },
            ["permissions"] = new JsonObject { ["allow"] = new JsonArray("Bash(npm test:*)", "Read(./src/**)"), ["deny"] = new JsonArray("Read(./.env)") },
        }.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(claude, "CLAUDE.md"), "# My preferences\n\n- Run the tests before saying something works.\n- Keep pull requests small.\n");
        return (claude, overseer);
    }
}
