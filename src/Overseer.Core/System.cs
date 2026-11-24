using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace Overseer.Core;

/// <summary>Runs the official `claude` CLI. Arguments are passed as a list, so nothing is shell-interpreted.</summary>
public static class ClaudeCli
{
    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    public static bool IsMac => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    public static string? Find()
    {
        var names = IsWindows ? new[] { "claude.exe", "claude.cmd" } : new[] { "claude" };
        var dirs = new List<string> { Path.Combine(Paths.Home, ".local", "bin"), Path.Combine(Paths.Home, ".claude", "local") };
        if (!IsWindows) dirs.AddRange(new[] { "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin" });
        dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
        foreach (var d in dirs)
            foreach (var n in names)
            {
                try { var p = Path.Combine(d.Trim(), n); if (File.Exists(p)) return p; } catch { }
            }
        return null;
    }

    public static async Task<ActionResult> Run(IEnumerable<string> args, int timeoutMs = 120_000)
    {
        var exe = Find();
        if (exe == null) return ActionResult.Bad("Couldn't find the claude command-line tool. Install Claude Code first.");
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = Paths.Home
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var so = p.StandardOutput.ReadToEndAsync();
            var se = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(timeoutMs);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } return ActionResult.Bad("claude " + string.Join(' ', args) + " timed out"); }
            var output = await so + await se;
            return new ActionResult(p.ExitCode == 0, p.ExitCode == 0 ? "Done" : FirstLine(output), output);
        }
        catch (Exception ex) { return ActionResult.Bad(ex.Message); }
    }

    static string FirstLine(string s)
    {
        var l = s.Split('\n').Select(x => x.Trim()).FirstOrDefault(x => x.Length > 0) ?? "failed";
        return l.Length > 220 ? l[..220] : l;
    }

    // ---------- plugins ----------
    public static async Task<List<PluginRow>> ListPlugins()
    {
        var r = await Run(new[] { "plugin", "list", "--available", "--json" }, 90_000);
        var rows = new List<PluginRow>();
        if (!r.Ok || r.Output == null) return rows;
        var start = r.Output.IndexOfAny(new[] { '{', '[' });
        if (start < 0) return rows;
        var o = JsonNode.Parse(r.Output[start..]);
        var avail = o?["available"] as JsonArray ?? new JsonArray();
        foreach (var i in o?["installed"] as JsonArray ?? new JsonArray())
        {
            var id = i?["id"]?.GetValue<string>() ?? "";
            var cat = avail.FirstOrDefault(a => a?["pluginId"]?.GetValue<string>() == id);
            rows.Add(new PluginRow
            {
                Id = id, Name = id.Split('@')[0], Scope = i?["scope"]?.GetValue<string>(), Version = i?["version"]?.GetValue<string>(),
                Installed = true, Enabled = i?["enabled"]?.GetValue<bool>() ?? false,
                Marketplace = id.Contains('@') ? id.Split('@')[1] : null, Description = cat?["description"]?.GetValue<string>()
            });
        }
        foreach (var a in avail)
        {
            var id = a?["pluginId"]?.GetValue<string>();
            if (id == null || rows.Any(x => x.Id == id)) continue;
            rows.Add(new PluginRow { Id = id, Name = a?["name"]?.GetValue<string>() ?? id, Description = a?["description"]?.GetValue<string>(), Marketplace = a?["marketplaceName"]?.GetValue<string>() });
        }
        return rows;
    }

    public static Task<ActionResult> Plugin(string verb, string id) => Run(new[] { "plugin", verb, id }, 180_000);

    // ---------- MCP ----------
    public static Task<ActionResult> McpList() => Run(new[] { "mcp", "list" }, 90_000);
    public static Task<ActionResult> McpRemove(string name) => Run(new[] { "mcp", "remove", name }, 60_000);

    /// <summary>`claude mcp add` for stdio (commandOrUrl + args) or http/sse (url).</summary>
    public static Task<ActionResult> McpAdd(string name, string transport, string scope, string commandOrUrl, IEnumerable<string> extraArgs)
    {
        var args = new List<string> { "mcp", "add", "--scope", scope };
        if (transport != "stdio") { args.Add("--transport"); args.Add(transport); }
        args.Add(name);
        if (transport == "stdio") args.Add("--");
        args.Add(commandOrUrl);
        args.AddRange(extraArgs);
        return Run(args, 60_000);
    }

    // ---------- sessions ----------
    public static List<string> SessionArgs(string? model, string? effort, string? permissionMode, string? name)
    {
        var a = new List<string>();
        if (!string.IsNullOrEmpty(model)) { a.Add("--model"); a.Add(model); }
        if (!string.IsNullOrEmpty(effort)) { a.Add("--effort"); a.Add(effort); }
        if (!string.IsNullOrEmpty(permissionMode)) { a.Add("--permission-mode"); a.Add(permissionMode); }
        if (!string.IsNullOrWhiteSpace(name)) { a.Add("-n"); a.Add(name.Trim()); }
        return a;
    }

    public static ActionResult OpenInDesktop(string sessionId, string? cwd)
    {
        var exe = Find();
        if (exe == null) return ActionResult.Bad("Couldn't find the claude command-line tool.");
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Directory.Exists(cwd) ? cwd : Paths.Home };
            psi.ArgumentList.Add("--desktop"); psi.ArgumentList.Add("--resume"); psi.ArgumentList.Add(sessionId);
            Process.Start(psi);
            return ActionResult.Good("Asked the Claude desktop app to open this session");
        }
        catch (Exception ex) { return ActionResult.Bad(ex.Message); }
    }
}

