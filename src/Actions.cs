// Claude Monitor - control layer.
// Everything that changes something lives here:
//   * settings.json edits (re-read before every write, other keys preserved, one-time backup)
//   * skill load/unload (move folders between ~/.claude/skills and ~/.claude/skills-disabled)
//   * `claude` CLI calls (plugins, MCP) and launching sessions in a terminal
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using IOPath = System.IO.Path;

namespace ClaudeMonitor
{
    public class ActionResult
    {
        public bool Ok;
        public string Message;
        public string Output;
        public static ActionResult Good(string m) { return new ActionResult { Ok = true, Message = m }; }
        public static ActionResult Bad(string m) { return new ActionResult { Ok = false, Message = m }; }
    }


    public class PluginRow
    {
        public string Id, Name, Description, Scope, Version, Marketplace;
        public bool Installed, Enabled;
    }

    public static class Actions
    {
        static readonly JavaScriptSerializer Ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 512 };
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        public static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        public static readonly string ClaudeDir = Store.ConfigDir();
        public static readonly string SettingsPath = IOPath.Combine(ClaudeDir, "settings.json");
        public static readonly string SkillsDir = IOPath.Combine(ClaudeDir, "skills");
        static bool backedUp;

        // ================= settings.json =================
        public static Dictionary<string, object> ReadSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return new Dictionary<string, object>();
                var o = Ser.DeserializeObject(File.ReadAllText(SettingsPath, Encoding.UTF8)) as Dictionary<string, object>;
                return o ?? new Dictionary<string, object>();
            }
            catch { return null; } // unreadable / invalid JSON: never overwrite it
        }

        public static object GetSetting(params string[] path)
        {
            object cur = ReadSettings();
            foreach (var k in path)
            {
                var d = cur as Dictionary<string, object>;
                object v;
                if (d == null || !d.TryGetValue(k, out v)) return null;
                cur = v;
            }
            return cur;
        }

        /// <summary>Set (or with value == null, remove) a nested key, keeping everything else in the file.</summary>
        public static ActionResult SetSetting(object value, params string[] path)
        {
            try
            {
                var root = ReadSettings();
                if (root == null) return ActionResult.Bad("settings.json isn't valid JSON, so I won't touch it. Fix it by hand first.");
                if (!backedUp && File.Exists(SettingsPath))
                {
                    File.Copy(SettingsPath, SettingsPath + ".monitor-backup", true);
                    backedUp = true;
                }
                var cur = root;
                for (int i = 0; i < path.Length - 1; i++)
                {
                    object next;
                    if (!cur.TryGetValue(path[i], out next) || !(next is Dictionary<string, object>))
                    {
                        if (value == null) return ActionResult.Good("Already unset");
                        next = new Dictionary<string, object>();
                        cur[path[i]] = next;
                    }
                    cur = (Dictionary<string, object>)next;
                }
                var leaf = path[path.Length - 1];
                if (value == null) cur.Remove(leaf); else cur[leaf] = value;
                // drop parents we emptied
                if (value == null && path.Length > 1) PruneEmpty(root, path, 0);
                Directory.CreateDirectory(ClaudeDir);
                var tmp = SettingsPath + ".tmp";
                File.WriteAllText(tmp, Json(root, 0) + Environment.NewLine, new UTF8Encoding(false));
                if (File.Exists(SettingsPath)) File.Replace(tmp, SettingsPath, null); else File.Move(tmp, SettingsPath);
                return ActionResult.Good("Saved " + string.Join(".", path));
            }
            catch (Exception ex) { return ActionResult.Bad("Couldn't save settings: " + ex.Message); }
        }

        static bool PruneEmpty(Dictionary<string, object> d, string[] path, int i)
        {
            if (i >= path.Length - 1) return d.Count == 0;
            object child;
            if (d.TryGetValue(path[i], out child) && child is Dictionary<string, object>)
                if (PruneEmpty((Dictionary<string, object>)child, path, i + 1)) d.Remove(path[i]);
            return d.Count == 0;
        }

        // pretty JSON writer (JavaScriptSerializer can't indent)
        public static string Json(object o, int ind)
        {
            var pad = new string(' ', (ind + 1) * 2);
            var end = new string(' ', ind * 2);
            if (o == null) return "null";
            if (o is string) return Ser.Serialize(o);
            if (o is bool) return (bool)o ? "true" : "false";
            if (o is int || o is long || o is decimal || o is double || o is float) return Convert.ToString(o, Inv);
            var d = o as IDictionary<string, object>;
            if (d != null)
            {
                if (d.Count == 0) return "{}";
                return "{\n" + string.Join(",\n", d.Select(kv => pad + Ser.Serialize(kv.Key) + ": " + Json(kv.Value, ind + 1))) + "\n" + end + "}";
            }
            var e = o as IEnumerable;
            if (e != null)
            {
                var items = e.Cast<object>().ToList();
                if (items.Count == 0) return "[]";
                return "[\n" + string.Join(",\n", items.Select(x => pad + Json(x, ind + 1))) + "\n" + end + "]";
            }
            return Ser.Serialize(o);
        }

        // ================= skills =================
        // Uses the documented settings key  skillOverrides: { "<name>": "on" | "name-only" | "user-invocable-only" | "off" }
        public static string SkillState(string name)
        {
            var d = GetSetting("skillOverrides") as Dictionary<string, object>;
            object v;
            return d != null && d.TryGetValue(name, out v) && v is string ? (string)v : "on";
        }

        public static ActionResult SetSkillState(string name, string state)
        {
            var r = SetSetting(state == "on" ? null : state, "skillOverrides", name);
            if (r.Ok) r.Message = name + " → " + state + " (applies to new sessions)";
            return r;
        }

        public static ActionResult InstallSkillFromFolder(string src)
        {
            try
            {
                if (!File.Exists(IOPath.Combine(src, "SKILL.md"))) return ActionResult.Bad("That folder has no SKILL.md, so it isn't a skill.");
                var name = IOPath.GetFileName(src.TrimEnd('\\', '/'));
                var target = IOPath.Combine(SkillsDir, name);
                if (Directory.Exists(target)) return ActionResult.Bad("A skill named '" + name + "' is already installed.");
                CopyDir(src, target);
                return ActionResult.Good("Installed skill " + name + " (available in new sessions)");
            }
            catch (Exception ex) { return ActionResult.Bad("Couldn't copy the skill: " + ex.Message); }
        }

        public static ActionResult CreateSkill(string name)
        {
            name = new string((name ?? "").Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
            if (name.Length == 0) return ActionResult.Bad("Type a name for the skill first.");
            var dir = IOPath.Combine(SkillsDir, name);
            if (Directory.Exists(dir)) return ActionResult.Bad("A skill named '" + name + "' already exists.");
            Directory.CreateDirectory(dir);
            var md = IOPath.Combine(dir, "SKILL.md");
            File.WriteAllText(md, "---\nname: " + name + "\ndescription: Describe when Claude should use this skill.\n---\n\n# " + name + "\n\nWrite the instructions Claude should follow here.\n", new UTF8Encoding(false));
            OpenPath(md);
            return ActionResult.Good("Created skill " + name + " and opened its SKILL.md");
        }

        static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src)) File.Copy(f, IOPath.Combine(dst, IOPath.GetFileName(f)));
            foreach (var d in Directory.GetDirectories(src)) CopyDir(d, IOPath.Combine(dst, IOPath.GetFileName(d)));
        }

        // ================= claude CLI =================
        public static string ClaudeExe()
        {
            var local = IOPath.Combine(Home, ".local", "bin", "claude.exe");
            if (File.Exists(local)) return local;
            foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try
                {
                    var c = IOPath.Combine(p.Trim(), "claude.exe");
                    if (File.Exists(c)) return c;
                }
                catch { }
            }
            return null;
        }

        public static Task<ActionResult> RunClaude(string args, int timeoutMs)
        {
            return Task.Factory.StartNew(() =>
            {
                var exe = ClaudeExe();
                if (exe == null) return ActionResult.Bad("Couldn't find the claude command-line tool (claude.exe).");
                try
                {
                    var psi = new ProcessStartInfo(exe, args)
                    {
                        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = Home
                    };
                    using (var p = Process.Start(psi))
                    {
                        var so = p.StandardOutput.ReadToEndAsync();
                        var se = p.StandardError.ReadToEndAsync();
                        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return ActionResult.Bad("claude " + args + " timed out"); }
                        var outp = so.Result + se.Result;
                        return new ActionResult { Ok = p.ExitCode == 0, Output = outp, Message = p.ExitCode == 0 ? "Done" : FirstLine(outp) };
                    }
                }
                catch (Exception ex) { return ActionResult.Bad(ex.Message); }
            });
        }

        static string FirstLine(string s)
        {
            var l = (s ?? "").Split('\n').Select(x => x.Trim()).FirstOrDefault(x => x.Length > 0) ?? "failed";
            return l.Length > 200 ? l.Substring(0, 200) : l;
        }

        public static async Task<List<PluginRow>> ListPlugins()
        {
            var r = await RunClaude("plugin list --available --json", 60000);
            var rows = new List<PluginRow>();
            if (!r.Ok) return rows;
            var o = Ser.DeserializeObject(JsonPart(r.Output));
            var installed = Store.G(o, "installed") as object[] ?? new object[0];
            foreach (var i in installed)
            {
                var id = Store.S(i, "id") ?? "";
                var en = Store.G(i, "enabled");
                rows.Add(new PluginRow
                {
                    Id = id, Name = id.Split('@')[0], Scope = Store.S(i, "scope"), Version = Store.S(i, "version"),
                    Installed = true, Enabled = en is bool && (bool)en, Marketplace = id.Contains("@") ? id.Split('@')[1] : null
                });
            }
            var avail = Store.G(o, "available") as object[] ?? new object[0];
            foreach (var a in avail)
            {
                var id = Store.S(a, "pluginId");
                if (id == null || rows.Any(x => x.Id == id)) continue;
                rows.Add(new PluginRow { Id = id, Name = Store.S(a, "name") ?? id, Description = Store.S(a, "description"), Marketplace = Store.S(a, "marketplaceName") });
            }
            // fill descriptions of installed ones from the catalog
            foreach (var row in rows.Where(x => x.Installed && x.Description == null))
            {
                var m = avail.FirstOrDefault(a => Store.S(a, "pluginId") == row.Id);
                if (m != null) row.Description = Store.S(m, "description");
            }
            return rows;
        }

        static string JsonPart(string s)
        {
            int i = s.IndexOfAny(new[] { '{', '[' });
            return i >= 0 ? s.Substring(i) : "{}";
        }

        public static Task<ActionResult> Plugin(string verb, string id)
        {
            return RunClaude("plugin " + verb + " " + Q(id), 180000);
        }

        public static Task<ActionResult> McpList() { return RunClaude("mcp list", 90000); }
        public static Task<ActionResult> McpRemove(string name) { return RunClaude("mcp remove " + Q(name), 60000); }

        public static string Q(string s)
        {
            if (string.IsNullOrEmpty(s)) return "\"\"";
            if (s.IndexOfAny(new[] { ' ', '\t', '"', ';', '&', '|' }) < 0) return s;
            return "\"" + s.Replace("\"", "\\\"") + "\"";
        }

        /// <summary>Open a new terminal tab in <paramref name="cwd"/> running claude with the given arguments.</summary>
        public static ActionResult LaunchInTerminal(string cwd, string claudeArgs)
        {
            try
            {
                var exe = ClaudeExe();
                if (exe == null) return ActionResult.Bad("Couldn't find claude.exe.");
                if (string.IsNullOrEmpty(cwd) || !Directory.Exists(cwd)) cwd = Home;
                var wt = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
                // ';' is a separator for wt, so escape it
                var safeArgs = claudeArgs.Replace(";", "\\;");
                if (File.Exists(wt))
                    Process.Start(new ProcessStartInfo(wt, "-d " + Q(cwd) + " " + Q(exe) + " " + safeArgs) { UseShellExecute = false });
                else
                    Process.Start(new ProcessStartInfo("cmd.exe", "/k \"" + Q(exe) + " " + claudeArgs + "\"") { WorkingDirectory = cwd, UseShellExecute = true });
                return ActionResult.Good("Opened a terminal: claude " + claudeArgs);
            }
            catch (Exception ex) { return ActionResult.Bad("Couldn't open a terminal: " + ex.Message); }
        }

        /// <summary>Hand a session to the Claude desktop app.</summary>
        public static ActionResult OpenInDesktop(string sessionId, string cwd)
        {
            try
            {
                var exe = ClaudeExe();
                if (exe == null) return ActionResult.Bad("Couldn't find claude.exe.");
                Process.Start(new ProcessStartInfo(exe, "--desktop --resume " + Q(sessionId)) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Directory.Exists(cwd ?? "") ? cwd : Home });
                return ActionResult.Good("Asked the desktop app to open this session");
            }
            catch (Exception ex) { return ActionResult.Bad(ex.Message); }
        }

        public static ActionResult StopProcess(int pid)
        {
            try { Process.GetProcessById(pid).Kill(); return ActionResult.Good("Stopped process " + pid); }
            catch (Exception ex) { return ActionResult.Bad("Couldn't stop it: " + ex.Message); }
        }

        public static void OpenPath(string path)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
        }

        public static void RevealFile(string path)
        {
            try { Process.Start("explorer.exe", "/select,\"" + path + "\""); } catch { }
        }
    }
}
