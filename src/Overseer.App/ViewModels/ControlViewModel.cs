using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Overseer.Core;

namespace Overseer.App.ViewModels;

/// <summary>A segmented picker bound to one settings value. Writes through <paramref name="write"/> when the user picks.</summary>
public sealed partial class PickerVM : ObservableObject
{
    readonly Func<string?, ActionResult>? write;
    readonly Action<ActionResult>? report;
    bool loading;

    public PickerVM(string label, Option[] options, string? current, Func<string?, ActionResult>? write, Action<ActionResult>? report, string? hint = null)
    {
        Label = label; Items = options; this.write = write; this.report = report; Hint = hint;
        loading = true;
        selected = Options.Pick(options, current);
        loading = false;
    }

    public string Label { get; }
    public bool HasLabel => Label.Length > 0;
    public string? Hint { get; }
    public bool HasHint => Hint != null;
    public Option[] Items { get; }
    [ObservableProperty] Option? selected;

    partial void OnSelectedChanged(Option? value)
    {
        if (loading || value == null || write == null) return;
        report?.Invoke(write(value.Value));
    }
}

public sealed record PresetVM(Preset Preset, string Summary);

public sealed partial class SkillRowVM : ObservableObject
{
    public SkillRowVM(SkillInfo info, string state, Action<ActionResult> report)
    {
        Name = info.Name; Description = info.Description ?? ""; IsPersonal = info.Source == "personal";
        Picker = new PickerVM("", Options.Of(("On", "on"), ("Name only", "name-only"), ("Manual", "user-invocable-only"), ("Off", "off")), state,
            v => SettingsFile.SetSkillState(info.Name, v ?? "on"), report);
    }
    public string Name { get; }
    public string Description { get; }
    public bool IsPersonal { get; }
    public string SourceText => IsPersonal ? "yours" : "synced";
    public PickerVM Picker { get; }
}

public sealed partial class PluginVM : ObservableObject
{
    readonly ControlViewModel owner;
    bool loading = true;
    public PluginVM(ControlViewModel owner, PluginRow row)
    {
        this.owner = owner; Row = row; enabled = row.Enabled; loading = false;
    }
    public PluginRow Row { get; }
    public string Name => Row.Name + (Row.Version != null ? "   v" + Row.Version : "");
    public string Meta => string.Join(" · ", new[] { Row.Scope, Row.Scope == "synced" ? "managed by your account" : null, Row.Marketplace != Row.Scope ? Row.Marketplace : null }.Where(x => !string.IsNullOrEmpty(x)));
    public string Description => Row.Description ?? "";
    public bool CanUninstall => Row.Installed && Row.Scope != "synced";
    [ObservableProperty] bool enabled;
    partial void OnEnabledChanged(bool value) { if (!loading) _ = owner.PluginOp(value ? "enable" : "disable", Row); }
}

public sealed record McpLineVM(string Text, IBrush Brush, bool IsServer, string? Name);

public sealed partial class ControlViewModel : ObservableObject
{
    readonly MainViewModel main;
    public ControlViewModel(MainViewModel main) => this.main = main;

    static readonly Option[] ModelOpts = Options.Of(("Default", null), ("Fable", "fable"), ("Opus", "opus"), ("Sonnet", "sonnet"), ("Haiku", "haiku"), ("Opus Plan", "opusplan"));
    static readonly Option[] EffortOpts = Options.Of(("Auto", null), ("Low", "low"), ("Medium", "medium"), ("High", "high"), ("X-High", "xhigh"));
    static readonly Option[] ModeOpts = Options.Of(("Ask first", null), ("Accept edits", "acceptEdits"), ("Plan", "plan"), ("Auto", "auto"), ("Don't ask", "dontAsk"));

    // defaults
    [ObservableProperty] PickerVM? model;
    [ObservableProperty] string customModel = "";
    [ObservableProperty] string customModelNote = "";
    [ObservableProperty] List<PickerVM> efforts = new();
    [ObservableProperty] PickerVM? permissionMode;
    [ObservableProperty] bool bypassWarning;
    [ObservableProperty] PickerVM? outputStyle;
    [ObservableProperty] bool thinking;
    [ObservableProperty] bool spinnerTips;