/// <summary>Opens a new terminal window running claude, on Windows, macOS and Linux.</summary>
public static class Terminal
{
    public static ActionResult LaunchClaude(string? cwd, IReadOnlyList<string> claudeArgs)
    {
        var exe = ClaudeCli.Find();
        if (exe == null) return ActionResult.Bad("Couldn't find the claude command-line tool.");
        if (string.IsNullOrEmpty(cwd) || !Directory.Exists(cwd)) cwd = Paths.Home;
        var shown = "claude " + string.Join(' ', claudeArgs);
        try
        {
            if (ClaudeCli.IsWindows) return Windows(cwd, exe, claudeArgs, shown);
            var cmd = "cd " + Sh(cwd) + " && " + Sh(exe) + " " + string.Join(' ', claudeArgs.Select(Sh));
            if (ClaudeCli.IsMac)
            {
                var psi = new ProcessStartInfo("osascript") { UseShellExecute = false };
                foreach (var a in new[] { "-e", "on run argv", "-e", "tell application \"Terminal\" to do script (item 1 of argv)", "-e", "tell application \"Terminal\" to activate", "-e", "end run", cmd })
                    psi.ArgumentList.Add(a);
                Process.Start(psi);
                return ActionResult.Good("Opened Terminal: " + shown);
            }
            foreach (var (term, pre) in new[] { ("x-terminal-emulator", new[] { "-e" }), ("gnome-terminal", new[] { "--" }), ("konsole", new[] { "-e" }), ("xfce4-terminal", new[] { "-x" }), ("kitty", Array.Empty<string>()), ("alacritty", new[] { "-e" }), ("xterm", new[] { "-e" }) })
            {
                if (!OnPath(term)) continue;
                var psi = new ProcessStartInfo(term) { UseShellExecute = false, WorkingDirectory = cwd };
                foreach (var a in pre) psi.ArgumentList.Add(a);
                psi.ArgumentList.Add("bash"); psi.ArgumentList.Add("-lc"); psi.ArgumentList.Add(cmd + "; exec bash");
                Process.Start(psi);
                return ActionResult.Good($"Opened {term}: {shown}");
            }
            return ActionResult.Bad("No terminal emulator found. Run this yourself: " + cmd);
        }
        catch (Exception ex) { return ActionResult.Bad("Couldn't open a terminal: " + ex.Message); }
    }

    static ActionResult Windows(string cwd, string exe, IReadOnlyList<string> args, string shown)
    {
        var wt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
        if (File.Exists(wt))
        {
            var psi = new ProcessStartInfo(wt) { UseShellExecute = false };
            psi.ArgumentList.Add("-d"); psi.ArgumentList.Add(cwd);
            psi.ArgumentList.Add(exe);
            // wt treats ';' as a command separator
            foreach (var a in args) psi.ArgumentList.Add(a.Replace(";", "\\;"));
            Process.Start(psi);
            return ActionResult.Good("Opened Windows Terminal: " + shown);
        }
        var cmdPsi = new ProcessStartInfo("cmd.exe") { UseShellExecute = true, WorkingDirectory = cwd };
        cmdPsi.ArgumentList.Add("/k"); cmdPsi.ArgumentList.Add(exe);
        foreach (var a in args) cmdPsi.ArgumentList.Add(a);
        Process.Start(cmdPsi);
        return ActionResult.Good("Opened a command prompt: " + shown);
    }

