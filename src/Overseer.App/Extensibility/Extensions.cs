using System.Reflection;
using Avalonia.Controls;
using Overseer.Core;

namespace Overseer.App.Extensibility;

/// <summary>
/// EXPERIMENTAL. What Overseer gives an extension.
/// </summary>
public interface IOverseerHost
{
    /// <summary>Top-level sessions, live first, newest first.</summary>
    IReadOnlyList<Session> Sessions { get; }
    /// <summary>Raised on a background thread whenever session data changes.</summary>
    event Action? Changed;
    void Toast(string message, bool ok);
}

/// <summary>
/// EXPERIMENTAL. Implement this in a .NET 8 class library that references Overseer, drop the DLL into
/// ~/.overseer/plugins, and turn on "Load extensions" in Control → Overseer. Each extension becomes a panel
/// on the Extensions page. Extensions run with your permissions, so only install ones you trust.
/// </summary>
public interface IOverseerExtension
{
    string Name { get; }
    Control CreateView(IOverseerHost host);
}

public sealed record ExtensionVM(string Name, Control View, string Source);

public static class ExtensionLoader
{
    public static List<ExtensionVM> Load(OverseerConfig cfg, IOverseerHost host)
    {
        var list = new List<ExtensionVM>();
        if (!cfg.LoadExtensions) return list;
        foreach (var dll in Directory.GetFiles(Paths.PluginsHostDir, "*.dll"))
        {
            try
            {
                var asm = Assembly.LoadFrom(dll);
                foreach (var t in asm.GetTypes().Where(t => typeof(IOverseerExtension).IsAssignableFrom(t) && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) != null))
                {
                    var ext = (IOverseerExtension)Activator.CreateInstance(t)!;
                    list.Add(new ExtensionVM(ext.Name, ext.CreateView(host), Path.GetFileName(dll)));
                }
            }
            catch (Exception ex)
            {
                list.Add(new ExtensionVM(Path.GetFileName(dll), new TextBlock { Text = "Couldn't load: " + ex.Message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, dll));
            }
        }
        return list;
    }
}