    // presets & launch
    [ObservableProperty] List<PresetVM> presets = new();
    [ObservableProperty] string launchFolder = Paths.Home;
    [ObservableProperty] string launchName = "";
    [ObservableProperty] List<string> recentFolders = new();
    public PickerVM LaunchModel { get; } = new("Model", ModelOpts, null, null, null);
    public PickerVM LaunchEffort { get; } = new("Effort", Options.Of(("Default", null), ("Low", "low"), ("Medium", "medium"), ("High", "high"), ("X-High", "xhigh"), ("Max", "max")), null, null, null);
    public PickerVM LaunchMode { get; } = new("Permission mode", Options.Of(("Default", null), ("Ask first", "manual"), ("Accept edits", "acceptEdits"), ("Plan", "plan"), ("Auto", "auto")), null, null, null);

    // live data
    [ObservableProperty] bool hooksOn;
    [ObservableProperty] bool statusLineOn;
    [ObservableProperty] string existingStatusLine = "";

    // skills, plugins, mcp
    [ObservableProperty] List<SkillRowVM> skills = new();
    [ObservableProperty] string newSkillName = "";
    public ObservableCollection<PluginVM> InstalledPlugins { get; } = new();
    public ObservableCollection<PluginVM> Market { get; } = new();
    [ObservableProperty] string marketFilter = "";
    [ObservableProperty] string marketNote = "";
    [ObservableProperty] bool pluginsLoading;
    List<PluginRow>? allPlugins;
    public ObservableCollection<McpLineVM> McpLines { get; } = new();
    [ObservableProperty] bool mcpLoading;
    [ObservableProperty] string mcpName = "";
    [ObservableProperty] string mcpTarget = "";
    [ObservableProperty] string mcpArgs = "";
    public PickerVM McpTransport { get; } = new("Type", Options.Of(("Command (stdio)", "stdio"), ("HTTP", "http"), ("SSE", "sse")), "stdio", null, null);
    public PickerVM McpScope { get; } = new("Scope", Options.Of(("All projects (user)", "user"), ("This machine, one project (local)", "local"), ("Shared with the repo (project)", "project")), "user", null, null);

    // overseer's own settings
    [ObservableProperty] bool notifyOnFinish;
    [ObservableProperty] bool notifyOnPermission;
    [ObservableProperty] bool notifyOnErrors;
    [ObservableProperty] bool closeToTray;
    [ObservableProperty] bool startMinimized;
    [ObservableProperty] bool loadExtensions;
    [ObservableProperty] decimal contextAlert;
    [ObservableProperty] decimal limitAlert;
    [ObservableProperty] decimal fiveHourBudget;
    [ObservableProperty] PickerVM? theme;

    bool loading;

