# Claude Monitor

A Windows desktop app that shows what Claude Code is doing and lets you control it.


## What it does

**Session tab**: live view of one session.
- What Claude is doing right now (running a tool, thinking, writing, waiting for you)
- Model, Claude Code version, permission mode, working folder
- How full the context window is, token use (input, output, cache), tool calls, errors, subagents
- Activity timeline, tools and MCP servers used, skills invoked and available
- Actions: resume in a terminal, open in the desktop app, open the folder or transcript, stop a live session, or continue as a copy on another model

**Overview tab**: usage across all sessions.
- Token totals for the last 5 hours, today and the last 7 days, and tokens per hour over 24 hours
- Models, top tools, MCP servers, skills, installed skills and plugins, settings

**Control tab**: change Claude Code's configuration.
- Defaults for new sessions: model, effort per model, permission mode, output style, thinking, spinner tips
- Skills: On / Name only / Manual / Off (Claude Code's `skillOverrides` setting); install a skill from a folder or create a new one
- Plugins: turn on or off, uninstall, search and install from the marketplace (`claude plugin …`)
- MCP servers: check status, remove (`claude mcp …`)
- Start a new session in a terminal with a chosen folder, model, effort and permission mode

## Build and run

No installs needed: it builds with the C# compiler that ships with Windows (.NET Framework 4.x).

```powershell
.\build.ps1
.\ClaudeMonitor.exe
```

To redraw the icon, edit and run `assets\make-icon.ps1`, then build again.

## How it works

| File | Purpose |
| --- | --- |
| `src/Data.cs` | Reads `~/.claude` (session transcripts, live sessions, skills, plugins, settings). Read-only. |
| `src/Actions.cs` | All changes: `settings.json` edits, skill install/create, `claude` CLI calls, launching terminals. |
| `src/MainWindow.cs` | Window, theme, Session and Overview tabs. |
| `src/ControlView.cs` | Control tab, session action buttons, pop-up notifications, input widgets. |
| `assets/` | Icon and the script that draws it. |

It respects `CLAUDE_CONFIG_DIR` if you have set it.

### Safety

- Settings are changed only through Claude Code's own `settings.json` keys or the official `claude` CLI.
- The file is re-read before each save, all other keys are kept, and a backup is written to `settings.json.monitor-backup` before the first change.
- An invalid `settings.json` is never overwritten.
- The "bypass all permissions" mode is deliberately not offered.
- It never reads `.credentials.json`.

### Limits

- Plan rate limits aren't stored locally, so the usage numbers are worked out from session logs. Your exact limits are in claude.ai → Settings → Usage.
- A running session's model can't be changed from outside. Model and effort changes apply to new sessions. Use "Continue as a copy with…" to carry a conversation onto another model.
- The context window size is inferred (200k, or 1M once usage passes 200k).
