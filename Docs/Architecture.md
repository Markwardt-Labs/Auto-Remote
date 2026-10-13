# Architecture

This document explains the high-level design decisions behind Auto Remote: why it is built the way it is, not the class-by-class mechanics.

## Core, Server, Frontend, two client heads and a web page

`Core` holds everything the server and the clients must agree on, so no app references another. The alternative, having a client depend on the server project, was rejected because it would drag server-only code into the client builds. The trade-off is that shared code must stay platform neutral.

## Server and desktop client are self-contained single-file executables

The server and the desktop client (Windows and Linux, x64) are each distributed as one executable per platform with the runtime bundled, so users install nothing else. The trade-off is a larger download than a framework-dependent build.

## Clients share one Avalonia frontend

The Android and desktop clients draw the same UI with Avalonia, so everything visual and all client logic (view models, views, theme, connection and settings services, the markdown renderer) lives in `Client/Frontend/`, a library both reference. `Client/Mobile/` and `Client/Desktop/` are only heads: the Android activity hosting the root view as a single view, and a desktop window hosting it with a plain `Main`. Both connect over secure WebSocket on the same port as the web client. `Client/Mobile/` needs the .NET Android workload to build; keeping the shared code in a plain `net10.0` library is also what lets the headless Avalonia tests reference it, since they cannot load an Android assembly. A head adds nothing of its own to behavior, which is what keeps the clients identical.

## The web client is one static file

`Client/Web/index.html` is the whole browser frontend: HTML, CSS and JavaScript with no build step, no dependencies and no serving code, so it can be dropped on any static host (or opened from disk). It mirrors the Avalonia clients' screens and behavior and connects straight to the server, over secure WebSocket (`wss`), the same transport the Avalonia clients use (see the protocol document). It is hand-written JavaScript rather than a second Avalonia target because a single file with nothing to build is what lets it be hosted anywhere; the trade-off is that it is not covered by the .NET tests and has to be kept in step with the Avalonia clients by hand.

## Access key handshake before any traffic

Clients authenticate with a single shared access key sent as the first line on the connection, before the server accepts anything else. A per-user account system was rejected as unneeded for a single-owner server.

## Always `wss`, on one port

Every connection is encrypted, so the key never travels in plaintext and there is no insecure mode to misconfigure. Every client, Avalonia and browser alike, speaks the line protocol as WebSocket messages over TLS on one port. A browser cannot open raw sockets, and a single transport means one port to forward and a connection that passes through anything that proxies WebSockets, such as a Cloudflare tunnel, which cannot carry raw TCP to a client that is not running its own agent. The server creates a self-signed certificate on first start (or uses a configured PFX), since a single-owner server usually has no certificate authority behind it. Without a CA the client cannot validate the certificate, so the Avalonia clients accept any certificate, trading protection against a man in the middle for needing no setup and never breaking when the server's certificate is replaced. A browser cannot skip validation, so it must accept the certificate once by opening the port in a tab (not needed behind a proxy that presents a real certificate).

## The server runs Claude Code or Codex, the client is a remote control

The server hosts workspaces and runs the local `claude` or `codex` executable, as chosen per workspace, so the chat is a session rather than strict back and forth: a prompt sent while the AI is working joins that work instead of waiting for it. Claude Code is a live process per workspace that takes prompts on its input stream; Codex takes one prompt per process, so the server queues prompts and runs them one after another in the same Codex session, which looks the same to the client. Idle processes are ended after a while and on rename, delete and settings changes, and the next prompt resumes the AI session in a fresh process, so the server holds no state it cannot rebuild from its stored workspaces and history. The cost is a long-lived process per active workspace. The server also reads the usage limits of each service itself (Claude Code's `/usage` report, Codex's session records) and caches them for a minute, so every client shows the same numbers without each running the services.

## The server pushes what changes

