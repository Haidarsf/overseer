using Avalonia;
using Overseer.Core;

namespace Overseer.App;

public static class Program
{
    /// <summary>
    /// `overseer`             opens the app
    /// `overseer hook`        called by Claude Code hooks: records the event, prints nothing, exits 0
    /// `overseer statusline`  called by Claude Code's status line: records the snapshot, prints one line
    /// `overseer --version`
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0)
        {
            if (args[0] is "hook" or "statusline")
            {
                // Claude Code talks UTF-8 on stdin/stdout; Windows consoles default to the OEM code page
                var utf8 = new System.Text.UTF8Encoding(false);
                try { Console.InputEncoding = utf8; Console.OutputEncoding = utf8; } catch { }
            }
            switch (args[0])
            {
                case "hook": return Integration.RunHook(Console.In);
                case "statusline": return Integration.RunStatusLine(Console.In, Console.Out);
                case "--version": Console.WriteLine("Overseer " + typeof(Program).Assembly.GetName().Version); return 0;
                case "--remove-integration":
                    // used by the uninstaller: take out only the hooks and status line Overseer added
                    var h = Integration.AnyHookInstalled() ? Integration.UninstallHooks() : ActionResult.Good("No Overseer hooks installed.");
                    var s = Integration.UninstallStatusLine();
                    Console.WriteLine(h.Message);
                    Console.WriteLine(s.Message);
                    return h.Ok && s.Ok ? 0 : 1;
                case "--demo":
                    // made-up sessions in a temp folder; your real ~/.claude is not read or changed
                    var (claude, home) = Demo.Create(Path.Combine(Path.GetTempPath(), "overseer-demo"));
                    Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", claude);
                    Environment.SetEnvironmentVariable("OVERSEER_HOME", home);
                    break;
            }
        }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