    public void Load()
    {
        loading = true;
        var settings = SettingsFile.Read() ?? new JsonObject();
        var cur = SettingsFile.GetString("model");
        Model = new PickerVM("", ModelOpts, cur, v => SettingsFile.SetString(v, "model"), Report);
        var known = ModelOpts.Any(o => string.Equals(o.Value, cur, StringComparison.OrdinalIgnoreCase));
        CustomModelNote = !known && cur != null ? "Currently set to a custom model: " + cur : "";
        if (!known && cur != null && CustomModel.Length == 0) CustomModel = cur;

        var ids = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        if (settings["modelSettings"] is JsonObject ms) foreach (var kv in ms) ids.Add(kv.Key);
        foreach (var s in main.Store.All) if (s.Model?.StartsWith("claude-", StringComparison.Ordinal) == true) ids.Add(s.Model.Replace("[1m]", ""));
        var eff = ids.Select(id => new PickerVM($"{Text.PrettyModel(id)} · {id}", EffortOpts, SettingsFile.GetString("modelSettings", id, "effortLevel"),
            v => SettingsFile.SetString(v, "modelSettings", id, "effortLevel"), Report)).ToList();
        eff.Add(new PickerVM("All other models", EffortOpts, SettingsFile.GetString("effortLevel"), v => SettingsFile.SetString(v, "effortLevel"), Report));
        Efforts = eff;

        var mode = SettingsFile.GetString("permissions", "defaultMode");
        BypassWarning = mode == "bypassPermissions";
        PermissionMode = new PickerVM("", ModeOpts, mode is "default" or "manual" ? null : mode, v => SettingsFile.SetString(v, "permissions", "defaultMode"), Report);
        OutputStyle = new PickerVM("", Options.Of(("Default", null), ("Explanatory", "Explanatory"), ("Learning", "Learning")), SettingsFile.GetString("outputStyle"),
            v => SettingsFile.SetString(v, "outputStyle"), Report);
        Thinking = SettingsFile.GetBool("alwaysThinkingEnabled") != false;
        SpinnerTips = SettingsFile.GetBool("spinnerTipsEnabled") != false;

        Presets = Core.Presets.Load().Select(p => new PresetVM(p, string.Join(" · ", new[] { p.Model, p.Effort, p.PermissionMode }.Where(x => x != null)))).ToList();
        RecentFolders = main.Store.TopLevel().Where(s => s.Cwd != null && Directory.Exists(s.Cwd)).Select(s => s.Cwd!).Distinct().Take(5).ToList();

        HooksOn = Integration.HooksInstalled();
        StatusLineOn = Integration.StatusLineInstalled();
        var sl = Integration.CurrentStatusLineCommand();
        ExistingStatusLine = sl != null && !StatusLineOn ? "You already have a status line: " + sl : "";

        Skills = main.Store.InstalledSkills.Select(x => new SkillRowVM(x, SettingsFile.SkillState(x.Name), Report)).ToList();

        var c = main.Config;
        NotifyOnFinish = c.NotifyOnFinish; NotifyOnPermission = c.NotifyOnPermission; NotifyOnErrors = c.NotifyOnErrors;
        CloseToTray = c.CloseToTray; StartMinimized = c.StartMinimized; LoadExtensions = c.LoadExtensions;
        ContextAlert = c.ContextAlertPercent; LimitAlert = c.LimitAlertPercent; FiveHourBudget = c.FiveHourTokenBudget;
        Theme = new PickerVM("Theme", Options.Of(("Dark", "dark"), ("Light", "light"), ("System", "system")), c.Theme, v => { main.SetTheme(v ?? "dark"); return ActionResult.Good("Theme changed"); }, null);
        loading = false;

        if (allPlugins == null && !PluginsLoading) _ = LoadPlugins();
    }

    void Report(ActionResult r) => main.Toast(r);

    // ---------- defaults ----------
    [RelayCommand]
    void UseCustomModel()
    {
        var t = CustomModel.Trim();
        if (t.Length == 0) return;
        Report(SettingsFile.SetString(t, "model"));
        Load();
    }

    partial void OnThinkingChanged(bool value) { if (!loading) Report(SettingsFile.Set(value ? null : JsonValue.Create(false), "alwaysThinkingEnabled")); }
    partial void OnSpinnerTipsChanged(bool value) { if (!loading) Report(SettingsFile.Set(value ? null : JsonValue.Create(false), "spinnerTipsEnabled")); }

    [RelayCommand] void OpenSettings() => ProcessControl.Open(SettingsFile.FilePath);

    [RelayCommand]
    async Task RestoreBackup()
    {
        if (!await main.ConfirmAsync("Replace your current settings.json with the copy Overseer saved before its first change?", "Restore")) return;
        Report(SettingsFile.RestoreBackup());
        Load();
    }

    // ---------- presets & launch ----------
    [RelayCommand]
    void ApplyPreset(PresetVM p) { Report(Core.Presets.ApplyAsDefault(p.Preset)); Load(); }

    [RelayCommand]
    void LaunchPreset(PresetVM p) =>
        Report(Terminal.LaunchClaude(LaunchFolder, ClaudeCli.SessionArgs(p.Preset.Model, p.Preset.Effort, p.Preset.PermissionMode, LaunchName)));

    [RelayCommand] void EditPresets() { Core.Presets.Save(Core.Presets.Load()); ProcessControl.Open(Paths.PresetsFile); }

    [RelayCommand]
    async Task BrowseFolder()
    {
        var f = await main.PickFolderAsync("Folder for the new Claude session");
        if (f != null) LaunchFolder = f;
    }

