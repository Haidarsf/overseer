# Architecture

## Data sources

Overseer combines four sources, from least to most precise. Each feature uses the best one available and says which one it used.

| Source | Where | Gives | Notes |
| --- | --- | --- | --- |
| Transcripts | `~/.claude/projects/<project>/<session>.jsonl`, plus `<session>/subagents/*.jsonl` | Model, token usage per call, tool calls, prompts, replies, errors, skills offered | Internal format; parsed defensively by `TranscriptParser` |
| Live session files | `~/.claude/sessions/<pid>.json` | Which sessions are open, busy or idle, their names | Checked against running processes |
| Hooks (optional) | `~/.overseer/events/<session>.jsonl`, written by `Overseer hook` | The exact tool running, permission prompts, stops, session start and end | [Documented feature](https://code.claude.com/docs/en/hooks); registered async so it can't slow Claude down |
| Status line (optional) | `~/.overseer/statusline/<session>.json`, written by `Overseer statusline` | Exact context use and window size, cost, **plan limits** (5-hour and 7-day percentages and reset times) | [Documented feature](https://code.claude.com/docs/en/statusline); plan limits appear for Pro and Max plans after the first reply |
| Permission hook (optional) | `~/.overseer/pending/<id>.json` and `answers/<id>.json`, exchanged with `Overseer permission` | Permission prompts you answer in the app | Synchronous [PermissionRequest hook](https://code.claude.com/docs/en/hooks). It only waits while the app is running (heartbeat file `~/.overseer/app.alive`) and gives up after the configured time, so Claude Code then asks in the terminal |

## Flow

```
Claude Code ──writes──▶ transcripts, live session files
     │
     ├─ hook ──────────▶ Overseer hook ───────▶ ~/.overseer/events
     ├─ status line ───▶ Overseer statusline ─▶ ~/.overseer/statusline
     └─ permission ────▶ Overseer permission ─▶ ~/.overseer/pending  ◀── answers ── app
                                     │
              FileSystemWatcher + 15 s fallback scan
                                     ▼
                            SessionStore (Core)
          incremental reads · saved index · live state · alerts
                                     │  Changed event
                                     ▼
                     MainViewModel ─▶ page view models ─▶ XAML views
                                     │
                     Notifier · tray icon · mini window
```

## Overseer.Core

- **`SessionStore`** keeps one `Session` per transcript. It reads only the bytes appended since the last pass (complete lines only), saves an index to `~/.overseer/cache/index.json` so restarts don't re-read gigabytes, and watches the folders so updates arrive within about a second. A slower full pass every 15 seconds covers anything a watcher misses.
- **`UsageHistory`** copies each transcript's usage records to `~/.overseer/usage-history.json` whenever the index is saved. While a transcript exists its entry is refreshed from it; once Claude Code deletes it (`cleanupPeriodDays`, 30 days by default), the last copy stays and `SessionStore.AllUsage()` adds it back, so reports keep the full history. Keys are transcript paths, so nothing is counted twice.
- **`TranscriptParser`** turns one JSONL line into updates. Usage is counted once per message id (streaming writes the same message several times) while keeping the final output count. Unknown or malformed lines are skipped.
- **`PermissionBridge`** (`Permissions.cs`) is the `permission` entry point and the app side of it: pending requests, answers, the decision JSON Claude Code expects (allow, deny with a message, or allow with an `addRules` update saved to the place you pick) and removing a rule again.
- **`Updater`** (`Updates.cs`) asks GitHub for the latest published release, compares versions, picks the Windows installer for this machine (only from this repository's release downloads), checks it against GitHub's SHA-256 digest or the `.sha256` file next to it, and runs it with `/SILENT /RELAUNCH`. It only installs itself when `unins000.exe` sits next to the app, i.e. the copy came from the installer.
- **`Integration`** contains the `hook` and `statusline` entry points (which must never throw, block or print anything that changes Claude's behaviour) and installs and uninstalls them in `settings.json`.
- **`SettingsFile`** is the only code that edits `settings.json`. It re-reads before each change, keeps every other key and their order, makes a one-time backup, writes atomically, and refuses invalid JSON.
- **`ClaudeCli`, `Terminal`, `ProcessControl`, `Notifier`** handle the operating-system work. Every process gets its arguments as a list. Notification text travels as data (environment variables or `argv`), never as script.
- **`Features.cs`** holds presets, the price table and reports, alerts, search and conversation reading, redaction, and file helpers for skills, agents and commands.
- **`Demo`** writes a made-up Claude folder for `--demo`.

## Overseer.App

- Avalonia 11 with the Fluent theme, compiled bindings, and MVVM via CommunityToolkit.Mvvm.
- `MainViewModel` owns the store, refreshes on `Changed` and every 5 seconds (for "x ago" labels), keeps the session list in sync without rebuilding it, detects finished turns for notifications, and runs the `AlertEngine`.
- The window draws its own title bar (name, menus, the "waiting for you" bell) where the OS supports it and keeps the system window buttons. `MainViewModel` also owns the command palette, toasts with Undo, the new-session dialog and the pending permission requests (a `FileSystemWatcher` on `~/.overseer/pending`).
- One view model per page: `OverviewViewModel` (sessions table, activity, plan usage dial), `SessionDetailViewModel`, `ReportsViewModel`, `SearchViewModel`, and for Settings `ControlViewModel` plus `ConfigureViewModel`.
- Colours are theme resources (`Styles/Colors.axaml`) with light and dark variants of Claude's palette; `Styles/Theme.axaml` keeps controls flat with 1 px lines and small radii.
- Option pickers are `ListBox`es styled as segmented controls, so they work with the keyboard and screen readers.
- The same executable is the GUI and the hook and status-line commands, so there's nothing extra to install.

## Known limits

- The transcript format is internal to Claude Code and may change. Tests in `tests/Overseer.Core.Tests` pin the parts Overseer relies on.
- Without the status line, the context window size is inferred (200k, or 1M once usage passes 200k), and plan limits aren't available.
- The docs don't say whether hooks and the status line run in the Claude desktop app's Code tab; the transcripts and live session files work everywhere.
- Overseer can't type into a running terminal, so replies to Claude still happen there.
- A running session's model can't be changed from outside. Overseer changes defaults, starts new sessions, or continues a conversation as a copy (`--fork-session`) on another model.
