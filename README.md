# Auto Remote

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Build](https://github.com/Markwardt-Labs/Auto-Remote/actions/workflows/build.yml/badge.svg)](https://github.com/Markwardt-Labs/Auto-Remote/actions/workflows/build.yml)
![Coverage](.github/badges/badge_linecoverage.svg)

Auto Remote is a command line server that hosts Claude Code or OpenAI Codex workspaces on your computer and an Avalonia Android client, an Avalonia desktop client and a single-file browser client (`Client/Web/index.html`) for using them remotely. The server needs the `claude` (Claude Code) and/or `codex` (OpenAI Codex) command installed, for the services you use, and `git` for the git features (status, changes, history, diffs, commit and clone), which run on the server. Built on .NET 10 and [Avalonia](https://avaloniaui.net/); see [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) for redistributed dependency licenses.

## Requirements

- Windows 10/11, 64-bit, or Linux, x86-64 (glibc-based distro). No separate .NET runtime install needed: the server executable is self-contained.

## Installing

Download the server executable for your platform from the latest [Release](https://github.com/Markwardt-Labs/Auto-Remote/releases):

- **Windows** - download `Markwardt.AutoRemote.Server.exe` and run it.
- **Linux** - download `Markwardt.AutoRemote.Server`, mark it executable (`chmod +x Markwardt.AutoRemote.Server`), and run it.

## Documentation

| File | Covers |
| --- | --- |
| [Docs/Api.md](Docs/Api.md) | Shape and flow of the shared `Core` surface |
| [Docs/Usage.md](Docs/Usage.md) | Runnable usage examples |
| [Docs/Architecture.md](Docs/Architecture.md) | High-level design decisions |
| [Docs/Project.md](Docs/Project.md) | Repository tooling: scripts, publishing, CI |
| [Docs/Components/](Docs/Components) | One file per complex internal component |
