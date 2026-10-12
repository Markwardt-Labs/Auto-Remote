# Protocol

How the client and server talk after the TCP connection is up.

## Transport

Every connection is TLS; there is no plaintext mode. The server listens on two ports so no traffic has to be told apart: `port` carries the protocol as newline-delimited lines over TLS (the mobile and desktop clients), and `webPort` carries the same lines as WebSocket messages over TLS (the browser, which cannot open raw sockets). Inside a WebSocket every message is one line of this protocol (a trailing newline is optional when sending) and each line the server sends is one text message, so everything below applies unchanged. On the web port, an HTTPS request that is not a WebSocket upgrade is answered with a short text page, which lets a browser be pointed at the port once to accept a self-signed certificate. No origin check is made because the access key is the authentication.

The server presents one certificate: the PFX named by `certificateFile`, or a self-signed one it creates on first start as `certificate.pfx` in the data folder (private key, readable by its owner only) and logs the SHA-256 fingerprint of at startup. The Avalonia clients accept whatever certificate the server presents without validating or pinning it, so the connection is encrypted but the server is not authenticated by its certificate; the access key is the only authentication.

## Framing

Every line in both directions, including the handshake, is one JSON object, camelCase, nulls omitted. `IMessageChannel` owns this framing so server and client cannot drift apart; the handshake itself reads byte by byte so no buffered reader can swallow bytes meant for the channel that follows.

## Handshake

The client opens the TLS connection and sends one JSON line (at most 1024 bytes, within 5 seconds), then the server replies with one JSON line. On acceptance the connection carries requests and responses; on rejection the server closes it. The key is compared in constant time. A line that is not valid JSON, or has no key, is a rejection.

| Direction | Property | Type | Meaning |
| --- | --- | --- | --- |
| Client to server | `accessKey` | string | The access key the client was configured with. |
| Server to client | `accepted` | bool | `true` if the key was correct. |