    /// <summary>POSIX single-quote escaping.</summary>
    public static string Sh(string s) => "'" + s.Replace("'", "'\\''") + "'";

    static bool OnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Any(d => { try { return File.Exists(Path.Combine(d, exe)); } catch { return false; } });
}

public static class ProcessControl
{
    [DllImport("libc", SetLastError = true)] static extern int kill(int pid, int sig);

    /// <summary>
    /// Stops a session process: asks politely first (SIGINT then SIGTERM on macOS/Linux; close request on Windows),
    /// and only force-kills if it is still running after the grace period.
    /// </summary>
    public static async Task<ActionResult> StopGracefully(int pid, TimeSpan grace)
    {
        Process p;
        try { p = Process.GetProcessById(pid); } catch { return ActionResult.Good("That session isn't running any more."); }
        try
        {
            if (!ClaudeCli.IsWindows)
            {
                kill(pid, 2); // SIGINT, like pressing Ctrl+C
                if (await Exited(p, grace / 2)) return ActionResult.Good("Session stopped (interrupted).");
                kill(pid, 15); // SIGTERM
                if (await Exited(p, grace / 2)) return ActionResult.Good("Session stopped.");
            }
            else
            {
                var tk = new ProcessStartInfo("taskkill") { UseShellExecute = false, CreateNoWindow = true };
                tk.ArgumentList.Add("/PID"); tk.ArgumentList.Add(pid.ToString());
                Process.Start(tk)?.WaitForExit(3000);
                if (await Exited(p, grace)) return ActionResult.Good("Session stopped.");
            }
            p.Kill(entireProcessTree: true);
            return ActionResult.Good("The session didn't stop on request, so it was force-closed.");
        }
        catch (Exception ex) { return ActionResult.Bad("Couldn't stop it: " + ex.Message); }
    }

    static async Task<bool> Exited(Process p, TimeSpan wait)
    {
        using var cts = new CancellationTokenSource(wait);
        try { await p.WaitForExitAsync(cts.Token); return true; } catch { return p.HasExited; }
    }

    public static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
    }

    public static void Reveal(string file)
    {
        try
        {
            if (ClaudeCli.IsWindows) { var psi = new ProcessStartInfo("explorer.exe"); psi.ArgumentList.Add("/select," + file); Process.Start(psi); }
            else if (ClaudeCli.IsMac) { var psi = new ProcessStartInfo("open"); psi.ArgumentList.Add("-R"); psi.ArgumentList.Add(file); Process.Start(psi); }
            else Open(Path.GetDirectoryName(file)!);
        }
        catch { }
    }
}

/// <summary>Native desktop notifications without extra dependencies. Text is passed as data, never as script.</summary>
public static class Notifier
{
    public static void Show(string title, string message)
    {
        try
        {
            if (ClaudeCli.IsWindows)
            {
                // Windows toast through PowerShell; title/message travel as environment variables, not as script text
                const string script =
                    "[Windows.UI.Notifications.ToastNotificationManager,Windows.UI.Notifications,ContentType=WindowsRuntime]>$null;" +
                    "$t=[Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02);" +
                    "$x=$t.GetElementsByTagName('text');$x.Item(0).AppendChild($t.CreateTextNode($env:OVS_TITLE))>$null;$x.Item(1).AppendChild($t.CreateTextNode($env:OVS_MSG))>$null;" +
                    "$n=[Windows.UI.Notifications.ToastNotification]::new($t);" +
                    "[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\\WindowsPowerShell\\v1.0\\powershell.exe').Show($n)";
                var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
                psi.ArgumentList.Add("-NoProfile"); psi.ArgumentList.Add("-NonInteractive"); psi.ArgumentList.Add("-Command"); psi.ArgumentList.Add(script);
                psi.Environment["OVS_TITLE"] = title;
                psi.Environment["OVS_MSG"] = message;
                Process.Start(psi);
            }
            else if (ClaudeCli.IsMac)
            {
                var psi = new ProcessStartInfo("osascript") { UseShellExecute = false };
                foreach (var a in new[] { "-e", "on run argv", "-e", "display notification (item 2 of argv) with title (item 1 of argv)", "-e", "end run", title, message })
                    psi.ArgumentList.Add(a);
                Process.Start(psi);
            }
            else
            {
                var psi = new ProcessStartInfo("notify-send") { UseShellExecute = false };
                psi.ArgumentList.Add("--app-name=Overseer"); psi.ArgumentList.Add(title); psi.ArgumentList.Add(message);
                Process.Start(psi);
            }
        }
        catch { /* notifications are best-effort */ }
    }
}