    [RelayCommand] void PickRecent(string folder) => LaunchFolder = folder;

    [RelayCommand]
    void Launch()
    {
        if (!Directory.Exists(LaunchFolder)) { main.Toast("That folder doesn't exist.", false); return; }
        Report(Terminal.LaunchClaude(LaunchFolder, ClaudeCli.SessionArgs(LaunchModel.Selected?.Value, LaunchEffort.Selected?.Value, LaunchMode.Selected?.Value, LaunchName)));
    }

    // ---------- live data ----------
    static string ExePath => Environment.ProcessPath ?? "overseer";

    partial void OnHooksOnChanged(bool value)
    {
        if (loading) return;
        Report(value ? Integration.InstallHooks(ExePath) : Integration.UninstallHooks());
    }

    partial void OnStatusLineOnChanged(bool value)
    {
        if (loading) return;
        _ = SetStatusLine(value);
    }

    async Task SetStatusLine(bool on)
    {
        if (!on) { Report(Integration.UninstallStatusLine()); return; }
        var existing = Integration.CurrentStatusLineCommand();
        bool replace = false;
        if (existing != null && !Integration.StatusLineInstalled())
        {
            replace = await main.ConfirmAsync($"You already have a status line:\n{existing}\n\nReplace it with Overseer's? (It shows model, context %, 5-hour and weekly use.)", "Replace");
            if (!replace) { loading = true; StatusLineOn = false; loading = false; return; }
        }
        Report(Integration.InstallStatusLine(ExePath, replace));
        Load();
    }

    // ---------- skills ----------
    [RelayCommand]
    void CreateSkill()
    {
        var r = ClaudeFiles.CreateSkill(NewSkillName);
        Report(r);
        if (r.Ok) { NewSkillName = ""; main.Store.ForceRescan(); }
    }

    [RelayCommand]
    async Task InstallSkill()
    {
        var f = await main.PickFolderAsync("Pick a skill folder (it must contain SKILL.md)");
        if (f == null) return;
        Report(ClaudeFiles.InstallSkillFromFolder(f));
        main.Store.ForceRescan();
    }

    [RelayCommand] void OpenSkillsFolder() { Directory.CreateDirectory(Paths.SkillsDir); ProcessControl.Open(Paths.SkillsDir); }

    // ---------- plugins ----------
    async Task LoadPlugins()
    {
        PluginsLoading = true;
        try { allPlugins = await ClaudeCli.ListPlugins(); } catch { allPlugins = new(); }
        PluginsLoading = false;
        InstalledPlugins.Clear();
        foreach (var p in allPlugins.Where(x => x.Installed)) InstalledPlugins.Add(new PluginVM(this, p));
        FillMarket();
    }

    partial void OnMarketFilterChanged(string value) => FillMarket();

