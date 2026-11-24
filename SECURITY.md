# Security policy

## Reporting a vulnerability

Please **don't** open a public issue. Use GitHub's private reporting instead:
**Security → Report a vulnerability** on this repository.

Include what you found, how to reproduce it, and what an attacker could do with it. You'll get a reply within a week, and a fix or a plan as soon as possible. You'll be credited in the release notes unless you'd rather not be.

## Scope

Overseer runs on your machine with your permissions, reads Claude Code's files and can change Claude Code's settings. Issues we especially want to hear about:

- anything that lets a file, transcript, session title, plugin listing or MCP output run code or commands
- anything that writes to `settings.json` in a way the user didn't ask for, or corrupts it
- the `hook` and `statusline` commands affecting Claude Code's behaviour (they must stay passive)
- reading or leaking credentials or other private data

## Supported versions

Only the latest release gets security fixes.

## Extensions

Extensions (DLLs in `~/.overseer/plugins`) are off by default and run with your full permissions. Treat them like any program you download, and only install ones you trust.
