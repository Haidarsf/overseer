# Roadmap

Ideas are welcome. Open an issue to discuss, or pick something up and say so in the issue.

## 0.2 (this release)

- [x] Cross-platform app (Windows, macOS, Linux) on .NET 8 and Avalonia
- [x] Live events through hooks and exact limits through the status line
- [x] Tray icon, notifications, mini mode, redact mode, light theme
- [x] Saved index and file watching for instant startup and updates
- [x] Presets, alerts, reports with CSV export, conversation search and reader
- [x] Editors for permission rules, hooks, CLAUDE.md, agents and commands; MCP add and remove
- [x] Graceful stop, accessible controls, demo mode
- [x] Tests, CI on three platforms, release workflow with checksums

## Next

- [ ] Code signing for Windows and notarization for macOS (needs certificates)
- [ ] Submit to winget, Scoop and Homebrew (manifests are in `packaging/`)
- [ ] Optional update check (off by default, asked on first run)
- [ ] Project-level settings (`.claude/settings.json` and `settings.local.json`) alongside user settings
- [ ] Per-session cost alerts and a monthly budget view
- [ ] Diff view for files a session changed (Claude Code's file history)
- [ ] Global keyboard shortcut to show the mini window
- [ ] Localisation

## Later

- [ ] OpenTelemetry receiver as another data source for teams already exporting Claude Code metrics
- [ ] A stable extension API with a small gallery
- [ ] Comparing sessions: tokens, time and tools for the same task on different models or efforts

## Won't do

- Anything that weakens Claude Code's safety settings, such as a one-click "bypass permissions" switch
- Sending your data anywhere