Replies, the working state and file system changes reach the client as events on the same connection instead of being polled or held inside a long request. Events go to every connected client, which is what keeps several clients showing the same live conversation, and carry enough position information (message index) for a client to join mid-stream without duplicating or losing messages.

## Git runs on the server only

The server is the only place git runs: clients send commands such as `GetGitStatus` or `GitCommit` and draw what comes back, so a client needs neither git nor credentials and every client sees the same repository. Git is driven through its command line with an argument list and no shell, non-interactively (no prompts, ssh in batch mode), and a folder counts as a repository only when it is the repository's root, so a workspace inside a larger repository is not shown as one. The server computes diffs from git's own output and sends them as marked lines, so the clients need no diff algorithm. The AI and the user both change repositories in ways the server cannot watch cheaply, so the server announces its own git actions with a `GitChanged` event and clients also read the state again after file changes, after the AI stops working and after their own actions. Git actions are refused unless the workspace is Ready so they cannot race the AI's edits or a script, and the server lets only one git action run per workspace. Squash, rebase and merge are workflows rather than single commands, so they live in their own server component that pushes after rewriting history, undoes everything when cancelled or crashed, and hands merge conflicts to the workspace's own AI instead of asking the user to edit conflict markers by hand.

## Client is dark only and view-model driven

The client forces the dark theme and takes its look from the Auto Dev app: VS Code style dark surfaces separated by thin borders, a blue accent, small corner radii, a drawer listing workspaces as a tree of groups, a header that turns blue while the AI works or a script runs and a gear at the top right that opens the workspace's settings. A focused workspace is laid out for the device: the mobile client, for narrow touch screens, shows chat, files and scripts one at a time with equal tabs along the bottom, while the desktop and web clients have no bottom tabs and show the file system, the content of the selected file and the chat side by side with resizable separators, the file system much narrower (a seventh of the width) and the other two equal at first. The desktop client alone also has what only makes sense on a computer: a window without a title bar of its own, whose header carries the window controls, and buttons for the workspace folder in the file manager. These are the deliberate differences between the clients. Screens are plain Avalonia views bound to view models with no MVVM framework; the root view model decides which screen shows and owns the single server connection. Saved connection settings let the app reconnect on launch and only fall back to the host, port and access key screen when none are saved or the server cannot be reached.

## Workspaces are folders, AI sessions are attached to them

What the user manages is a workspace: a folder on the server. The AI conversation is a separate AI session of the workspace's service that the server attaches to a workspace on demand and replaces silently when the service no longer has it, so a renamed folder or cleaned up data never leaves a workspace unusable. The groups the client shows in its drawer, which can contain other groups, are only a server-side grouping of workspaces, not a mirror of the file system. Everything about workspaces, groups, settings and history is stored on the server and the client keeps only how to reach the server, so any client sees the same state.

## All clients stay identical

The mobile, desktop and web clients are one product in three shells: a change to a screen, behavior or style lands in all of them together. Sharing `Client/Frontend/` makes that automatic for the two Avalonia clients (the git screens included); the web client is mirrored by hand. Files whose name ends in `.md` or `.markdown` are previewed rendered as markdown (headings, emphasis, lists and task lists, quotes, code, tables, rules) rather than as raw text. The Avalonia clients render it as native controls from a markdown parser, and the web client with a small built-in renderer since it has no dependencies; links are shown styled but are not followed, and images appear as their alt text. Other text files get syntax highlighting from a small built-in tokenizer rather than a grammar engine,, so the clients stay dependency free and identical.

## Scripts run on the server, one thing at a time

A workspace's files live on the server, so its `.cs` scripts and `.task` files run there too: a client asks for a run and follows the output the server pushes, and the server keeps the runs so any client sees them. The AI, git actions and scripts all change the same working tree, so the server enforces that a workspace is in exactly one state (Ready, Working, Running or Managing) and that work only starts from Ready, instead of trusting each client to disable its buttons; the cost is that a script cannot be started while the AI works, a git action cannot run during either, and the AI cannot be prompted while a script or a git action runs.
