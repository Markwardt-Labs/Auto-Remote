# Server config

The server reads one JSON file at startup (the path passed as the first argument, `config.json` by default). Names are case-insensitive. A missing file, invalid JSON or a value that fails validation is printed and the server exits with code 1.

| Setting | Default | Description |
| --- | --- | --- |
| `key` | required | Key a client must send to connect; must not be empty. |
| `port` | `52679` | The one port every client connects to, over secure WebSocket; 1 to 65535. |
| `dataFolder` | `AutoRemote` folder in the server user's application data | Folder holding all data the server stores; created when missing. |
| `defaultFolder` | the server user's home folder | Existing folder where browsing for a new workspace's folder starts. |
| `claudeCommand` | `claude` | Claude Code executable run for prompts; must resolve on the server's machine. |
| `claudeArguments` | `[]` | Extra arguments added to every Claude Code run. |
| `codexCommand` | `codex` | Codex executable run for prompts; must resolve on the server's machine. |
| `codexArguments` | `[]` | Extra arguments added to every Codex run. |
| `dotnetCommand` | `dotnet` | .NET executable that runs scripts and tasks with `run --file`; only needed for scripts. |
| `certificateFile` | none | PFX certificate presented to clients. |
| `certificatePassword` | none | Password of the PFX certificate, if it has one. |

## Data location

The server stores all its data in `dataFolder`, by default the `AutoRemote` folder of the server user's application data (`~/.local/share/AutoRemote` on Linux) rather than the working directory: `workspaces.json` (workspaces, groups and settings), the per-workspace conversations in a `history` folder, and the self-signed `certificate.pfx` created on first start when `certificateFile` is not set.

## Fixed behavior

Prompts always run with `--dangerously-skip-permissions` (Claude Code) or `--dangerously-bypass-approvals-and-sandbox` (Codex), since they run unattended and cannot ask, and always do their work in the foreground (see Workspaces); `claudeArguments` and `codexArguments` come after them. Which service a workspace uses is a workspace setting, not a server setting; the executables are only needed for the services in use.