```json
{"accessKey": "change-me"}
{"accepted": true}
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
| `groupId` | GUID | per command | The group targeted; for `MoveWorkspace`, omitted means no group. |
| `parentId` | GUID | no | `CreateGroup` and `MoveGroup`: the group to put the group in; omitted means the top level. |
| `before` | number | no | `GetHistory` only: read the messages before this position in the conversation; omitted means the newest. |
| `limit` | number | no | `GetHistory`: the most messages to read; 50 when omitted, at most 200. `GetGitHistory`: the most commits to read; 30 when omitted, at most 200. |
| `skip` | number | no | `GetGitHistory` only: how many of the newest commits to skip; zero when omitted. |
| `service` | string | `ConfigureWorkspace`, `GetUsage` | `ClaudeCode` or `Codex`. |
| `model` | string | `ConfigureWorkspace` | For `ClaudeCode`: `Sonnet`, `Opus` or `Haiku`. For `Codex`: `Terra`, `Sol` or `Luna`. Refused when the service does not offer it. |
| `effort` | string | `ConfigureWorkspace` | `Low`, `Medium`, `High`, `XHigh` or `Max`. |
| `name` | string | per command | A folder name, a group name, a branch or tag name, or the name of the folder `CloneWorkspace` creates. |
| `path` | string | per command | A folder or file path; for `CloneWorkspace` the existing folder to clone into. |
| `url` | string | `CloneWorkspace`, `GitSetRemote` | A git repository URL. |
| `commit` | string | per command | A commit hash, 4 to 64 hexadecimal characters. |
| `base` | string | per command | The branch to compare with, rebase onto, squash against or merge into. |
| `email` | string | `GitSetIdentity` | The git email. |
| `force` | bool | no | `GitCheckout` only: discard changes that are not committed first; omitted means false. |
| `paths` | array of string | `WatchFiles` | Folder paths. |
| `text` | string | `Prompt`, `GitCommit` | The prompt text, or the commit message. |
| `deleteFolder` | bool | no | `DeleteWorkspace` only; defaults to `true` when omitted. |

Properties a command does not use are omitted.

### Response

```json
{"id": 7, "history": {"messages": [{"role": "Assistant", "text": "Done."}], "start": 12}}
```

Exactly one payload property is set on success (none for `DeleteWorkspace`, `DeleteGroup`, `Prompt`, `WatchFiles` and the git actions: every `Git...` command except the `GetGit...` reads); on failure only `error` is set (see Errors). A response with `id` zero answers no request and carries `event` instead (see Events).

| Property | Type | Set for |
| --- | --- | --- |
| `id` | number | Always; the request's `id`. |
| `error` | string | Failure only. |
| `catalog` | catalog | `ListWorkspaces` |
| `workspace` | workspace | `CreateWorkspace`, `CloneWorkspace`, `RenameWorkspace`, `ConfigureWorkspace`, `MoveWorkspace` |
| `group` | group | `CreateGroup`, `RenameGroup`, `MoveGroup` |
| `listing` | listing | `ListFolders`, `CreateFolder` |
| `history` | history | `GetHistory` |
| `usage` | usage | `GetUsage` |
| `files` | files | `ListFiles` |
| `content` | content | `ReadFile` |
| `git` | git status | `GetGitStatus` |
| `gitBranch` | branch state | `GetGitBranchState` |
| `gitChanges` | git changes | `GetGitChanges`, `GetGitCommitChanges` |
| `gitHistory` | git history | `GetGitHistory` |
| `gitDiff` | git diff | `GetGitDiff` |
| `event` | event | Pushed by the server, with `id` zero (see Events). |

### Commands

A missing required property fails with `The request needs a <field>.` (`workspaceId` reports `a workspace` and `groupId` reports `a group`).

| Command | Request properties | Response property | Notes |
| --- | --- | --- | --- |
| `ListWorkspaces` | none | `catalog` | Workspaces and groups, each ordered by name. |
| `CreateWorkspace` | `path` (existing folder on the server) | `workspace` | |
| `RenameWorkspace` | `workspaceId`, `name` (new folder name) | `workspace` | Refused while the AI is working in the workspace. |
| `ConfigureWorkspace` | `workspaceId`, `service`, `model`, `effort` | `workspace` | Applies from the next AI process: an idle one is ended now, a working one finishes first. Switching the service detaches the workspace's AI session, since sessions belong to one service. |
| `MoveWorkspace` | `workspaceId`, optional `groupId` | `workspace` | Omitting `groupId` takes the workspace out of any group. The group must exist. |
| `CreateGroup` | `name`, optional `parentId` | `group` | Names are trimmed, must be non-empty and unique ignoring case among the groups with the same parent. The parent must exist. |
| `RenameGroup` | `groupId`, `name` | `group` | Same name rules as `CreateGroup`. |
| `MoveGroup` | `groupId`, optional `parentId` | `group` | Omitting `parentId` moves it to the top level. Refused into itself or anything inside it, and when the name is taken in the new place. |
| `DeleteGroup` | `groupId` | none | Its workspaces and groups are kept and move into its parent. |
| `DeleteWorkspace` | `workspaceId`, optional `deleteFolder` | none | Refused while the AI is working in the workspace. |
| `ListFolders` | optional `path` (absolute; the server's `defaultFolder` when omitted) | `listing` | `listing.path` is empty when listing file system roots. |
| `CreateFolder` | `path` (the parent), `name` | `listing` | Listing of the parent, including the new folder. |
| `GetHistory` | `workspaceId`, optional `before`, `limit` | `history` | One chunk, oldest first. |
| `GetUsage` | `service` | `usage` | The service's usage limits as the server last read them, at most a minute old. Clients ask once a minute for the focused workspace's service. |
| `Prompt` | `workspaceId`, `text` | none | Answers as soon as the text is handed to the AI, also while it is working, when it joins the work in progress. The message and the AI's replies arrive as events. |
| `ListFiles` | `workspaceId`, optional `path` (relative to the workspace folder; empty or omitted for the folder itself) | `files` | |
| `ReadFile` | `workspaceId`, `path` (relative to the workspace folder) | `content` | |
| `WatchFiles` | `workspaceId`, `paths` (folders relative to the workspace folder; empty string for the folder itself) | none | Replaces the folders this connection watched before; an empty list stops watching. Changes arrive as `FilesChanged` events. |
| `CloneWorkspace` | `url`, `path` (existing folder on the server), optional `name` | `workspace` | The server runs `git clone` into a new folder of that name (the repository's name when omitted) inside `path` and creates a workspace in it. Refused when the folder exists; a failed clone leaves nothing behind. It answers when the clone is done, which can take minutes. |
| `GetGitStatus` | `workspaceId` | `git` | `isRepo` is false for a folder that is not the root of a repository, and when git is not installed on the server. |
| `GetGitChanges` | `workspaceId` | `gitChanges` | Paths that differ from the head commit plus the ones git ignores; empty for a folder that is not a repository. |
| `GetGitHistory` | `workspaceId`, optional `skip`, `limit` | `gitHistory` | Commits of every branch and tag, newest first; empty before the first commit. |
| `GetGitCommitChanges` | `workspaceId`, `commit` | `gitChanges` | The paths a commit changed relative to its first parent. |
| `GetGitDiff` | `workspaceId`, `path`, optional `commit` | `gitDiff` | The whole file with its changes marked: the commit against its first parent, or without `commit` the working tree against the head. |
| `GitCommit` | `workspaceId`, `text` | none | Stages everything, commits, then pushes the branch to `origin` when there is one; a failing push is ignored. Refused while the AI is working in the workspace, and when there is nothing to commit. |
| `GitReset` | `workspaceId` | none | Discards every change that is not committed, new files included. Refused while the AI is working. |
| `GitCreateBranch` | `workspaceId`, `name` | none | Creates the branch at the head and checks it out. Refused while the AI is working, and for an invalid or taken name. |
| `GitCreateTag` | `workspaceId`, `name` | none | Creates an annotated tag at the head; refused for an invalid or taken name or before the first commit. |
| `GitSetRemote` | `workspaceId`, `url` | none | Sets `origin`, creating the repository first when the workspace has none. |
| `GitCheckout` | `workspaceId`, `name`, optional `force` | none | Checks out a branch, a tag or a commit (the last two detach the head); a branch that exists only on the remote gets a local tracking branch. Without `force` it fails when changes are not committed. Refused while the AI is working. |
| `GitDeleteBranch` | `workspaceId`, `name` | none | Deletes the branch locally and, when it exists there, on the remote. The checked out branch cannot be deleted. Refused while the AI is working. |
| `GitDeleteTag` | `workspaceId`, `name` | none | Deletes the tag locally and on the remote. |
| `GetGitBranchState` | `workspaceId`, `name`, `base` | `gitBranch` | How many commits the branch has that `base` lacks, whether it is built on `base`, and its tip's subject. |
| `GitSquash` | `workspaceId`, `name`, `base`, `text` | none | Collapses the branch's commits that `base` lacks into one commit with message `text` without touching the working tree, then force-pushes the branch. Refused while the AI is working. |
| `GitRebase` | `workspaceId`, `name`, `base`, optional `text` | none | Rebases the branch onto `base` (it is checked out for that and the original branch is checked out again), squashing its own commits first when `text` is given, then force-pushes it. Conflicts are handed to the AI of the workspace, up to three attempts, otherwise the rebase is aborted. Needs a clean working tree. Refused while the AI is working. |
| `GitMerge` | `workspaceId`, `name` (source), `base` (target), optional `text` | none | Rebases the source onto the target when needed (squashing with `text` when given), fast-forwards the target to it, pushes the target and deletes the source locally and on the remote; the target stays checked out. Conflicts are handled as in `GitRebase`. Needs a clean working tree. Refused while the AI is working. |
| `GitFetch` | `workspaceId` | none | Fetches `origin` with tags and pruning, moves local branches that have no commits of their own to match the remote, and detaches and deletes the checked out branch when its remote counterpart was deleted. Does nothing without a remote. |
| `GitSync` | `workspaceId` | none | `GitFetch`, then fast-forwards the checked out branch onto its remote counterpart, stashing changes that are not committed and restoring them (the AI resolves conflicts of the restore). Does nothing while the AI is working or another git action runs, without a remote, or when the branch has diverged. |
| `GitSetIdentity` | `workspaceId`, `name`, `email` | none | Sets the git user name and email in the server user's global git configuration. |
| `GitCancel` | `workspaceId` | none | Cancels the git action running in the workspace; that action then fails with a message saying it was cancelled and undone. |

### Payload objects

Nested objects are shown after the table of the object that contains them. Optional properties are omitted when null (or, for `truncated`, when false).

**workspace**

| Property | Type | Meaning |
| --- | --- | --- |
| `id` | GUID | The workspace's identifier. |
| `folder` | string | Absolute path of the workspace's folder on the server. The display name is not sent; clients take the last segment of this. |
| `aiSessionId` | GUID, optional | The AI session attached to the workspace; omitted until the first prompt. Informational: the server manages it. |
| `groupId` | GUID, optional | The group the workspace is filed in; omitted when it is at the top level. |
| `service` | string | The AI service the workspace's prompts run on; `ClaudeCode` by default. |
| `model` | string | The AI model the prompts run on; `Sonnet` by default. |
| `effort` | string | The AI effort the prompts run with; `High` by default. |

**catalog** (`ListWorkspaces`)

| Property | Type | Meaning |
| --- | --- | --- |
| `groups` | array of group | The groups, ordered by name. |
| `workspaces` | array of workspace | The workspaces, ordered by name. |

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
| `start` | number | Position of the first message of the chunk in the whole conversation; above zero means older messages exist. |
| `working` | bool, optional | `true` while the AI is working in the workspace; omitted otherwise. |

**usage** (`GetUsage`)

| Property | Type | Meaning |
| --- | --- | --- |
| `service` | string | The AI service the limits belong to. |
| `session` | period, optional | The short window (5 hours); omitted when the service reports none. |
| `week` | period, optional | The weekly window; omitted when the service reports none. |

**period** (`usage.session`, `usage.week`)

| Property | Type | Meaning |
| --- | --- | --- |
| `percentUsed` | number | How much of the window's limit is used, 0 to 100. |
| `windowMinutes` | number | Length of the window. |
| `resetsAt` | string, optional | When the window resets, ISO 8601 UTC; omitted when unknown. |

The client colors the usage icon by the pace of the session: percent used minus the percent of the window already passed. Within 5 points is even (normal color), 5 to 15 under is low (blue) and beyond that very low (green), 5 to 15 over is high (orange) and beyond that very high (red); a used up limit is always very high, and an unknown or passed reset time is even.

**message** (items of `history.messages`, and carried by `MessageAdded` events)

| Property | Type | Meaning |
| --- | --- | --- |
| `role` | string | Author: `User` (the person using the client) or `Assistant` (the AI). |
| `text` | string | The message text. |

**files** (`ListFiles`)

| Property | Type | Meaning |
| --- | --- | --- |
| `path` | string | Folder path relative to the workspace folder; empty for the workspace folder. |
| `parent` | string, optional | Relative path of the containing folder; omitted at the workspace folder. |
| `entries` | array of entry | Folders first, then files, each ordered by name. |

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
| `refs` | array of ref | The branches and tags pointing at the commit. |

**ref** (item of `commit.refs`)

| Property | Type | Meaning |
| --- | --- | --- |
| `name` | string | The short name. |
| `kind` | string | `Branch`, `Remote` or `Tag`. |
| `isCurrent` | bool, optional | `true` for the checked out branch. |

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
| `kind` | string | `MessageAdded`, `WorkingChanged`, `FilesChanged`, `GitChanged` or `Failed`. |
| `workspaceId` | GUID | The workspace concerned. |
| `index` | number | `MessageAdded`: the message's position in the conversation. |
| `message` | message | `MessageAdded`: the message, from the user or the AI. |
| `working` | bool | `WorkingChanged`: whether the AI is working now. |
| `path` | string | `FilesChanged`: the changed folder relative to the workspace folder. |
| `error` | string | `Failed`: why the AI could not do its work. |

`GitChanged` says a git action started or finished in the workspace (the automatic fetch and pull only say it when they changed something), so a client reads the git state again; it does the same when a `FilesChanged` event arrives, when a `WorkingChanged` event says the AI stopped, and after its own git actions.

A client that opens a workspace subscribes first and then reads the newest history chunk: an event whose `index` is below what it already has is a duplicate, one right after the end is appended, and one beyond a gap means it missed messages and reloads. `WorkingChanged` is not guaranteed to be balanced; the last one received is the truth.

## Requests are multiplexed

Every `RemoteRequest` carries a client-chosen `id` that the matching `RemoteResponse` echoes. The server handles each request on its own task and serializes only the writes, and the client keeps a table of pending requests fed by a single read loop. This lets slow requests such as renaming or deleting a large folder run without freezing the file browser and workspace list. The trade-off is that the client must tolerate responses and events in any order relative to each other.

## Errors

A request the server understands but refuses (missing folder, name taken, workspace busy) comes back as a normal response with `error` set, and the client raises it as `RemoteException`. Only a malformed line or a broken socket closes the connection. When the connection drops, every pending request fails together with an `IOException`.

## Disconnects do not cancel the AI's work

The AI keeps working after a client leaves, because the server records every message as it happens. A reconnecting client sees the conversation by reading the history and learns whether the AI is still working from it. A connection's file watch ends with the connection.
