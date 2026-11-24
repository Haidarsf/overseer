# Privacy

Overseer is a local desktop app. This page lists exactly what it reads, writes and sends.

## What it reads

| Path | Why |
| --- | --- |
| `~/.claude/projects/**.jsonl` | Session transcripts: model, token usage, tool calls, prompts and replies (for the timeline, search and conversation view) |
| `~/.claude/sessions/*.json` | Which sessions are open and whether they're busy |
| `~/.claude/settings.json` | Current settings, shown in Settings |
| `~/.claude/skills`, `agents`, `commands`, `CLAUDE.md` | Lists and editors in Settings |
| `~/.overseer/` | Its own data (below) |
| Project `CLAUDE.md` files | Only for folders your sessions ran in, and only when you open them in Settings → Rules, hooks and files |

`~/.claude` means `$CLAUDE_CONFIG_DIR` if you've set it.

It **never** reads `~/.claude/.credentials.json`, keychains, API keys or tokens.

## What it writes

| Path | When |
| --- | --- |
| `~/.overseer/cache/index.json` | A summary of your sessions (titles, folders, token counts, recent activity), so the app starts instantly. Delete it at any time. |
| `~/.overseer/events/*.jsonl` | Only if you turn on **Live events**: one line per hook event. Large tool outputs are dropped and long inputs are cut to 400 characters. Files are trimmed when they pass 2 MB. |
| `~/.overseer/statusline/*.json` | Only if you turn on the **Status line**: the latest status JSON per session. |
| `~/.overseer/pending`, `answers`, `app.alive` | Only if you turn on **Answer permission prompts here**: each waiting request (tool name and input, as Claude Code sends it), your answer, and a heartbeat so the hook knows the app is open. Files are removed once answered or timed out. |
| `~/.overseer/config.json`, `presets.json`, `prices.json` | Overseer's own settings |
| `~/.overseer/trash/` | Agents and commands you delete |
| `~/.claude/settings.json` | Only when you change something in Overseer. A backup is kept as `settings.json.overseer-backup`. |
| `CLAUDE.md`, skills, agents, commands | Only when you create or save them. Saving a `CLAUDE.md` keeps the previous version as `.overseer-backup`. |

`~/.overseer` means `$OVERSEER_HOME` if you've set it.

## What it sends

Nothing. Overseer has no telemetry, analytics, crash reporting or update checks, and makes no network requests of its own.

Some actions run the official `claude` command, which may use the network: listing or installing plugins, and checking, adding or removing MCP servers. They only run when you click them.

Desktop notifications go through your operating system's notification service. On Windows they're shown via PowerShell. Notification text includes the session title.

## Hide private info

**Hide private info** in the toolbar (Ctrl+Shift+H) replaces session titles with "Session 3f2a", hides your prompts, commands, project names and emails, shortens file paths and blurs folder pickers, so you can share your screen or take screenshots.

## Removing Overseer

1. Turn off the switches in Settings → Live data (or run `Overseer --remove-integration`; the Windows uninstaller does this for you). This removes exactly what Overseer added to `settings.json`.
2. Quit Overseer and delete the app.
3. Delete `~/.overseer`.
