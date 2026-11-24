# Changelog

All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses [semantic versioning](https://semver.org/).

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
- Experimental extension API.
- Unit tests, CI on three platforms, release workflow with checksums, packaging templates.

### Changed
- Renamed from "Claude Monitor" to Overseer, with a new icon and colours, to make clear it's an independent project.

## [0.1.0] - 2026-11-24

### Added
- Windows-only prototype (WPF): session view, usage overview, control tab for model, effort, permission mode, skills and plugins.
