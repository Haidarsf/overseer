// Claude Monitor - Control tab, session actions, toasts and input widgets.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Shapes = System.Windows.Shapes;

namespace ClaudeMonitor
{
    public partial class MainWindow
    {
        // persistent inputs (kept across re-renders so typing isn't lost)
        readonly TextBox customModelBox = NewInput("custom model id, e.g. claude-opus-5-5");
        readonly TextBox launchFolderBox = NewInput("folder to start in");
        readonly TextBox launchNameBox = NewInput("session name (optional)");
        readonly TextBox newSkillBox = NewInput("new-skill-name");
        readonly TextBox pluginSearchBox = NewInput("search the marketplace…");
        readonly StackPanel marketList = new StackPanel();
        readonly StackPanel toastHost = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 24, 20), Width = 380 };

        string launchModel, launchEffort, launchMode;
        List<PluginRow> plugins;          // null = not loaded
        bool pluginsLoading, mcpLoading;
        string mcpOutput;

        static readonly string[] ModelVals = { null, "fable", "opus", "sonnet", "haiku", "opusplan" };
        static readonly string[] ModelLbls = { "Default", "Fable", "Opus", "Sonnet", "Haiku", "Opus Plan" };
        static readonly string[] EffortVals = { null, "low", "medium", "high", "xhigh" };
        static readonly string[] EffortLbls = { "Auto", "Low", "Medium", "High", "X-High" };
        static readonly string[] ModeVals = { null, "acceptEdits", "plan", "auto", "dontAsk" };
        static readonly string[] ModeLbls = { "Ask first", "Accept edits", "Plan", "Auto", "Don't ask" };

        void InitControls()
        {
            pluginSearchBox.TextChanged += delegate { FillMarket(); };
            launchFolderBox.Text = Actions.Home;
        }

        // ================= Control tab =================
        UIElement RenderControl(List<Sess> top)
        {
            var settings = Actions.ReadSettings() ?? new Dictionary<string, object>();
            var p = new StackPanel { Margin = new Thickness(30, 24, 30, 30) };
            var h = Txt("Control panel", 26, Fg, FontWeights.SemiBold); h.FontFamily = Display;
            p.Children.Add(h);
            var sub = Txt("Changes are written to your Claude Code settings. Model and effort apply to sessions you start next; permission changes apply right away.", 12.5, Muted, FontWeights.Normal);
            sub.TextWrapping = TextWrapping.Wrap; sub.Margin = new Thickness(0, 6, 0, 14);
            p.Children.Add(sub);

            var cols = new Grid { Margin = new Thickness(-7, 0, -7, 0) };
            cols.ColumnDefinitions.Add(new ColumnDefinition());
            cols.ColumnDefinitions.Add(new ColumnDefinition());
            var left = new StackPanel();
            var right = new StackPanel();
            Grid.SetColumn(right, 1);
            cols.Children.Add(left);
            cols.Children.Add(right);

            left.Children.Add(Card("Defaults for new sessions", IcSettings, Accent, DefaultsPanel(settings, top), "Saved to " + Actions.SettingsPath + ". A backup is made next to it (settings.json.monitor-backup) before the first change."));
            left.Children.Add(Card("Start a new session", "", Green, LaunchPanel(top), "Opens a terminal running Claude Code with the options you pick. They apply to that session only."));
            right.Children.Add(Card("Skills", IcBolt, Yellow, SkillsPanel(), "Uses Claude Code's skillOverrides setting. On = Claude can use it, Name only = listed by name, Manual = only when you type /name, Off = hidden. Skills that come from plugins are controlled by the plugin."));
            right.Children.Add(Card("Plugins", IcPuzzle, Purple, PluginsPanel(), "Runs the official 'claude plugin' commands. Changes apply to new sessions."));
            right.Children.Add(Card("MCP servers", IcPlug, Blue, McpPanel(), "Runs 'claude mcp list'. This health-checks each server, so it can take a moment."));
            p.Children.Add(cols);
            return p;
        }

        UIElement DefaultsPanel(Dictionary<string, object> settings, List<Sess> top)
        {
            var sp = new StackPanel();
            var curModel = Store.S(settings, "model");

            sp.Children.Add(Label("Model"));
            bool known = ModelVals.Contains(curModel);
            sp.Children.Add(Pills(ModelLbls, ModelVals, curModel, v => Apply(Actions.SetSetting(v, "model"))));
            var customRow = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
            var setBtn = Btn("Use", null, Accent, false, () =>
            {
                var t = customModelBox.Text.Trim();
                if (t.Length > 0) Apply(Actions.SetSetting(t, "model"));
            });
            setBtn.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(setBtn, Dock.Right);
            customRow.Children.Add(setBtn);
            if (!known && curModel != null && customModelBox.Text.Length == 0) customModelBox.Text = curModel;
            customRow.Children.Add(Detach(customModelBox));
            sp.Children.Add(customRow);
            if (!known && curModel != null) sp.Children.Add(Hint("Currently set to a custom model: " + curModel, Accent2));

            // effort, per model
            sp.Children.Add(Label("Effort"));
            var ms = Store.G(settings, "modelSettings") as Dictionary<string, object>;
            var ids = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            if (ms != null) foreach (var k in ms.Keys) ids.Add(k);
            foreach (var s in store.Sessions.Values) if (!string.IsNullOrEmpty(s.Model) && s.Model.StartsWith("claude-")) ids.Add(s.Model.Replace("[1m]", ""));
            foreach (var id in ids)
            {
                var modelId = id;
                var cur = Store.S(Store.G(ms, id), "effortLevel");
                sp.Children.Add(SubLabel(PrettyModel(id) + "  ·  " + id));
                sp.Children.Add(Pills(EffortLbls, EffortVals, cur, v => Apply(Actions.SetSetting(v, "modelSettings", modelId, "effortLevel"))));
            }
            sp.Children.Add(SubLabel("All other models"));
            sp.Children.Add(Pills(EffortLbls, EffortVals, Store.S(settings, "effortLevel"), v => Apply(Actions.SetSetting(v, "effortLevel"))));

            sp.Children.Add(Label("Permission mode"));
            var mode = Store.S(Store.G(settings, "permissions"), "defaultMode");
            if (mode == "default" || mode == "manual") mode = null;
            sp.Children.Add(Pills(ModeLbls, ModeVals, mode, v => Apply(Actions.SetSetting(v, "permissions", "defaultMode"))));
            if (mode == "bypassPermissions") sp.Children.Add(Hint("Currently set to bypassPermissions (all checks skipped). Pick a mode above to turn it off.", Red));
            sp.Children.Add(Hint("Bypass-all-permissions isn't offered here on purpose.", Faint));

            sp.Children.Add(Label("Output style"));
            sp.Children.Add(Pills(new[] { "Default", "Explanatory", "Learning" }, new string[] { null, "Explanatory", "Learning" }, Store.S(settings, "outputStyle"), v => Apply(Actions.SetSetting(v, "outputStyle"))));

            sp.Children.Add(Label("Options"));
            var think = Store.G(settings, "alwaysThinkingEnabled");
            sp.Children.Add(ToggleRow("Extended thinking", "Let Claude think before answering (some models always think).", !(think is bool) || (bool)think,
                on => Apply(Actions.SetSetting(on ? null : (object)false, "alwaysThinkingEnabled"))));
            var tips = Store.G(settings, "spinnerTipsEnabled");
            sp.Children.Add(ToggleRow("Spinner tips", "Show tips while Claude is working in the terminal.", !(tips is bool) || (bool)tips,
                on => Apply(Actions.SetSetting(on ? null : (object)false, "spinnerTipsEnabled"))));

            var links = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
            links.Children.Add(Btn("Open settings.json", IcDoc, Fg2, false, () => Actions.OpenPath(Actions.SettingsPath)));
            links.Children.Add(Btn("Show backup", IcHistory, Fg2, false, () =>
            {
                var b = Actions.SettingsPath + ".monitor-backup";
                if (File.Exists(b)) Actions.RevealFile(b); else Toast("No backup yet. One is made before the first change.", true);
            }));
            sp.Children.Add(links);
            return sp;
        }

        UIElement LaunchPanel(List<Sess> top)
        {
            var sp = new StackPanel();
            sp.Children.Add(Label("Folder"));
            var row = new DockPanel();
            var browse = Btn("Browse…", "", Fg2, false, () =>
            {
                using (var dlg = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = launchFolderBox.Text, Description = "Folder for the new Claude session" })
                    if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) launchFolderBox.Text = dlg.SelectedPath;
            });
            browse.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(browse, Dock.Right);
            row.Children.Add(browse);
            row.Children.Add(Detach(launchFolderBox));
            sp.Children.Add(row);
            var recent = top.Where(s => s.Cwd != null).Select(s => s.Cwd).Distinct().Take(4).ToList();
            if (recent.Count > 0)
            {
                var wp = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
                foreach (var r in recent)
                {
                    var path = r;
                    var c = Chip("", ShortPath(r), Fg2);
                    c.Cursor = System.Windows.Input.Cursors.Hand; c.ToolTip = r;
                    c.MouseLeftButtonUp += delegate { launchFolderBox.Text = path; };
                    wp.Children.Add(c);
                }
                sp.Children.Add(wp);
            }
            sp.Children.Add(Label("Name"));
            sp.Children.Add(Detach(launchNameBox));
            sp.Children.Add(Label("Model"));
            sp.Children.Add(Pills(ModelLbls, ModelVals, launchModel, v => { launchModel = v; controlDirty = true; Render(true); }));
            sp.Children.Add(Label("Effort"));
            sp.Children.Add(Pills(new[] { "Default", "Low", "Medium", "High", "X-High", "Max" }, new string[] { null, "low", "medium", "high", "xhigh", "max" }, launchEffort, v => { launchEffort = v; controlDirty = true; Render(true); }));
            sp.Children.Add(Label("Permission mode"));
            sp.Children.Add(Pills(new[] { "Default", "Ask first", "Accept edits", "Plan", "Auto" }, new string[] { null, "manual", "acceptEdits", "plan", "auto" }, launchMode, v => { launchMode = v; controlDirty = true; Render(true); }));
            var go = Btn("Start in terminal", "", Accent, true, () =>
            {
                var args = new List<string>();
                if (launchModel != null) args.Add("--model " + launchModel);
                if (launchEffort != null) args.Add("--effort " + launchEffort);
                if (launchMode != null) args.Add("--permission-mode " + launchMode);
                if (launchNameBox.Text.Trim().Length > 0) args.Add("-n " + Actions.Q(launchNameBox.Text.Trim()));
                if (!Directory.Exists(launchFolderBox.Text)) { Toast("That folder doesn't exist.", false); return; }
                Toast(Actions.LaunchInTerminal(launchFolderBox.Text, string.Join(" ", args)));
            });
            go.Margin = new Thickness(0, 16, 0, 0); go.HorizontalAlignment = HorizontalAlignment.Left;
            sp.Children.Add(go);
            return sp;
        }

        UIElement SkillsPanel()
        {
            var sp = new StackPanel();
            if (store.InstalledSkills.Count == 0) sp.Children.Add(Txt("No skills found in ~/.claude/skills.", 12, Muted, FontWeights.Normal));
            foreach (var si in store.InstalledSkills)
            {
                var name = si.Name;
                var state = Actions.SkillState(name);
                var row = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
                var top = new DockPanel();
                var tag = Chip(null, si.Source == "personal" ? "yours" : "synced", si.Source == "personal" ? Green : Fg2);
                tag.Margin = new Thickness(8, 0, 0, 0); tag.Padding = new Thickness(7, 1, 8, 2);
                DockPanel.SetDock(tag, Dock.Right);
                top.Children.Add(tag);
                var n = Txt(name, 13, state == "off" ? Muted : Fg, FontWeights.SemiBold);
                top.Children.Add(n);
                row.Children.Add(top);
                if (!string.IsNullOrEmpty(si.Description))
                {
                    var d = Txt(si.Description, 11.5, Faint, FontWeights.Normal);
                    d.TextTrimming = TextTrimming.CharacterEllipsis; d.ToolTip = si.Description; d.Margin = new Thickness(0, 2, 0, 6);
                    row.Children.Add(d);
                }
                row.Children.Add(Pills(new[] { "On", "Name only", "Manual", "Off" }, new[] { "on", "name-only", "user-invocable-only", "off" }, state,
                    v => Apply(Actions.SetSkillState(name, v)), true));
                sp.Children.Add(row);
            }
            sp.Children.Add(new Border { Height = 1, Background = LineSoft, Margin = new Thickness(0, 4, 0, 12) });
            var create = new DockPanel();
            var cb = Btn("Create", IcEdit, Accent, false, () => { var r = Actions.CreateSkill(newSkillBox.Text); if (r.Ok) newSkillBox.Text = ""; AfterSkillChange(r); });
            cb.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(cb, Dock.Right);
            create.Children.Add(cb);
            create.Children.Add(Detach(newSkillBox));
            sp.Children.Add(create);
            var btns = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            btns.Children.Add(Btn("Install from folder…", "", Fg2, false, () =>
            {
                using (var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "Pick a skill folder (it must contain SKILL.md)" })
                    if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) AfterSkillChange(Actions.InstallSkillFromFolder(dlg.SelectedPath));
            }));
            btns.Children.Add(Btn("Open skills folder", "", Fg2, false, () => { Directory.CreateDirectory(Actions.SkillsDir); Actions.OpenPath(Actions.SkillsDir); }));
            sp.Children.Add(btns);
            return sp;
        }

        void AfterSkillChange(ActionResult r)
        {
            Toast(r);
            lock (gate) store.ForceStatic();
            Refresh();
            controlDirty = true;
            Dispatcher.BeginInvoke(new Action(() => Render(true)), DispatcherPriority.Background);
        }

        UIElement PluginsPanel()
        {
            var sp = new StackPanel();
            if (plugins == null)
            {
                if (!pluginsLoading) LoadPlugins();
                sp.Children.Add(Txt("Loading plugins…", 12, Muted, FontWeights.Normal));
                return sp;
            }
            var inst = plugins.Where(x => x.Installed).ToList();
            sp.Children.Add(Label("Installed"));
            if (inst.Count == 0) sp.Children.Add(Txt("No plugins installed.", 12, Muted, FontWeights.Normal));
            foreach (var pl in inst)
            {
                var plugin = pl;
                var row = new DockPanel { Margin = new Thickness(0, 4, 0, 8) };
                var un = Btn("Uninstall", "", Red, false, () =>
                {
                    if (Confirm("Uninstall the plugin " + plugin.Id + "?")) RunPluginOp("uninstall", plugin.Id, "Uninstalling " + plugin.Name + "…");
                });
                un.Margin = new Thickness(8, 0, 0, 0);
                DockPanel.SetDock(un, Dock.Right);
                if (plugin.Scope != "synced") row.Children.Add(un);
                var tg = Toggle(plugin.Enabled, on => RunPluginOp(on ? "enable" : "disable", plugin.Id, (on ? "Enabling " : "Disabling ") + plugin.Name + "…"));
                tg.Margin = new Thickness(8, 0, 0, 0);
                DockPanel.SetDock(tg, Dock.Right);
                row.Children.Add(tg);
                var t = new StackPanel();
                t.Children.Add(Txt(plugin.Name + (plugin.Version != null ? "   v" + plugin.Version : ""), 13, Fg, FontWeights.SemiBold));
                t.Children.Add(Txt((plugin.Scope ?? "") + (plugin.Scope == "synced" ? " · managed by your account" : "") + (plugin.Marketplace != null && plugin.Marketplace != plugin.Scope ? " · " + plugin.Marketplace : ""), 11.5, Faint, FontWeights.Normal));
                row.Children.Add(t);
                sp.Children.Add(row);
            }
            sp.Children.Add(Label("Marketplace  ·  " + plugins.Count(x => !x.Installed) + " available"));
            sp.Children.Add(Detach(pluginSearchBox));
            marketList.Margin = new Thickness(0, 8, 0, 0);
            sp.Children.Add(Detach(marketList));
            FillMarket();
            var rf = Btn("Reload list", IcHistory, Fg2, false, () => { plugins = null; controlDirty = true; Render(true); });
            rf.HorizontalAlignment = HorizontalAlignment.Left; rf.Margin = new Thickness(0, 10, 0, 0);
            sp.Children.Add(rf);
            return sp;
        }

        void FillMarket()
        {
            marketList.Children.Clear();
            if (plugins == null) return;
            var q = pluginSearchBox.Text.Trim().ToLowerInvariant();
            var hits = plugins.Where(x => !x.Installed && (q.Length == 0 || (x.Name + " " + x.Description).ToLowerInvariant().Contains(q))).ToList();
            foreach (var pl in hits.Take(q.Length == 0 ? 8 : 25))
            {
                var plugin = pl;
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
                var ib = Btn("Install", "", Purple, false, () =>
                {
                    if (Confirm("Install " + plugin.Id + "?\n\nPlugins can run code on your computer. Only install ones you trust.")) RunPluginOp("install", plugin.Id, "Installing " + plugin.Name + "…");
                });
                ib.Margin = new Thickness(8, 0, 0, 0); ib.VerticalAlignment = VerticalAlignment.Top;
                DockPanel.SetDock(ib, Dock.Right);
                row.Children.Add(ib);
                var t = new StackPanel();
                t.Children.Add(Txt(plugin.Name, 13, Fg, FontWeights.SemiBold));
                if (!string.IsNullOrEmpty(plugin.Description))
                {
                    var d = Txt(plugin.Description, 11.5, Muted, FontWeights.Normal);
                    d.TextWrapping = TextWrapping.Wrap; d.MaxHeight = 32; d.TextTrimming = TextTrimming.CharacterEllipsis; d.ToolTip = plugin.Description;
                    t.Children.Add(d);
                }
                row.Children.Add(t);
                marketList.Children.Add(row);
            }
            if (hits.Count == 0) marketList.Children.Add(Txt("No matches.", 12, Muted, FontWeights.Normal));
            else if (hits.Count > (q.Length == 0 ? 8 : 25)) marketList.Children.Add(Txt("+ " + (hits.Count - (q.Length == 0 ? 8 : 25)) + " more. Type to search.", 11.5, Faint, FontWeights.Normal));
        }

        async void LoadPlugins()
        {
            pluginsLoading = true;
            List<PluginRow> rows;
            try { rows = await Actions.ListPlugins(); } catch { rows = new List<PluginRow>(); }
            plugins = rows;
            pluginsLoading = false;
            if (control) { controlDirty = true; Render(true); }
        }

        async void RunPluginOp(string verb, string id, string busyMsg)
        {
            Toast(busyMsg, true);
            var r = await Actions.Plugin(verb, id);
            if (r.Ok) r.Message = "Plugin " + verb + (verb.EndsWith("e") ? "d" : "ed") + ": " + id + " (applies to new sessions)";
            Toast(r);
            plugins = null;
            controlDirty = true;
            Render(true);
        }

        UIElement McpPanel()
        {
            var sp = new StackPanel();
            if (mcpOutput == null)
            {
                var b = Btn(mcpLoading ? "Checking…" : "Check MCP servers", IcPlug, Blue, false, async () =>
                {
                    if (mcpLoading) return;
                    mcpLoading = true; controlDirty = true; Render(true);
                    var r = await Actions.McpList();
                    mcpOutput = r.Output ?? r.Message ?? "";
                    mcpLoading = false; controlDirty = true; Render(true);
                });
                b.HorizontalAlignment = HorizontalAlignment.Left;
                sp.Children.Add(b);
                return sp;
            }
            var lines = mcpOutput.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();
            foreach (var l in lines)
            {
                var line = l;
                int colon = line.IndexOf(": ", StringComparison.Ordinal);
                bool isServer = colon > 0 && !line.StartsWith("Checking") && line.IndexOf(' ') > colon - 1;
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
                if (isServer)
                {
                    var name = line.Substring(0, colon).Trim();
                    var rm = Btn("Remove", "", Red, false, async () =>
                    {
                        if (!Confirm("Remove the MCP server '" + name + "' from Claude Code's config?")) return;
                        var r = await Actions.McpRemove(name);
                        if (r.Ok) r.Message = "Removed MCP server " + name;
                        Toast(r);
                        mcpOutput = null; controlDirty = true; Render(true);
                    });
                    rm.Margin = new Thickness(8, 0, 0, 0);
                    DockPanel.SetDock(rm, Dock.Right);
                    row.Children.Add(rm);
                }
                var ok = line.Contains("✓") ? Green : line.Contains("✗") || line.ToLowerInvariant().Contains("failed") ? Red : Fg2;
                var t = Txt(line, 11.5, isServer ? ok : Muted, FontWeights.Normal);
                t.FontFamily = Mono; t.TextWrapping = TextWrapping.Wrap;
                row.Children.Add(t);
                sp.Children.Add(row);
            }
            var again = Btn("Check again", IcHistory, Fg2, false, () => { mcpOutput = null; controlDirty = true; Render(true); });
            again.HorizontalAlignment = HorizontalAlignment.Left; again.Margin = new Thickness(0, 6, 0, 0);
            sp.Children.Add(again);
            return sp;
        }

        // ================= session actions =================
        UIElement SessionActions(Sess s)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
            var wp = new WrapPanel();
            wp.Children.Add(Btn("Resume in terminal", IcTerminal, Fg2, false, () =>
            {
                if (s.Live && !Confirm("This session is still open somewhere else. Resume it in a terminal anyway?")) return;
                Toast(Actions.LaunchInTerminal(s.Cwd, "--resume " + s.Id));
            }));
            wp.Children.Add(Btn("Open in desktop app", "", Fg2, false, () => Toast(Actions.OpenInDesktop(s.Id, s.Cwd))));
            if (s.Cwd != null) wp.Children.Add(Btn("Open folder", "", Fg2, false, () => Actions.OpenPath(s.Cwd)));
            wp.Children.Add(Btn("Show transcript", IcDoc, Fg2, false, () => Actions.RevealFile(s.File)));
            if (s.Live && s.Pid > 0)
                wp.Children.Add(Btn("Stop session", IcStop, Red, false, () =>
                {
                    if (Confirm("Stop \"" + s.Title + "\"?\n\nThis ends the Claude process (pid " + s.Pid + "). Anything it's doing right now is interrupted. The conversation stays on disk and can be resumed.")) Toast(Actions.StopProcess(s.Pid));
                }));
            sp.Children.Add(wp);
            var fork = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            var fl = Txt("Continue as a copy with", 12, Muted, FontWeights.Normal);
            fl.VerticalAlignment = VerticalAlignment.Center; fl.Margin = new Thickness(0, 0, 10, 6);
            fl.ToolTip = "Opens a terminal with a new copy of this conversation (--fork-session) running on the model you pick. The original session is untouched.";
            fork.Children.Add(fl);
            fork.Children.Add(Pills(new[] { "Fable", "Opus", "Sonnet", "Haiku" }, new[] { "fable", "opus", "sonnet", "haiku" }, null,
                v => Toast(Actions.LaunchInTerminal(s.Cwd, "--resume " + s.Id + " --fork-session --model " + v))));
            sp.Children.Add(fork);
            return sp;
        }

        // ================= feedback =================
        void Apply(ActionResult r)
        {
            Toast(r);
            controlDirty = true;
            Render(true);
        }

        void Toast(ActionResult r) { Toast(r.Message, r.Ok); }

        void Toast(string msg, bool ok)
        {
            var c = ok ? Green : Red;
            var b = new Border
            {
                Background = B("#22211E"), BorderBrush = Tint(c, 0x66), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
                Padding = new Thickness(14, 10, 14, 11), Margin = new Thickness(0, 8, 0, 0),
                Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 20, ShadowDepth = 4, Opacity = 0.5 }
            };
            var dp = new DockPanel();
            var ic = Glyph(ok ? IcCheck : IcError, 13, c); ic.Margin = new Thickness(0, 1, 10, 0); ic.VerticalAlignment = VerticalAlignment.Top;
            dp.Children.Add(ic);
            var t = Txt(msg, 12.5, Fg, FontWeights.Normal); t.TextWrapping = TextWrapping.Wrap;
            dp.Children.Add(t);
            b.Child = dp;
            b.MouseLeftButtonUp += delegate { toastHost.Children.Remove(b); };
            toastHost.Children.Add(b);
            while (toastHost.Children.Count > 4) toastHost.Children.RemoveAt(0);
            b.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(ok ? 5 : 9) };
            timer.Tick += delegate { timer.Stop(); toastHost.Children.Remove(b); };
            timer.Start();
        }

        bool Confirm(string text)
        {
            return MessageBox.Show(this, text, "Claude Monitor", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
        }

        // ================= input widgets =================
        static TextBox NewInput(string placeholder)
        {
            var tb = new TextBox { Tag = placeholder, ToolTip = placeholder };
            return tb;
        }

        static T Detach<T>(T el) where T : FrameworkElement
        {
            var p = el.Parent;
            if (p is Panel) ((Panel)p).Children.Remove(el);
            else if (p is Decorator) ((Decorator)p).Child = null;
            else if (p is ContentControl) ((ContentControl)p).Content = null;
            return el;
        }

        static TextBlock Label(string s)
        {
            var t = Txt(s.ToUpperInvariant(), 10.5, Muted, FontWeights.Bold);
            t.Margin = new Thickness(0, 14, 0, 7);
            return t;
        }
        static TextBlock SubLabel(string s)
        {
            var t = Txt(s, 11.5, Fg2, FontWeights.Normal);
            t.Margin = new Thickness(0, 4, 0, 5);
            return t;
        }
        static TextBlock Hint(string s, Brush c)
        {
            var t = Txt(s, 11.5, c, FontWeights.Normal);
            t.TextWrapping = TextWrapping.Wrap; t.Margin = new Thickness(0, 2, 0, 2);
            return t;
        }

        static UIElement Pills(string[] labels, string[] values, string current, Action<string> pick, bool small = false)
        {
            var wrap = new Border { CornerRadius = new CornerRadius(10), Background = Tint(Fg, 0x06), BorderBrush = LineSoft, BorderThickness = new Thickness(1), Padding = new Thickness(3), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6) };
            var wp = new WrapPanel();
            for (int i = 0; i < labels.Length; i++)
            {
                var v = values[i];
                bool on = string.Equals(v, current, StringComparison.OrdinalIgnoreCase);
                var b = new Border
                {
                    CornerRadius = new CornerRadius(7), Padding = small ? new Thickness(10, 3, 10, 4) : new Thickness(12, 5, 12, 6), Cursor = System.Windows.Input.Cursors.Hand,
                    Background = on ? Grad(Col(Accent2), Col(Accent), 90) : Brushes.Transparent
                };
                var t = Txt(labels[i], small ? 11.5 : 12, on ? Brushes.White : Fg2, on ? FontWeights.SemiBold : FontWeights.Normal);
                b.Child = t;
                if (!on)
                {
                    b.MouseEnter += delegate { b.Background = Tint(Fg, 0x0F); };
                    b.MouseLeave += delegate { b.Background = Brushes.Transparent; };
                    b.MouseLeftButtonUp += delegate { pick(v); };
                }
                wp.Children.Add(b);
            }
            wrap.Child = wp;
            return wrap;
        }

        static FrameworkElement Toggle(bool on, Action<bool> changed)
        {
            var track = new Border { Width = 38, Height = 22, CornerRadius = new CornerRadius(11), Cursor = System.Windows.Input.Cursors.Hand, VerticalAlignment = VerticalAlignment.Center };
            var knob = new Shapes.Ellipse { Width = 16, Height = 16, Fill = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(on ? 19 : 3, 0, 0, 0) };
            track.Background = on ? Grad(Col(Accent2), Col(Accent), 90) : Line;
            track.Child = knob;
            track.MouseLeftButtonUp += delegate { changed(!on); };
            return track;
        }

        static UIElement ToggleRow(string title, string desc, bool on, Action<bool> changed)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 10) };
            var tg = Toggle(on, changed);
            DockPanel.SetDock(tg, Dock.Right);
            row.Children.Add(tg);
            var t = new StackPanel();
            t.Children.Add(Txt(title, 13, Fg, FontWeights.SemiBold));
            var d = Txt(desc, 11.5, Faint, FontWeights.Normal); d.TextWrapping = TextWrapping.Wrap;
            t.Children.Add(d);
            row.Children.Add(t);
            return row;
        }

        static Border Btn(string text, string icon, Brush c, bool primary, Action click)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            if (icon != null)
            {
                var ic = Glyph(icon, 12, primary ? Brushes.White : c); ic.Margin = new Thickness(0, 1, 8, 0); ic.VerticalAlignment = VerticalAlignment.Center;
                sp.Children.Add(ic);
            }
            sp.Children.Add(Txt(text, 12.5, primary ? Brushes.White : (c == Fg2 ? Fg : c), FontWeights.SemiBold));
            Brush normal = primary ? Grad(Col(Accent2), Col(Accent), 90) : Tint(c == Fg2 ? Fg : c, 0x10);
            Brush hover = primary ? Grad(Col(Accent2), Col(Accent2), 90) : Tint(c == Fg2 ? Fg : c, 0x22);
            var b = new Border
            {
                Child = sp, Padding = primary ? new Thickness(18, 9, 18, 10) : new Thickness(12, 6, 13, 7), Margin = new Thickness(0, 0, 8, 8),
                CornerRadius = new CornerRadius(9), Background = normal, BorderBrush = primary ? Brushes.Transparent : Tint(c == Fg2 ? Fg : c, 0x2A), BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand, VerticalAlignment = VerticalAlignment.Center,
                Effect = primary ? new DropShadowEffect { Color = Col(Accent), BlurRadius = 14, ShadowDepth = 0, Opacity = 0.4 } : null
            };
            b.MouseEnter += delegate { b.Background = hover; };
            b.MouseLeave += delegate { b.Background = normal; };
            b.MouseLeftButtonUp += delegate { click(); };
            return b;
        }
    }
}