    void FillMarket()
    {
        Market.Clear();
        if (allPlugins == null) return;
        var q = MarketFilter.Trim();
        var hits = allPlugins.Where(x => !x.Installed && (q.Length == 0 || (x.Name + " " + x.Description).Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
        var take = q.Length == 0 ? 8 : 30;
        foreach (var p in hits.Take(take)) Market.Add(new PluginVM(this, p));
        MarketNote = hits.Count == 0 ? "No matches." : hits.Count > take ? $"{hits.Count - take} more. Type to search." : "";
    }

    [RelayCommand] async Task ReloadPlugins() { allPlugins = null; await LoadPlugins(); }

    [RelayCommand]
    async Task InstallPlugin(PluginVM p)
    {
        if (!await main.ConfirmAsync($"Install {p.Row.Id}?\n\nPlugins can run code on your computer. Only install ones you trust.", "Install")) return;
        await PluginOp("install", p.Row);
    }

    [RelayCommand]
    async Task UninstallPlugin(PluginVM p)
    {
        if (!await main.ConfirmAsync($"Uninstall {p.Row.Id}?", "Uninstall")) return;
        await PluginOp("uninstall", p.Row);
    }

    public async Task PluginOp(string verb, PluginRow row)
    {
        main.Toast($"{char.ToUpper(verb[0]) + verb[1..]}ing {row.Name}…", true);
        var r = await ClaudeCli.Plugin(verb, row.Id);
        main.Toast(r.Ok ? $"Plugin {verb} done: {row.Id} (applies to new sessions)" : r.Message, r.Ok);
        await ReloadPlugins();
    }

    // ---------- MCP ----------
    [RelayCommand]
    async Task CheckMcp()
    {
        McpLoading = true;
        McpLines.Clear();
        var r = await ClaudeCli.McpList();
        McpLoading = false;
        foreach (var raw in (r.Output ?? r.Message).Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0))
        {
            var colon = raw.IndexOf(": ", StringComparison.Ordinal);
            // server lines look like "name: command-or-url - ✓ Connected"; the name has no spaces
            var isServer = colon > 0 && !raw.StartsWith("Checking", StringComparison.Ordinal) && raw.IndexOf(' ') >= colon;
            var key = raw.Contains('✓') ? "Green" : raw.Contains('✗') || raw.Contains("failed", StringComparison.OrdinalIgnoreCase) ? "Red" : isServer ? "Fg2" : "Muted";
            McpLines.Add(new McpLineVM(raw, Res.Brush(key), isServer && colon > 0, colon > 0 ? raw[..colon].Trim() : null));
        }
    }

    [RelayCommand]
    async Task RemoveMcp(McpLineVM line)
    {
        if (line.Name == null || !await main.ConfirmAsync($"Remove the MCP server '{line.Name}' from Claude Code's config?", "Remove")) return;
        var r = await ClaudeCli.McpRemove(line.Name);
        main.Toast(r.Ok ? "Removed MCP server " + line.Name : r.Message, r.Ok);
        await CheckMcp();
    }

    [RelayCommand]
    async Task AddMcp()
    {
        if (McpName.Trim().Length == 0 || McpTarget.Trim().Length == 0) { main.Toast("Give the server a name and a command or URL.", false); return; }
        var transport = McpTransport.Selected?.Value ?? "stdio";
        var args = McpArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!await main.ConfirmAsync($"Add MCP server '{McpName.Trim()}'?\n{transport}: {McpTarget} {McpArgs}\n\nMCP servers can run code and reach the network. Only add ones you trust.", "Add")) return;
        var r = await ClaudeCli.McpAdd(McpName.Trim(), transport, McpScope.Selected?.Value ?? "user", McpTarget.Trim(), args);
        main.Toast(r.Ok ? "Added MCP server " + McpName.Trim() : r.Message, r.Ok);
        if (r.Ok) { McpName = ""; McpTarget = ""; McpArgs = ""; await CheckMcp(); }
    }

    // ---------- overseer settings ----------
    partial void OnNotifyOnFinishChanged(bool value) => SaveConfig(c => c.NotifyOnFinish = value);
    partial void OnNotifyOnPermissionChanged(bool value) => SaveConfig(c => c.NotifyOnPermission = value);
    partial void OnNotifyOnErrorsChanged(bool value) => SaveConfig(c => c.NotifyOnErrors = value);
    partial void OnCloseToTrayChanged(bool value) => SaveConfig(c => c.CloseToTray = value);
    partial void OnStartMinimizedChanged(bool value) => SaveConfig(c => c.StartMinimized = value);
    partial void OnLoadExtensionsChanged(bool value) => SaveConfig(c => c.LoadExtensions = value, value ? "Extensions load the next time Overseer starts." : null);
    partial void OnContextAlertChanged(decimal value) => SaveConfig(c => c.ContextAlertPercent = (int)value);
    partial void OnLimitAlertChanged(decimal value) => SaveConfig(c => c.LimitAlertPercent = (int)value);
    partial void OnFiveHourBudgetChanged(decimal value) => SaveConfig(c => c.FiveHourTokenBudget = (long)value);

    void SaveConfig(Action<OverseerConfig> set, string? message = null)
    {
        if (loading) return;
        set(main.Config);
        main.Config.Save();
        if (message != null) main.Toast(message, true);
    }

    [RelayCommand] void TestNotification() => Notifier.Show("Overseer", "Notifications are working.");
    [RelayCommand] void OpenOverseerFolder() => ProcessControl.Open(Paths.OverseerDir);
}
