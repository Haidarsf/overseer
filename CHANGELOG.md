# Changelog

All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses [semantic versioning](https://semver.org/).

## [Unreleased]

### Added
- Usage history: Overseer keeps a copy of your token usage in `~/.overseer/usage-history.json`, so Reports and the usage dials still count conversations after Claude Code deletes them (after 30 days by default). It holds numbers, model names, project folders and session titles, never the conversations. Settings → Privacy and backups → Delete usage history removes it.
- Reports says when your usage history starts, so equal totals for 30 days, 90 days and All time make sense.

## [0.3.2] - 2026-11-25

### Changed
- The plan usage note says why exact limits aren't showing yet: the status line is off, it's on but no terminal session has run it (the Claude desktop app and IDE extensions don't run status lines), or it's running but Claude Code hasn't reported limits yet. Settings → Live data says the same.

## [0.3.1] - 2026-11-25

### Added
- Check for updates in Settings → Updates (and Help → Check for updates). Installed Windows copies can download the new installer, verify its SHA-256 checksum and install it quietly, then restart; other copies open the download page. An optional daily check at startup is off by default.
- The Windows installer accepts `/RELAUNCH` to start Overseer again after a quiet install.

### Fixed
- Overseer 0.3.0 closed right after starting when two session files shared one session id (which resuming or copying a session can cause). The usage dial and reports now use the most recent of them.
- Errors while showing a page no longer close the app: they're written to `~/.overseer/errors.log` and shown as a message with an "Open log" button.

## [0.3.0] - 2026-11-25

### Added
- Answer permission prompts from Overseer: allow once, always allow (this project or all projects, with "Remove rule" to undo) or deny with a note for Claude. Works from the overview, the "waiting for you" bell, the mini window and the keyboard (A / D). Uses an optional synchronous PermissionRequest hook (`Overseer permission`) that only waits while Overseer is open and falls back to the terminal.
- Command palette (Ctrl+K), menu bar, keyboard shortcuts, and a new-session dialog that starts from your own defaults.
- Plan usage dial with a 5 hours / this week switch and a "used by" breakdown per session.

### Changed
- Redesigned as a native-feeling desktop window in Claude's colours: own title bar with menus and the bell, sidebar, toolbar, status bar, flat panes, light and dark themes (following the system by default), and a new icon.
- Settings (formerly Control and Configure) is one preferences window with sections; wording explains what applies to new sessions versus running ones.
- "Redact mode" is now "Hide private info" and also hides session titles, commands and project names.
- Toasts appear top right and offer Undo for settings changes.

### Fixed
- Stopping a session shows "Stopping…" until it has stopped.
- "Hide done sessions" applies everywhere, not only to the sidebar.

## [0.2.0] - 2026-11-24

### Added
- Cross-platform rewrite on .NET 8 and Avalonia (Windows, macOS, Linux).
- Live events through optional async hooks (`Overseer hook`) and exact context and plan limits through an optional status line (`Overseer statusline`).
- Tray icon that shows idle, working or needs-you; desktop notifications; mini mode; redact mode; light theme.
- Saved session index and file watching, so startup is instant and updates arrive within about a second.
- Presets, alerts (context, plan limits, repeated errors, permission prompts, finished turns), reports with CSV export and an editable price table, conversation search and reader.
- Configure page: permission rules, hooks, CLAUDE.md editor, agents and slash commands.
- MCP servers: add (stdio, HTTP, SSE) as well as check and remove.
- Subagent and files-changed panels on the session page.
- Graceful stop (interrupt first, force only if needed).
- `--demo` mode with made-up data.
- Windows installer (`Overseer-Setup-<version>-x64.exe`): per-user install, Start menu and optional desktop and startup shortcuts; uninstalling removes Overseer's hooks and status line from Claude Code settings.
- Experimental extension API.
- Unit tests, CI on three platforms, release workflow with checksums, packaging templates.

### Changed
- Renamed from "Claude Monitor" to Overseer, with a new icon and colours, to make clear it's an independent project.

## [0.1.0] - 2026-11-24

### Added
- Windows-only prototype (WPF): session view, usage overview, control tab for model, effort, permission mode, skills and plugins.
