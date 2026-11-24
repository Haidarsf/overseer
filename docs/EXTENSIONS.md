# Extensions (experimental)

Extensions add panels to an **Extensions** page. The API may change between releases.

> Extensions run inside Overseer with your full permissions. They're off by default. Only install ones you trust.

## Write one

1. Create a .NET 8 class library and reference `Overseer.dll` (from a release or your own build) and the same Avalonia version Overseer uses.
2. Implement `Overseer.App.Extensibility.IOverseerExtension` with a public parameterless constructor:

```csharp
using Avalonia.Controls;
using Overseer.App.Extensibility;

public sealed class SessionCount : IOverseerExtension
{
    public string Name => "Session count";

    public Control CreateView(IOverseerHost host)
    {
        var text = new TextBlock { Text = $"{host.Sessions.Count} sessions" };
        host.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(
            () => text.Text = $"{host.Sessions.Count} sessions");
        return text;
    }
}
```

3. Build it and copy the DLL into `~/.overseer/plugins/`.
4. In Overseer, turn on **Control → Overseer → Load extensions** and restart.

## The host

| Member | |
| --- | --- |
| `IReadOnlyList<Session> Sessions` | Top-level sessions, live first, newest first (`Overseer.Core.Session`) |
| `event Action Changed` | Raised on a background thread when data changes; marshal to the UI thread |
| `void Toast(string message, bool ok)` | Shows a notification inside Overseer |

Extensions must not write to Claude Code's files directly. Ask in an issue if you need an API for that, so it can go through the same safety checks as the rest of the app.
