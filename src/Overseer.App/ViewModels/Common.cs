using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Overseer.Core;

namespace Overseer.App.ViewModels;

/// <summary>Looks up theme brushes and icons from the app resources.</summary>
public static class Res
{
    public static IBrush Brush(string key)
    {
        var app = Application.Current;
        if (app != null && app.TryGetResource(key, app.ActualThemeVariant, out var v) && v is IBrush b) return b;
        return Brushes.Gray;
    }

    public static IBrush Tint(string key, byte alpha)
    {
        if (Brush(key) is ISolidColorBrush s) return new SolidColorBrush(Color.FromArgb(alpha, s.Color.R, s.Color.G, s.Color.B));
        return Brushes.Transparent;
    }

    public static Geometry? Icon(string key)
    {
        var app = Application.Current;
        return app != null && app.TryGetResource("Icon." + key, null, out var v) ? v as Geometry : null;
    }

    /// <summary>Dot colour per state: clay = needs you, sky = working, olive = your turn, gray = done.</summary>
    public static string StateKey(SessionState s) => s switch
    {
        SessionState.Working => "WorkDot",
        SessionState.NeedsPermission => "Accent",
        SessionState.Idle => "Green",
        _ => "DoneDot",
    };

    /// <summary>Text colour per state (readable on the panel background).</summary>
    public static string StateInkKey(SessionState s) => s switch
    {
        SessionState.Working => "WorkFg",
        SessionState.NeedsPermission => "NeedFg",
        SessionState.Idle => "Green",
        _ => "Muted",
    };

    public static string StateText(SessionState s) => s switch
    {
        SessionState.Working => "Working",
        SessionState.NeedsPermission => "Needs you",
        SessionState.Idle => "Your turn",
        SessionState.Active => "Done",
        _ => "Done",
    };

    public static bool IsDone(SessionState s) => s is SessionState.Active or SessionState.Ended;

    public static string ToolIcon(string? tool) => Tools.Category(tool) switch
    {
        "terminal" => "Terminal", "read" => "File", "edit" => "Edit", "search" => "Search", "web" => "Globe",
        "agent" => "Robot", "skill" => "Bolt", "plan" => "List", "mcp" => "Puzzle", _ => "Tool",
    };
}

/// <summary>One option in a segmented picker.</summary>
public sealed record Option(string Label, string? Value, string? Description = null)
{
    public bool HasDescription => !string.IsNullOrEmpty(Description);
    public override string ToString() => Label;
}

public static class Options
{
    public static Option[] Of(params (string label, string? value)[] items) => items.Select(i => new Option(i.label, i.value)).ToArray();
    public static Option[] Described(params (string label, string? value, string description)[] items) => items.Select(i => new Option(i.label, i.value, i.description)).ToArray();
    public static Option? Pick(IEnumerable<Option> opts, string? value) =>
        opts.FirstOrDefault(o => string.Equals(o.Value, value, StringComparison.OrdinalIgnoreCase));
}

public sealed record BarVM(string Label, string ValueText, double Fraction, IBrush Brush, Geometry? Icon, string Tooltip)
{
    public bool HasIcon => Icon != null;

    public static List<BarVM> From(IEnumerable<KeyValuePair<string, long>> data, string brushKey, int max, bool toolIcons = false, Func<string, string>? label = null)
    {
        var list = data.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).Take(max).ToList();
        if (list.Count == 0) return new();
        var top = (double)list[0].Value;
        var brush = Res.Brush(brushKey);
        return list.Select(kv =>
        {
            var l = label?.Invoke(kv.Key) ?? kv.Key;
            return new BarVM(l, Text.Tokens(kv.Value), Math.Max(0.02, kv.Value / top), brush, toolIcons ? Res.Icon(Res.ToolIcon(kv.Key)) : null, $"{l}: {kv.Value:N0}");
        }).ToList();
    }
}

public sealed record ColumnVM(double Height, IBrush Brush, string Tooltip, string? Label);

public sealed record ChipVM(string Text, IBrush Brush, Geometry? Icon, string? Tooltip = null)
{
    public bool HasIcon => Icon != null;
}

public sealed partial class ToastVM : ObservableObject
{
    public string Message { get; init; } = "";
    public bool Ok { get; init; } = true;
    public Action? Undo { get; init; }
    public string UndoLabel { get; init; } = "Undo";
    public bool HasUndo => Undo != null;
    public IBrush Brush => Res.Brush(Ok ? "Green" : "Red");
}
