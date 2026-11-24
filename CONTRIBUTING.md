# Contributing to Overseer

Thanks for helping. Bug reports, ideas, docs fixes and code are all welcome.

## Before you start

- **Bugs**: open an issue with your OS, Overseer version (`Overseer --version`), Claude Code version (`claude --version`) and what you expected. If it's about reading sessions, a few redacted transcript lines help a lot.
- **Bigger changes**: open an issue first so we can agree on the approach. Check the [roadmap](ROADMAP.md) for what's planned.
- **Security issues**: please don't open a public issue. See [SECURITY.md](SECURITY.md).

## Setup

1. Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
2. `dotnet test` runs the tests.
3. `dotnet run --project src/Overseer.App -- --demo` runs the app on made-up data, so you don't need Claude Code installed and your own sessions stay private.

Any editor works. Rider, Visual Studio and VS Code with the C# Dev Kit and the Avalonia extension give you XAML previews.

## Where things live

- `src/Overseer.Core`: all logic, no UI. **Put new behaviour here and test it.**
- `src/Overseer.App/ViewModels`: state and commands for each page (CommunityToolkit.Mvvm).
- `src/Overseer.App/Views`: XAML. Keep logic out of code-behind.
- `src/Overseer.App/Styles`: colours (dark and light), icons, shared templates.
- `tests/Overseer.Core.Tests`: xUnit tests and fixture transcripts.

[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) explains the data sources and how they fit together.

## Ground rules

These keep Overseer trustworthy, so pull requests that break them won't be merged:

1. **Only documented settings.** Write to Claude Code's config only through keys documented at [code.claude.com/docs](https://code.claude.com/docs), and only through `SettingsFile`, which keeps other keys, backs up the file and refuses invalid JSON. Prefer the official `claude` CLI where one exists.
2. **No network calls** from Overseer itself, and no telemetry.
3. **Never read credentials** (`.credentials.json`, keychains, tokens).
4. **Ask before anything destructive or outward-facing** (use `ConfirmAsync`), and prefer reversible actions such as moving to trash.
5. **Pass arguments as lists** (`ProcessStartInfo.ArgumentList`), never by building shell strings from user data.
6. **Accessible controls.** Use real buttons, toggles and list boxes, and give icon-only controls an `AutomationProperties.Name`.
7. **Plain language** in the UI: say what happens, not how it's implemented.

## Pull requests

- One topic per pull request, with a short description of what changed and why.
- Add or update tests for anything in `Overseer.Core`.
- Include a screenshot for visible UI changes (use `--demo` so no personal data shows).
- CI builds and tests on Windows, macOS and Linux; please make sure it's green.

## Releasing (maintainers)

1. Update `Version` in `Directory.Build.props` and `CHANGELOG.md`.
2. Tag `vX.Y.Z` and push the tag. The release workflow builds every platform and creates a **draft** GitHub release with checksums.
3. Check the draft, then publish it.

By contributing you agree that your contributions are licensed under the [MIT License](LICENSE), and to follow the [Code of Conduct](CODE_OF_CONDUCT.md).
