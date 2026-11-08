# Protocol

How the client and server talk after the WebSocket connection is up.

## Transport

Every connection is a WebSocket over TLS (`wss`) on the single `port`; there is no plaintext mode and no other transport, so the desktop, mobile and web clients all connect the same way, and a reverse proxy or tunnel that forwards WebSockets (such as a Cloudflare tunnel) can front the one port. Every message is one line of this protocol (a trailing newline is optional when sending) and each line the server sends is one text message, so everything below applies unchanged. An HTTPS request that is not a WebSocket upgrade is answered with a short text page, which lets a browser be pointed at the port once to accept a self-signed certificate. No origin check is made because the access key is the authentication. Both ends send WebSocket pings every 20 to 30 seconds when idle, which keeps proxies from closing an idle connection; the Avalonia clients also drop a connection whose pings go unanswered for 20 seconds, so a connection that died silently (a phone leaving the network or sleeping) fails promptly instead of hanging. A client whose connection was lost opens a new one, with the same key handshake, on its next request and re-sends its last `WatchWorkspace`, since the server forgets a connection's watch with it; events pushed while it was away are not replayed.

The server presents one certificate: the PFX named by `certificateFile`, or a self-signed one it creates on first start as `certificate.pfx` in the data folder (private key, readable by its owner only) and logs the SHA-256 fingerprint of at startup. The Avalonia clients accept whatever certificate the server presents without validating or pinning it, so the connection is encrypted but the server is not authenticated by its certificate; the access key is the only authentication.

## Framing

Every line in both directions, including the handshake, is one JSON object, camelCase, nulls omitted. `IMessageChannel` owns this framing so server and client cannot drift apart; the handshake itself reads byte by byte so no buffered reader can swallow bytes meant for the channel that follows.

## Handshake

The client opens the WebSocket connection and sends one JSON line (at most 1024 bytes, within 5 seconds), then the server replies with one JSON line. On acceptance the connection carries requests and responses; on rejection the server closes it. The key is compared in constant time. A line that is not valid JSON, or has no key, is a rejection.

| Direction | Property | Type | Meaning |
| --- | --- | --- | --- |
| Client to server | `accessKey` | string | The access key the client was configured with. |
| Server to client | `accepted` | bool | `true` if the key was correct. |
| Server to client | `version` | string | The server's version (`major.minor.patch`), sent only with an acceptance and left out by a server that predates version checks. The Avalonia clients compare it with their own on every connect and reconnect. |

```json
{"accessKey": "change-me"}
{"accepted": true, "version": "1.2.3"}
```

## Message formats

Every message is one line of JSON. Property names are camelCase, enums are strings, GUIDs are lowercase hyphenated strings, and properties that are `null` are omitted. Unknown properties are ignored.

### Request

```json
{"id": 7, "command": "Prompt", "workspaceId": "0b9c...", "text": "Fix the build"}
```

