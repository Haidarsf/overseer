using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Overseer.Core;

namespace Overseer.App.ViewModels;

public sealed partial class RuleGroupVM : ObservableObject
{
    readonly ConfigureViewModel owner;
    public RuleGroupVM(ConfigureViewModel owner, string kind, string title, string hint)
    {
        this.owner = owner; Kind = kind; Title = title; Hint = hint;
    }
    public string Kind { get; }
    public string Title { get; }
    public string Hint { get; }
    public ObservableCollection<string> Rules { get; } = new();
    [ObservableProperty] string newRule = "";

    public void Load() { Rules.Clear(); foreach (var r in SettingsFile.Rules(Kind)) Rules.Add(r); }

    [RelayCommand]
    void Add()
    {
        var r = SettingsFile.AddRule(Kind, NewRule);
        owner.Report(r);
        if (r.Ok) { NewRule = ""; Load(); }
    }

    [RelayCommand]
    async Task Remove(string rule)
    {
        if (!await owner.Main.ConfirmAsync($"Remove \"{rule}\" from {Kind}?", "Remove")) return;
        owner.Report(SettingsFile.RemoveRule(Kind, rule));
        Load();
    }
}

public sealed record HookRowVM(string Event, string Matcher, string Command, bool IsOverseer);
public sealed record MdItemVM(string Name, string Description, string FilePath);

public sealed partial class ConfigureViewModel : ObservableObject
{
    public MainViewModel Main { get; }
    public ConfigureViewModel(MainViewModel main)
    {
        Main = main;
        RuleGroups = new()
        {
            new(this, "allow", "Always allow", "Claude runs these without asking, e.g. Bash(npm test:*) or Read(./src/**)"),
            new(this, "ask", "Always ask", "Claude asks first, even in modes that would otherwise allow it"),
            new(this, "deny", "Never allow", "Claude can't use these at all, e.g. Read(./.env) or WebFetch"),
        };
    }

    public List<RuleGroupVM> RuleGroups { get; }
    public ObservableCollection<HookRowVM> Hooks { get; } = new();
    public ObservableCollection<MdItemVM> Agents { get; } = new();
    public ObservableCollection<MdItemVM> Commands { get; } = new();
    [ObservableProperty] List<Option> memoryFiles = new();
    [ObservableProperty] Option? memoryFile;
    [ObservableProperty] string memoryText = "";
    [ObservableProperty] bool memoryDirty;
    [ObservableProperty] string newAgentName = "";
    [ObservableProperty] string newCommandName = "";
    bool loadingMemory;

    public void Report(ActionResult r) => Main.Toast(r);

    public void Load()
    {
        foreach (var g in RuleGroups) g.Load();
        Hooks.Clear();
        foreach (var (ev, matcher, cmd) in SettingsFile.HookRows())
            Hooks.Add(new HookRowVM(ev, string.IsNullOrEmpty(matcher) ? "all" : matcher, cmd, cmd.Contains("overseer", StringComparison.OrdinalIgnoreCase)));
        Agents.Clear();
        foreach (var a in Main.Store.Agents) Agents.Add(new MdItemVM(a.Name, a.Description ?? "", a.FilePath));
        Commands.Clear();
        foreach (var c in Main.Store.Commands) Commands.Add(new MdItemVM("/" + c.Name, c.Description ?? "", c.FilePath));

        var files = new List<Option> { new("Your instructions for every project (~/.claude/CLAUDE.md)", Paths.UserMemoryFile) };
        foreach (var cwd in Main.Store.TopLevel().Select(s => s.Cwd).Where(c => c != null && Directory.Exists(c)).Distinct().Take(6))
            files.Add(new Option("Project: " + Paths.Short(cwd) + "/CLAUDE.md", Path.Combine(cwd!, "CLAUDE.md")));
        MemoryFiles = files;
        if (MemoryFile == null || !files.Any(f => f.Value == MemoryFile.Value)) MemoryFile = files[0];
    }

    partial void OnMemoryFileChanged(Option? value)
    {
        if (value?.Value == null) return;
        loadingMemory = true;
        MemoryText = ClaudeFiles.ReadText(value.Value);
        MemoryDirty = false;
        loadingMemory = false;
    }

    partial void OnMemoryTextChanged(string value) { if (!loadingMemory) MemoryDirty = true; }

    [RelayCommand]
    void SaveMemory()
    {
        if (MemoryFile?.Value == null) return;
        Report(ClaudeFiles.SaveText(MemoryFile.Value, MemoryText));
        MemoryDirty = false;
    }

    [RelayCommand] void RevertMemory() => OnMemoryFileChanged(MemoryFile);
    [RelayCommand] void OpenMemory() { if (MemoryFile?.Value != null && File.Exists(MemoryFile.Value)) ProcessControl.Open(MemoryFile.Value); }

    [RelayCommand]
    async Task RemoveHook(HookRowVM h)
    {
        if (!await Main.ConfirmAsync($"Remove this {h.Event} hook?\n{h.Command}", "Remove")) return;
        Report(SettingsFile.RemoveHook(h.Event, h.Command));
        Load();
    }

    [RelayCommand] void CreateAgent() { var r = ClaudeFiles.CreateMarkdown("agent", NewAgentName); Report(r); if (r.Ok) { NewAgentName = ""; Main.Store.ForceRescan(); } }
    [RelayCommand] void CreateCommand() { var r = ClaudeFiles.CreateMarkdown("command", NewCommandName); Report(r); if (r.Ok) { NewCommandName = ""; Main.Store.ForceRescan(); } }
    [RelayCommand] void OpenItem(MdItemVM m) => ProcessControl.Open(m.FilePath);

    [RelayCommand]
    async Task TrashItem(MdItemVM m)
    {
        if (!await Main.ConfirmAsync($"Move {m.Name} to Overseer's trash folder? You can restore it from {Paths.TrashDir}.", "Move to trash")) return;
        Report(ClaudeFiles.Trash(m.FilePath));
        Main.Store.ForceRescan();
        if (Agents.Contains(m)) Agents.Remove(m); else Commands.Remove(m);
    }
}