| Property | Type | Required | Meaning |
| --- | --- | --- | --- |
| `id` | number | always | Client-chosen number echoed in the response. |
| `command` | string | always | The command name. |
| `workspaceId` | GUID | per command | The workspace targeted. |
| `groupId` | GUID | per command | The group targeted; for `MoveWorkspace`, omitted means no group; for `CreateWorkspace` and `CloneWorkspace`, the group the new workspace is filed in (omitted: the top level), refused when it no longer exists. |
| `parentId` | GUID | no | `CreateGroup` and `MoveGroup`: the group to put the group in; omitted means the top level. |
| `before` | string | no | `GetHistory` only: read the messages before this cursor (the `older` of an earlier chunk); omitted means the newest. |
| `limit` | number | no | `GetHistory`: the most messages to read; 50 when omitted, at most 200. `GetGitHistory`: the most commits to read; 30 when omitted, at most 200. |
| `offset` | number | `ShiftGroup` | How many places to move: negative is up, positive is down. |
| `skip` | number | no | `GetGitHistory` only: how many of the newest commits to skip; zero when omitted. |
| `service` | string | `CreateWorkspace`, `CloneWorkspace`, `GetUsage` | `ClaudeCode`, `Codex` or `Local` (the models of a server on the server's machine such as Ollama, run through OpenCode); the provider a new workspace runs on for good, which is the default of the environment when omitted. `OpenCode` is accepted as `Local`. |
| `apiKey` | string | no | `CreateWorkspace`, `CloneWorkspace`: an API key for just the new workspace's AI session (Claude Code and Codex), which Live AI stores with the session; the server keeps no copy. Refused for `Local`. |
| `settings` | workspace settings | `SetWorkspaceSettings` | How the workspace's AI session is run. |
| `aiEnvironment` | ai environment | `SetAiEnvironment` | The environment of the server's AI. |
| `loop` | loop settings | `StartLoop` | What the loop sends, how often and when it ends. |
| `name` | string | per command | A folder name, a group name, a branch or tag name, the name of the folder `CloneWorkspace` creates, or the file name for `UploadAttachment`. |
| `data` | string | `UploadAttachment`, `Transcribe` | The file content or the WAV recording, base64 encoded. |
| `path` | string | per command | A folder or file path; for `CloneWorkspace` the existing folder to clone into; for the script commands the `.cs` or `.task` file. |
| `environment` | object, optional | `RunScript` | The client's `DISPLAY`, `WAYLAND_DISPLAY`, `XAUTHORITY` and `XDG_SESSION_TYPE`, when it has them. |
| `url` | string | `CloneWorkspace`, `GitSetRemote`, `ListLocalModels`, `ListImageModels` | A git repository URL; for the two list commands the optional address of the model server or image server to ask, the one of the environment when omitted. |
| `commit` | string | per command | A commit hash, 4 to 64 hexadecimal characters. |
| `base` | string | per command | The branch to compare with, rebase onto, squash against or merge into. |
| `email` | string | `GitSetIdentity` | The git email. |
| `force` | bool | no | `GitCheckout` only: discard changes that are not committed first; omitted means false. |
| `text` | string | `Prompt`, `GitCommit`, `SendScriptInput`, `SearchFiles` | The prompt text, the commit message, the line written to a script, or the text to search for. |
| `deleteFolder` | bool | no | `DeleteWorkspace` only; defaults to `true` when omitted. |
| `showHidden` | bool | no | `ListFiles` and `SearchFiles`; includes the entries a `.filehide` file hides. |
| `searchContent` | bool | no | `SearchFiles` only; searches the contents of the files instead of their names. |

Properties a command does not use are omitted.

### Response

```json
{"id": 7, "history": {"messages": [{"role": "Assistant", "text": "Done."}], "older": "48213:0"}}
```

Exactly one payload property is set on success (none for `DeleteWorkspace`, `DeleteGroup`, `Prompt`, `WatchWorkspace` and the git actions: every `Git...` command except the `GetGit...` reads); on failure only `error` is set (see Errors). A response with `id` zero answers no request and carries `event` instead (see Events).

| Property | Type | Set for |
| --- | --- | --- |
| `id` | number | Always; the request's `id`. |
| `error` | string | Failure only. |
| `catalog` | catalog | `ListWorkspaces` |
| `workspace` | workspace | `CreateWorkspace`, `CloneWorkspace`, `RenameWorkspace`, `MoveWorkspace` |
| `group` | group | `CreateGroup`, `RenameGroup`, `MoveGroup` |
| `listing` | listing | `ListFolders`, `CreateFolder` |
| `history` | history | `GetHistory` |
| `usage` | usage | `GetUsage` |
| `settings` | workspace settings | `GetWorkspaceSettings`, `SetWorkspaceSettings` |
| `aiEnvironment` | ai environment | `GetAiEnvironment`, `SetAiEnvironment` |
| `loop` | loop, optional | `GetLoop` |
| `names` | array of string | `ListLocalModels`, `ListImageModels` |
| `files` | files | `ListFiles` |
| `searchResults` | array of search result | `SearchFiles` |
| `content` | content | `ReadFile` |
| `state` | string | `GetWorkspaceState`: the workspace's state, as in the `StateChanged` event. |
| `git` | git status | `GetGitStatus` |
| `gitBranch` | branch state | `GetGitBranchState` |
| `gitChanges` | git changes | `GetGitChanges`, `GetGitCommitChanges` |
| `gitHistory` | git history | `GetGitHistory` |
| `gitDiff` | git diff | `GetGitDiff` |
| `commitMessage` | string | `GenerateCommitMessage` |
| `scripts` | script runs | `GetScripts` |
| `event` | event | Pushed by the server, with `id` zero (see Events). |

### Commands

A missing required property fails with `The request needs a <field>.` (`workspaceId` reports `a workspace` and `groupId` reports `a group`).

| Command | Request properties | Response property | Notes |
| --- | --- | --- | --- |
| `ListWorkspaces` | none | `catalog` | The groups in their custom order and the workspaces, each with when its AI session was last loaded (`lastUpdated`), read from Live AI at each call. Clients order the workspaces of a group by it, latest first. |
| `CreateWorkspace` | `path` (existing folder on the server), optional `service`, optional `apiKey`, optional `groupId` | `workspace` | The workspace runs on `service` (the environment's default provider when omitted) for good; the provider cannot be changed afterwards. The AI session is made together with the workspace, on `apiKey` when one is given. A workspace whose session cannot be made (a key for a provider that takes none, or local models without a model for every capability) refuses the request and nothing is created. |
| `RenameWorkspace` | `workspaceId`, `name` (new folder name) | `workspace` | Refused unless the workspace is Ready. |
| `MoveWorkspaceToFolder` | `workspaceId`, `path` (existing folder the workspace works in from now on) | `workspace` | Tells the workspace's AI session that the workspace is in `path` now, which is what tracks the place of a workspace; no file is moved by the server, so the folder has to exist already (the project may have been moved on disk, even from a folder that is gone). The workspace keeps its identifier, session, group and place. Refused unless the workspace is Ready, when `path` does not exist, and when it already hosts another workspace. Moving it to the folder it is in changes nothing. |
| `MoveWorkspace` | `workspaceId`, optional `groupId` | `workspace` | Omitting `groupId` takes the workspace out of any group. The group must exist. It goes to the end of the group. |
| `CreateGroup` | `name`, optional `parentId` | `group` | Names are trimmed, must be non-empty and unique ignoring case among the groups with the same parent. The parent must exist. |
| `RenameGroup` | `groupId`, `name` | `group` | Same name rules as `CreateGroup`. |
| `MoveGroup` | `groupId`, optional `parentId` | `group` | Omitting `parentId` moves it to the top level. Refused into itself or anything inside it, and when the name is taken in the new place. It goes to the end of its new parent. |
| `ShiftGroup` | `groupId`, `offset` | none | Moves the group among the groups with the same parent; it stops at the first or last place. |
| `DeleteGroup` | `groupId` | none | Its workspaces and groups are kept and move into its parent. |
| `DeleteWorkspace` | `workspaceId`, optional `deleteFolder` | none | Refused unless the workspace is Ready. |
| `ListFolders` | optional `path` (absolute; the server's `defaultFolder` when omitted) | `listing` | `listing.path` is empty when listing file system roots. |
| `CreateFolder` | `path` (the parent), `name` | `listing` | Listing of the parent, including the new folder. |
| `GetHistory` | `workspaceId`, optional `before`, `limit` | `history` | One chunk, oldest first, read from the AI service's stored conversation: the user's prompts and the AI's messages. |
| `GetUsage` | `service` | `usage` | The service's usage limits as the server last read them, at most a minute old. Clients ask once a minute for the focused workspace's service. |
| `StartLoop` | `workspaceId`, `loop` | none | Starts a loop of the AI: the prompt is sent over and over on the interval until the condition ends it or it is cancelled. The first run starts when the AI and the workspace are free. Refused unless the workspace is Ready or Working, while a loop already runs there, for settings that are not valid, and for a usage window the service does not report. |
| `CancelLoop` | `workspaceId` | none | Cancels the loop at once without stopping the run in progress, which finishes; does nothing when no loop runs. |
| `GetLoop` | `workspaceId` | `loop` | The loop that runs in the workspace, or none. |
| `GetWorkspaceSettings` | `workspaceId` | `settings` | How the workspace's AI session is run, as the session stores it (the server keeps none of its own): the provider, capability, effort, whether the shell, files outside the workspace, the web and image generation are on, the instructions, the workspace's own review and expand prompts next to the global ones, and descriptors, which are lines the session writes about how it runs (the model it chose and so on). Makes the session when the workspace has none yet. |
| `SetWorkspaceSettings` | `workspaceId`, `settings` | `settings` | Gives the session every value in `settings` (the provider, the descriptors and the global prompts in it are ignored) from its next prompt on; the session stores them. The review and expand prompts are stored with the workspace instead, and an omitted or blank one clears the override. Refused for a capability or effort the AI does not know, instructions the session refuses and combinations the provider cannot do. |
| `GetAiEnvironment` | none | `aiEnvironment` | The environment of the server's AI. API keys are never sent, only whether one is set. |
| `SetAiEnvironment` | `aiEnvironment` | `aiEnvironment` | Replaces the environment and applies it to every workspace from the next session on. A key left out stays as it is and an empty one is removed. Refused for an empty command, an address that is not http or https, a context size below one, local models for some capabilities but not all, OpenAI image generation without a key and local image generation without a checkpoint for every capability. |
| `ListLocalModels` | optional `url` | `names` | The models the local model server offers (Ollama by default). Refused when the server cannot be reached. |
| `ListImageModels` | optional `url` | `names` | The checkpoints the Stable Diffusion server offers. Refused when the server cannot be reached. |
| `Prompt` | `workspaceId`, `text` | none | Answers as soon as the server has the text, also while the AI is working, when it is steered into the work in progress and, if the AI may have missed it, asked again as a turn of its own afterwards. The message and the AI's replies arrive as events. Refused unless the workspace is Ready or Working (so while a script or a git action runs). |
| `UploadAttachment` | `workspaceId`, `name`, `data` | `attachmentPath` | Stores the file in the data folder, outside the workspace, and answers its server path (up to 2 MB). A client sends one for each attachment and then a `Prompt` with a line `Attached file: <path>` for each, so the AI can read it. Refused unless the workspace is Ready or Working. |
| `Transcribe` | `data` (a 16 kHz, mono, 16 bit WAV recording) | `transcript` | Turns the recording (up to 4 MB) into text with the server's built-in Whisper model, or its speech command when one is set (two minutes of processing at most), and answers the text, empty when nothing was heard. Refused when voice input is turned off on the server, or its model cannot be loaded; not tied to a workspace and allowed in any state. |
| `UpdateServer` | none | none | Makes the server look for the newest release on GitHub, download its server executable and restart with it, which closes every connection. Answers once the download is complete, a second before the restart. Refused when there is no newer release, GitHub cannot be reached, the server does not run from an installed executable or the folder is not writable. Not tied to a workspace and allowed in any state. |
| `ListFiles` | `workspaceId`, optional `path` (relative to the workspace folder; empty or omitted for the folder itself), optional `showHidden` | `files` | Entries matched by a `.filehide` file are left out unless `showHidden` is true. |
| `ReadFile` | `workspaceId`, `path` (relative to the workspace folder) | `content` | |
| `SearchFiles` | `workspaceId`, `text`, optional `searchContent`, optional `showHidden` | `searchResults` | Walks the workspace folder, skipping `.git`, symbolic links and (unless `showHidden`) what `.filehide` files hide. By name it matches `text` as a case insensitive subsequence of the path, ranking file name matches and runs of consecutive characters higher, and returns at most 50 files. By content it returns at most 200 lines containing `text`, ignoring case, skipping binary files and files over 1 MB. |
| `WatchWorkspace` | `workspaceId` | none | Says that this connection has the workspace open, in place of the one it had open before. The server watches the whole folder of every workspace some connection has open, however deep, and pushes what changes in it to every connection as `FilesChanged` and `GitChanged` events, also when the change was made outside Auto Remote. The watch ends with the connection, and a connection that reconnects sends it again. |
| `CloneWorkspace` | `url`, `path` (existing folder on the server), optional `name`, optional `service`, optional `apiKey`, optional `groupId` | `workspace` | The server runs `git clone` into a new folder of that name (the repository's name when omitted) inside `path` and creates a workspace in it. Refused when the folder exists; a failed clone leaves nothing behind. It answers when the clone is done, which can take minutes. |
| `GetGitStatus` | `workspaceId` | `git` | `isRepo` is false for a folder that is not the root of a repository, and when git is not installed on the server. |
| `GetGitChanges` | `workspaceId` | `gitChanges` | Paths that differ from the head commit plus the ones git ignores; empty for a folder that is not a repository. |
| `GetGitHistory` | `workspaceId`, optional `skip`, `limit` | `gitHistory` | Commits of every branch and tag, newest first; empty before the first commit. |
| `GetGitCommitChanges` | `workspaceId`, `commit` | `gitChanges` | The paths a commit changed relative to its first parent. |
| `GetGitDiff` | `workspaceId`, `path`, optional `commit` | `gitDiff` | The whole file with its changes marked: the commit against its first parent, or without `commit` the working tree against the head. |
| `GitCommit` | `workspaceId`, `text` | none | Stages everything, commits, then pushes the branch to `origin` when there is one; a failing push is ignored. Refused unless the workspace is Ready, and when there is nothing to commit. |
| `GenerateCommitMessage` | `workspaceId` | `commitMessage` | Asks the AI for a message of eight words or less for the pending changes (the changed paths and the diff of tracked files, cut at 30000 characters). Refused unless the workspace is Ready, and when there is nothing to commit. |
| `GitReset` | `workspaceId` | none | Discards every change that is not committed, new files included. Refused unless the workspace is Ready. |
| `GitCreateBranch` | `workspaceId`, `name` | none | Creates the branch at the head and checks it out. Refused unless the workspace is Ready, and for an invalid or taken name. |
| `GitCreateTag` | `workspaceId`, `name` | none | Creates an annotated tag at the head; refused unless the workspace is Ready, for an invalid or taken name or before the first commit. |
| `GitSetRemote` | `workspaceId`, `url` | none | Sets `origin`, creating the repository first when the workspace has none. Refused unless the workspace is Ready. |
| `GitCheckout` | `workspaceId`, `name`, optional `force` | none | Checks out a branch, a tag or a commit (the last two detach the head); a branch that exists only on the remote gets a local tracking branch. Without `force` it fails when changes are not committed. Refused unless the workspace is Ready. |
| `GitDeleteBranch` | `workspaceId`, `name` | none | Deletes the branch locally and, when it exists there, on the remote. The checked out branch cannot be deleted. Refused unless the workspace is Ready. |
| `GitDeleteTag` | `workspaceId`, `name` | none | Deletes the tag locally and on the remote. Refused unless the workspace is Ready. |
| `GetGitBranchState` | `workspaceId`, `name`, `base` | `gitBranch` | How many commits the branch has that `base` lacks, whether it is built on `base`, and its tip's subject. |
| `GitSquash` | `workspaceId`, `name`, `base`, `text` | none | Collapses the branch's commits that `base` lacks into one commit with message `text` without touching the working tree, then force-pushes the branch. Refused unless the workspace is Ready. |
| `GitRebase` | `workspaceId`, `name`, `base`, optional `text` | none | Rebases the branch onto `base` (it is checked out for that and the original branch is checked out again), squashing its own commits first when `text` is given, then force-pushes it. Conflicts are handed to the AI of the workspace, up to three attempts, otherwise the rebase is aborted. Needs a clean working tree. Refused unless the workspace is Ready. |
| `GitMerge` | `workspaceId`, `name` (source), `base` (target), optional `text` | none | Rebases the source onto the target when needed (squashing with `text` when given), fast-forwards the target to it, pushes the target and deletes the source locally and on the remote; the target stays checked out. Conflicts are handled as in `GitRebase`. Needs a clean working tree. Refused unless the workspace is Ready. |
| `GitFetch` | `workspaceId` | none | Fetches `origin` with tags and pruning, moves local branches that have no commits of their own to match the remote, and detaches and deletes the checked out branch when its remote counterpart was deleted. Refused unless the workspace is Ready; does nothing without a remote. |
| `GitSync` | `workspaceId` | none | `GitFetch`, then fast-forwards the checked out branch onto its remote counterpart, stashing changes that are not committed and restoring them (the AI resolves conflicts of the restore). Does nothing unless the workspace is Ready, without a remote, or when the branch has diverged. |
| `GitSetIdentity` | `workspaceId`, `name`, `email` | none | Sets the git user name and email in the server user's global git configuration. |
| `GitCancel` | `workspaceId` | none | Cancels the git action running in the workspace; that action then fails with a message saying it was cancelled and undone. While the workspace is Managing it is the same as `CancelWorkspace`. |
| `StopWorkspace` | `workspaceId` | none | Ends the work of the AI, the script or the git action at once and answers when the workspace is Ready again, keeping what the work did (a git action is undone). Refused when nothing runs or the workspace is already Stopping or Canceling. |
| `CancelWorkspace` | `workspaceId` | none | Ends the AI's work and reverts what it did, answering when the workspace is Ready again. None of the clients sends it. Only for the AI's work: refused for a script or a git action (stop those), when nothing runs, when it is already Stopping or Canceling, and in a workspace that is not a git repository. |
| `GetWorkspaceState` | `workspaceId` | `state` | Reads the state, which a client does when it opens a workspace and then follows with `StateChanged` events. |
| `GetScripts` | `workspaceId` | `scripts` | The runs the server holds for the workspace, with their output. |
| `RunScript` | `workspaceId`, `path` (a `.cs` or `.task` file) | none | Starts the run and answers once it started; progress arrives as events. Refused unless the workspace is Ready, for a path outside the workspace folder or that is missing, and for a task that lists anything but existing `.cs` files. A kept run of the same file is replaced. The `environment` is added to the environment of the scripts only when the connection comes from the server's own machine (a loopback address, or the address the server is listening on), and only the four display variables are taken from it. |
| `StopScript` | `workspaceId`, `path` | none | Kills the script, or every script of the task, and the task stops. Does nothing when the run is not in progress. |
| `SendScriptInput` | `workspaceId`, `path`, `text` | none | Writes the line to the standard input of the running script and adds it, with a newline, to the output. Refused when the script is not running. |
| `RemoveScript` | `workspaceId`, `path` | none | Forgets a finished run, and the runs of a task's scripts. Refused while it is in progress. |

### Payload objects

Nested objects are shown after the table of the object that contains them. Optional properties are omitted when null (or, for `truncated`, when false).

**workspace**

| Property | Type | Meaning |
| --- | --- | --- |
| `id` | GUID | The workspace's identifier. |
| `folder` | string | Absolute path of the workspace's folder on the server. The display name is not sent; clients take the last segment of this. |
| `sessionId` | string, optional | The live AI session that holds the workspace's folder, provider and AI configuration (32 hexadecimal digits). Informational: the server manages it and replaces it, keeping the workspace, when the session can no longer be used. |
| `groupId` | GUID, optional | The group the workspace is filed in; omitted when it is at the top level. |
| `lastUpdated` | timestamp, optional | When the workspace's AI session was last loaded, which is what workspaces are ordered by in their group (the latest first); refreshed whenever the workspaces are listed. |
| `service` | string | The provider the workspace's prompts run on, chosen when the workspace is created and never changed; `ClaudeCode`, `Codex` or `Local`. |
| `reviewPrompt`, `expandPrompt` | string, optional | The prompts of the workspace's own that its review and expand buttons send; omitted when it uses the environment's. |
| `statusMinutes` | number, optional | The status interval the workspace has of its own; omitted when it uses the environment's. |

**catalog** (`ListWorkspaces`)

| Property | Type | Meaning |
| --- | --- | --- |
| `groups` | array of group | The groups in their custom order. |
| `workspaces` | array of workspace | The workspaces in their custom order. |

**group** (also the items of `catalog.groups`)

| Property | Type | Meaning |
| --- | --- | --- |
| `id` | GUID | The group's identifier. |
| `name` | string | The group's name. Groups only organize workspaces in Auto Remote and are unrelated to folders on disk. |
| `parentId` | GUID, optional | The group it is inside; omitted at the top level. |

**listing** (`ListFolders`, `CreateFolder`)

| Property | Type | Meaning |
| --- | --- | --- |
| `path` | string | Absolute path of the folder; empty for the list of file system roots. |
| `parent` | string, optional | Absolute path of the containing folder; omitted when nothing is above. |
| `folders` | array of folder | Visible sub-folders, ordered by name. |

**folder** (item of `listing.folders`)

| Property | Type | Meaning |
| --- | --- | --- |
| `name` | string | Display name. |
| `path` | string | Absolute path on the server. |

**history** (`GetHistory`)

| Property | Type | Meaning |
| --- | --- | --- |
| `messages` | array of message | The chunk, oldest first. |
| `older` | string, optional | The cursor to send as `before` to read the messages before this chunk; omitted when there are none. |
| `working` | bool, optional | `true` while the AI is working in the workspace; omitted otherwise. |

**loop settings** (`loop` of `StartLoop`)

| Property | Type | Meaning |
| --- | --- | --- |
| `prompt` | string | The prompt every run sends. |
| `intervalSeconds` | number | Seconds from the start of one run to the start of the next; 0 runs again as soon as the last run is done. At most 86400. |
| `condition` | string | `Forever`, `UntilTime`, `ForDuration`, `Runs` or `UntilWindow`. |
| `until` | string, optional | ISO 8601 time the loop ends, within 24 days, for `UntilTime`. |
| `durationSeconds` | number, optional | Seconds the loop may run, at most 24 days, for `ForDuration`. |
| `runs` | number, optional | How many runs, at least 1, for `Runs`. |
| `window` | string, optional | The name of the usage window to watch, one `GetUsage` returns, for `UntilWindow`. |
| `percent` | number, optional | The share of the window's limit, 1 to 100, at which the loop ends, for `UntilWindow`. |

**loop** (`GetLoop`, and `loop` of `LoopChanged`)

| Property | Type | Meaning |
| --- | --- | --- |
| `settings` | loop settings | What the loop was started with. |
| `startedAt` | string | When the loop was started, ISO 8601 UTC. |
| `nextRunAt` | string, optional | The earliest time the next prompt is sent (the interval after the start of the last run), absent before the first run; it waits further while the workspace is busy. |
| `runs` | number | How many runs the loop has started. |

**workspace settings** (`GetWorkspaceSettings`, `SetWorkspaceSettings`)

| Property | Type | Meaning |
| --- | --- | --- |
| `service` | string | The provider, fixed when the workspace was created. |
| `capability` | string | `Fast`, `Balanced`, `Strong` or `Max`; the model is picked from it. |
| `effort` | string | `Low`, `Medium`, `High` or `Max`. |
| `isShellAllowed` | bool | Whether the AI may run shell commands. |
| `isSystemAccessAllowed` | bool | Whether the AI's file tools may use any path on the server's machine and not only the ones inside the workspace folder (the shell is not confined either way). |
| `isWebAllowed` | bool | Whether the AI may use the web. |
| `isImageGenerationAllowed` | bool | Whether the AI may generate images; needs an image service in the environment. |
| `instructions` | string, optional | Standing instructions for the AI. |
| `reviewPrompt`, `expandPrompt` | string, optional | The prompts the workspace's review and expand buttons send instead of the ones of the environment; omitted (or blank when setting) means the environment's. They belong to the workspace, not to the session. |
| `statusMinutes` | number, optional | The status interval of the workspace itself, 1 to 1440; omitted (or omitted when setting) means the environment's. It belongs to the workspace, not to the session. |
| `globalStatusMinutes` | number | The environment's status interval, which applies while the workspace has none of its own; read only. |
| `globalReviewPrompt`, `globalExpandPrompt` | string | The prompts of the environment, which the buttons send while the workspace has none of its own; read only. |
| `descriptors` | object | What the session says about how it runs, text by name; read only. |

**ai environment** (`GetAiEnvironment`, `SetAiEnvironment`)

| Property | Type | Meaning |
| --- | --- | --- |
| `defaultService` | string | The provider new workspaces start with. |
| `claudeCommand`, `codexCommand`, `localCommand` | string | The executables of Claude Code, Codex and OpenCode. |
| `claudeConfigFolder`, `codexConfigFolder` | string, optional | The folder a tool keeps its configuration in. |
| `hasClaudeApiKey`, `hasCodexApiKey`, `hasOpenAiApiKey` | bool | Whether the server holds the key; set by the server. |
| `claudeApiKey`, `codexApiKey`, `openAiApiKey` | string, optional | Write only: a key to set (empty removes it, omitted keeps it). Never sent by the server. |
| `localServer` | string | Address of the OpenAI compatible model server. |
| `localModels` | capability names | The local model for each capability (`fast`, `balanced`, `strong`, `max`); all four or none. |
| `localContexts` | capability contexts, optional | The context size in tokens for each capability, each optional. |
| `imageGeneration` | string | `None`, `OpenAi` or `Local`. |
| `localImageServer` | string | Address of the Stable Diffusion server. |
| `localImageModels` | capability names | The checkpoint for each capability; all four when `imageGeneration` is `Local`. |
| `statusMinutes` | number | How often, in minutes (1 to 1440, default 5), the AI is told to report its status in every workspace that has no interval of its own. |
| `reviewPrompt`, `expandPrompt` | string | The prompts the review and expand buttons send in every workspace that has none of its own; trimmed, and a blank one is replaced by the default (`review workspace and fix any problems or inconsistencies`, `expand upon content in workspace in areas that make sense`). |
| `newWorkspaces` | workspace defaults | What the AI session of a new workspace starts with: `capability`, `effort`, `isShellAllowed`, `isSystemAccessAllowed`, `isWebAllowed`, `isImageGenerationAllowed` (which only takes effect while `imageGeneration` is set) and `instructions`, a string where empty means none (the wire leaves out what is null, so none is not null). It starts balanced and high, with the web allowed, the shell, files outside the workspace and image generation not allowed, and no instructions. A workspace keeps the values it was created with whatever this says later. |

**usage** (`GetUsage`)

| Property | Type | Meaning |
| --- | --- | --- |
| `service` | string | The AI service the limits belong to. |
| `windows` | array of period | Every window the service reports, in the order it reports them; empty when it reports none. |

**period** (items of `usage.windows`)

| Property | Type | Meaning |
| --- | --- | --- |
| `name` | string | The name the service gives the window, such as `Session`, `Week` or `Week (Opus)`. |
| `percentUsed` | number | How much of the window's limit is used, 0 to 100. |
| `windowMinutes` | number | Length of the window. |
| `resetsAt` | string, optional | When the window resets, ISO 8601 UTC; omitted when unknown. |

The client lists every window and colors each one, and the usage icon by the shortest one, by its pace: percent used minus the percent of the window already passed. Within 5 points is even (normal color), 5 to 15 under is low (blue) and beyond that very low (green), 5 to 15 over is high (orange) and beyond that very high (red); a used up limit is always very high, and an unknown or passed reset time is even.

**message** (items of `history.messages`, and carried by `MessageAdded` events)

| Property | Type | Meaning |
| --- | --- | --- |
| `role` | string | Author: `User` (the person using the client) or `Assistant` (the AI). |
| `text` | string | The message text. |
| `time` | string, optional | When the message was written, ISO 8601 UTC; a message read from the history has the time the conversation tool stored (absent when it stored none), a live one the time the server published it. |

**files** (`ListFiles`)

| Property | Type | Meaning |
| --- | --- | --- |
| `path` | string | Folder path relative to the workspace folder; empty for the workspace folder. |
| `parent` | string, optional | Relative path of the containing folder; omitted at the workspace folder. |
| `entries` | array of entry | Folders first, then files, each ordered by name. |

**search result** (item of `searchResults`)

| Property | Type | Meaning |
| --- | --- | --- |
| `path` | string | File path relative to the workspace folder, using `/` separators. |
| `line` | number | One-based matching line; 0 for a match on the file name. |
| `text` | string | The trimmed matching line; empty for a match on the file name. |

**entry** (item of `files.entries`)

| Property | Type | Meaning |
| --- | --- | --- |
| `name` | string | Entry name. |
| `path` | string | Path relative to the workspace folder, using `/` separators. |
| `isFolder` | bool | Whether the entry is a folder. |
| `size` | number | Size in bytes; 0 for a folder. |

**content** (`ReadFile`)

| Property | Type | Meaning |
| --- | --- | --- |
| `path` | string | File path relative to the workspace folder. |
| `text` | string | The file text, cut short when the file is large. |
| `truncated` | bool, optional | `true` when `text` is only the start of the file; omitted otherwise. |

**git status** (`GetGitStatus`)

| Property | Type | Meaning |
| --- | --- | --- |
| `isRepo` | bool | Whether the workspace folder is the root of a git repository; the rest is meaningful only then. |
| `branch` | string, optional | The checked out branch; omitted when the head is detached. |
| `tag` | string, optional | A tag at the head. |
| `commit` | string | The abbreviated hash of the head commit; empty before the first commit. |
| `message` | string | The head commit's subject. |
| `hasChanges` | bool | Whether the working tree has changes that are not committed. |
| `hasIdentity` | bool | Whether git knows who commits; clients ask for a name and email first when it is false. |
| `busy` | bool, optional | `true` while a git action that the user waits for runs in the workspace, whichever client started it; the automatic fetch and pull do not count. |
| `remote` | string, optional | The URL of `origin`. |

**branch state** (`GetGitBranchState`)

| Property | Type | Meaning |
| --- | --- | --- |
| `unique` | number | Commits the branch has that the base lacks. |
| `isBased` | bool | Whether the base's tip is part of the branch's history. |
| `subject` | string | The subject of the branch's tip, the default message of a squash. |

**git changes** (`GetGitChanges`, `GetGitCommitChanges`)

| Property | Type | Meaning |
| --- | --- | --- |
| `changes` | array of change | Ordered by path. |

**change** (item of `gitChanges.changes`)

| Property | Type | Meaning |
| --- | --- | --- |
| `path` | string | Relative to the workspace folder, with `/` separators; an ignored folder ends with `/`. |
| `status` | string | `Added` (new or untracked), `Modified` (changed or renamed, by its new path), `Deleted` or `Ignored` (working tree only). |

**git history** (`GetGitHistory`)

| Property | Type | Meaning |
| --- | --- | --- |
| `commits` | array of commit | Newest first. |
| `hasMore` | bool, optional | `true` when older commits exist beyond the chunk. |

**commit** (item of `gitHistory.commits`)

| Property | Type | Meaning |
| --- | --- | --- |
| `hash` | string | The full hash. |
| `subject` | string | The first line of the message. |
| `author` | string | The author's name. |
| `date` | string | ISO 8601. |
| `parents` | array of string | The full hashes of the commit's parents, the first being the one it continues; clients lay out the history graph from them. |
| `refs` | array of ref | The branches and tags pointing at the commit. |

**ref** (item of `commit.refs`)

| Property | Type | Meaning |
| --- | --- | --- |
| `name` | string | The short name. |
| `kind` | string | `Branch`, `Remote` or `Tag`. |
| `isCurrent` | bool, optional | `true` for the checked out branch. |

**script runs** (`GetScripts`)

| Property | Type | Meaning |
| --- | --- | --- |
| `runs` | array of script run | Earliest started first, a task followed by the scripts it runs. |

**script run** (item of `scripts.runs`, and `event.script`)

| Property | Type | Meaning |
| --- | --- | --- |
| `path` | string | The file relative to the workspace folder, which identifies the run. |
| `name` | string | The file name without its extension. |
| `state` | string | `Pending` (a script of a task waiting for its turn), `Running`, `Succeeded`, `Failed`, `Stopped` or `Skipped` (a script of a task that stopped before its turn). |
| `isTask` | bool, optional | `true` for a `.task` file. |
| `taskPath` | string, optional | The task that runs this script. |
| `scripts` | array of string, optional | The scripts a task runs, in order. |
| `output` | string, optional | Everything written so far, the earliest part cut beyond 500000 characters; omitted in events. |
| `trimmed` | bool, optional | `true` when the earliest part of the output was cut. |

**git diff** (`GetGitDiff`)

| Property | Type | Meaning |
| --- | --- | --- |
| `path` | string | The file. |
| `lines` | array of line | The whole file in order, added and removed lines mixed in; empty for a binary file. |
| `isBinary` | bool, optional | `true` for a binary file. |
| `truncated` | bool, optional | `true` when `lines` is only the start (5000 lines). |

**line** (item of `gitDiff.lines`)

| Property | Type | Meaning |
| --- | --- | --- |
| `kind` | string | `Unchanged`, `Added` or `Removed`. |
| `text` | string | The line without its terminator. |
| `oldNumber` | number, optional | The line number before the change; absent for an added line. |
| `newNumber` | number, optional | The line number after the change; absent for a removed line. |

## Events

The server pushes events to every authenticated connection, as responses with `id` zero and the `event` property set. Client request ids start at one, so zero never collides. Events of one connection are delivered in order.

**event**

| Property | Type | Meaning |
| --- | --- | --- |
| `kind` | string | `MessageAdded`, `WorkingChanged`, `FilesChanged`, `GitChanged`, `Failed`, `ScriptChanged`, `ScriptOutput`, `ScriptRemoved`, `StateChanged`, `WorkspacesChanged` or `LoopChanged`. |
| `workspaceId` | GUID | The workspace concerned; the empty GUID for `WorkspacesChanged`, which is about all of them. |
| `message` | message | `MessageAdded`: the message, from the user or the AI. |
| `working` | bool | `WorkingChanged`: whether the AI is working now. |
| `path` | string | `FilesChanged`: a folder relative to the workspace folder whose entries or their contents changed (empty for the workspace folder itself); one event per folder. `ScriptOutput` and `ScriptRemoved`: the file of the run. |
| `script` | script run | `ScriptChanged`: the run that started or changed state, without its output. |
| `state` | string | `StateChanged`: the state the workspace entered: `Ready`, `Working`, `Running`, `Managing`, `Stopping` or `Canceling`. |
| `loop` | loop, optional | `LoopChanged`: the loop that runs in the workspace, absent once it ended. |
| `text` | string | `ScriptOutput`: what the run wrote since the last event. `LoopChanged`: why a loop ended by itself. |
| `error` | string | `Failed`: why the AI could not do its work. |

`ScriptChanged` is pushed when a run starts and each time its state changes, a task and each of its scripts separately; `ScriptOutput` carries what a running script wrote, batched about every 100 milliseconds, in order with the state events; `ScriptRemoved` says a run was forgotten or replaced by a new run of the same file. A client reads `GetScripts` after subscribing and applies the events it received meanwhile.

`WorkspacesChanged` says the workspaces or groups changed on the server (one was created, deleted, renamed, moved, reordered or configured, by any client); it carries no details, so a client lists the workspaces again with `ListWorkspaces`, which it also does after reconnecting. A new AI session being attached to a workspace is not announced. The client keeps what it has open: the focused workspace stays open untouched unless it was deleted (the view clears and the drawer opens) or its folder or AI settings changed (it is reopened).

`GitChanged` says a git action started or finished in the workspace (the automatic fetch and pull only say it when they changed something), so a client reads the git state again; the server also sends it when the head, the index or the refs of the repository change outside Auto Remote, and a client does the same when a `FilesChanged` event arrives, when a `WorkingChanged` event says the AI stopped, and after its own git actions.

A client that opens a workspace subscribes first and then reads the newest history chunk, holding the events that arrive meanwhile: the leading `MessageAdded` events whose messages the chunk already ends with are duplicates and are dropped, the rest are appended, and later events are appended as they come. A client that may have missed events reads the newest chunk again the same way. `WorkingChanged` is not guaranteed to be balanced; the last one received is the truth.

## Requests are multiplexed

Every `RemoteRequest` carries a client-chosen `id` that the matching `RemoteResponse` echoes. The server handles each request on its own task and serializes only the writes, and the client keeps a table of pending requests fed by a single read loop. This lets slow requests such as renaming or deleting a large folder run without freezing the file browser and workspace list. The trade-off is that the client must tolerate responses and events in any order relative to each other.

## Errors

A request the server understands but refuses (missing folder, name taken, workspace busy) comes back as a normal response with `error` set, and the client raises it as `RemoteException`. Only a malformed line or a broken socket closes the connection. When the connection drops, every pending request fails together with an `IOException`.

## Disconnects do not cancel the AI's work

The AI keeps working after a client leaves, because the server records every message as it happens. A reconnecting client sees the conversation by reading the history and learns whether the AI is still working from it. A connection's file watch ends with the connection. Events are not replayed to a connection that was away, so a client that loses its connection reopens it by itself (retrying after one second, then two, four, eight and every fifteen), replays its file watch, and reads again everything its screens show: the newest messages and whether the AI works, the scripts, the git state, the files and the workspace state. A mobile or browser client that was in the background for a few seconds does not trust its connection, which may have died without telling, and replaces it the same way when it comes back.
