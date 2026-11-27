# Protocol

How the client and server talk after the WebSocket connection is up.

## Transport

Every connection is a WebSocket over TLS (`wss`) on the single `port`; there is no plaintext mode and no other transport, so the desktop, mobile and web clients all connect the same way, and a reverse proxy or tunnel that forwards WebSockets (such as a Cloudflare tunnel) can front the one port. Every message is one line of this protocol (a trailing newline is optional when sending) and each line the server sends is one text message, so everything below applies unchanged. An HTTPS request that is not a WebSocket upgrade is answered with a short text page, which lets a browser be pointed at the port once to accept a self-signed certificate. No origin check is made because the access key is the authentication. Both ends send WebSocket pings every 20 to 30 seconds when idle, which keeps proxies from closing an idle connection; the Avalonia clients also drop a connection whose pings go unanswered for 20 seconds, so a connection that died silently (a phone leaving the network or sleeping) fails promptly instead of hanging. A client whose connection was lost opens a new one, with the same key handshake, on its next request and re-sends its last `WatchWorkspace`, since the server forgets a connection's watch with it; events pushed while it was away are not replayed.

The server presents one certificate: the PFX named by `certificateFile`, or a self-signed one it creates on first start as `certificate.pfx` in the data folder (private key, readable by its owner only) and logs the SHA-256 fingerprint of at startup. The Avalonia clients accept whatever certificate the server presents without validating or pinning it, so the connection is encrypted but the server is not authenticated by its certificate; the access key is the only authentication.

## Framing

Every line in both directions, including the handshake, is one JSON object, camelCase, nulls omitted. `IMessageChannel` owns this framing so server and client cannot drift apart; the handshake itself reads byte by byte so no buffered reader can swallow bytes meant for the channel that follows.

## Handshake

The client opens the WebSocket connection and sends one JSON line (at most 1024 bytes, within 5 seconds), then the server replies with one JSON line. On acceptance the connection carries requests and responses; on rejection the server closes it. The key is compared in constant time. A line that is not valid JSON, or has no key, is a rejection. After that a message (one line) may be up to 40 MB, which holds the largest attachment in base64; a longer one ends the connection.

| Direction | Property | Type | Meaning |
| --- | --- | --- | --- |
| Client to server | `accessKey` | string | The access key the client was configured with; empty for a paired server. |
| Client to server | `peer` | bool, optional | `true` for a paired server of the swarm. |
| Server to client | `accepted` | bool | `true` if the key was correct. |
| Server to client | `version` | string | The server's version (`major.minor.patch`), sent only with an acceptance and left out by a server that predates version checks. The Avalonia clients compare it with their own on every connect and reconnect. |

```json
{"accessKey": "change-me"}
{"accepted": true, "version": "1.2.3"}
```

A paired server of the swarm connects to the same port with `peer: true` and an empty `accessKey`; the server answers `accepted: true` without a version. Such a connection gets no events and takes no requests: what follows are the swarm's own frames, in which a peer pairs with a one-time code or, after a signed hello, exchanges sealed packages.

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
| `inputId` | GUID | `TakeSavedInput`, `DiscardSavedInput`, `SetSavedInputAutoCommit` | The saved input meant. |
| `autoCommit` | bool | no | `SaveInput` and `QueueInput`: whether the work the input starts is committed, `true` when omitted. `Prompt`: `true` turns auto commit on for the work the prompt starts or joins, except during a run of a loop; otherwise it is left as it is. |
| `before` | string | no | `GetHistory` only: read the messages before this cursor (the `older` of an earlier chunk); omitted means the newest. The cursor is the position, counted from the oldest message the client may be shown (work messages count only while the workspace shows its work), of the first message after it: a client that drops the oldest messages it holds adds their number to the cursor it has to read before the first message it keeps, and clients ask for 100 messages at a time. |
| `characters` | number | no | `GetHistory`: the most characters of text to read, at least one message always; 20,000 when omitted, at most 400,000. A chunk ends at whichever of `limit` and `characters` is reached first. |
| `limit` | number | no | `GetHistory`: the most messages to read; 50 when omitted, at most 200. `GetGitHistory`: the most commits to read; 30 when omitted, at most 200. |
| `offset` | number | `ShiftGroup` | How many places to move: negative is up, positive is down. |
| `skip` | number | no | `GetGitHistory` only: how many of the newest commits to skip; zero when omitted. |
| `service` | string | `CreateWorkspace`, `CloneWorkspace`, `GetUsage` | `ClaudeCode`, `Codex` or `Local` (the models of a server on the server's machine such as Ollama, run through OpenCode); the provider a new workspace runs on for good, which is the default of the environment when omitted. `OpenCode` is accepted as `Local`. |
| `apiKey` | string | no | `CreateWorkspace`, `CloneWorkspace`: an API key for just the new workspace's AI session (Claude Code and Codex), which Live AI stores with the session; the server keeps no copy. Refused for `Local`. |
| `settings` | workspace settings | `SetWorkspaceSettings` | How the workspace's AI session is run. |
| `aiEnvironment` | ai environment | `SetAiEnvironment` | The environment of the server's AI. |
| `loop` | loop settings | `StartLoop` | What the loop sends, how often and when it ends. |
| `name` | string | per command | A folder name, a group name, a side session name, a branch or tag name, the git user name for `GitSetIdentity`, the name of the folder `CloneWorkspace` creates, or the file name for `UploadAttachment`. |
| `data` | string | `UploadAttachment`, `TranscribeSavedInput` | The file content or the WAV recording, base64 encoded. |
| `path` | string | per command | A folder or file path; for `CloneWorkspace` the existing folder to clone into; for the script commands the `.cs` or `.task` file. |
| `environment` | object, optional | `RunScript` | The client's `DISPLAY`, `WAYLAND_DISPLAY`, `XAUTHORITY` and `XDG_SESSION_TYPE`, when it has them. |
| `url` | string | `CloneWorkspace`, `GitSetRemote`, `ListLocalModels`, `ListImageModels` | A git repository URL; for the two list commands the optional address of the model server or image server to ask, the one of the environment when omitted. |
| `enabled` | bool | `ConfigureOllamaService`, `SetIntegrateWhenFinished`, `SetAutoCommit`, `SetSavedInputAutoCommit` | Whether the Ollama service runs with the optimized context settings, whether the side session is integrated once it is finished, or whether auto commit is on, for the work under way or for a saved input (required for each). |
| `start` | string | `CreateSideSession` | What the session's files start from: `Commit` (main's current commit) or `Files` (main's files as they are, uncommitted changes included). |
| `password` | string, optional | `ConfigureOllamaService` | The password of the account the server runs as, for `sudo`, when `ollamaServiceNeedsPassword` says it is needed. Handed to `sudo` on its standard input only; never stored, logged or put in a command line. |
| `commit` | string | per command | A commit hash, 4 to 64 hexadecimal characters. |
| `base` | string | per command | The branch to compare with, rebase onto, squash against or merge into. |
| `email` | string | `GitSetIdentity` | The git email. |
| `force` | bool | no | `GitCheckout` only: discard changes that are not committed first; omitted means false. |
| `text` | string | `CreateSideSession` (optional first task), `Prompt`, `GitCommit`, `SaveInput`, `QueueInput`, `SendScriptInput`, `SearchFiles`, `RenderDiagram`, `GitSquash`, `GitRebase`, `GitMerge` | The prompt text, the commit message (of the squashed commit for the last three, optional for `GitRebase` and `GitMerge`), the chat input to save, the line written to a script, the text to search for or the Mermaid source. |
| `deleteFolder` | bool | no | `DeleteWorkspace` only; defaults to `true` when omitted. |
| `showWork` | bool | `SetShowWork` | Whether the chat of the workspace shows the AI's work (required for that command). |
| `ignoreLimit` | bool | no | `Prompt` only: the user confirmed that the work starts although a usage window is at or past the maximum; the workspace then ignores the maximum until the work is done. |
| `keep` | bool | no | `CancelQueuedWork`: keep the text of the removed prompt as a saved input. |
| `swarmServer` | swarm server | `SetSwarm` | The swarm settings of the server. |
| `swarmPeer` | swarm peer | `SetSwarmPeer` | The settings for a paired server. |
| `peerId` | string | `RevokeSwarmPeer`, `ConfirmDrivePairing` | A server of the swarm. |
| `address`, `code` | string | `PairWithCode` | The `host:port` of the server that made the code, and the code. |
| `task` | swarm task request | `DelegateTask` | The task to delegate. |
| `taskId` | GUID | the swarm task commands | The delegated task. |
| `showHidden` | bool | no | `ListFiles` and `SearchFiles`; includes the entries a `.filehide` file hides. |
| `searchContent` | bool | no | `SearchFiles` only; searches the contents of the files instead of their names. |

Properties a command does not use are omitted.

### Response

```json
{"id": 7, "history": {"messages": [{"role": "Assistant", "text": "Done."}], "older": "48"}}
```

Exactly one payload property is set on success (commands whose response column below says none have none, such as `DeleteWorkspace` and the git actions, every `Git...` command except the `GetGit...` reads; a `Prompt` has `queue` only when it had to wait); on failure only `error` is set (see Errors). A response with `id` zero answers no request and carries `event` instead (see Events).

| Property | Type | Set for |
| --- | --- | --- |
| `id` | number | Always; the request's `id`. |
| `error` | string | Failure only. |
| `catalog` | catalog | `ListWorkspaces` |
| `workspace` | workspace | `CreateWorkspace`, `CloneWorkspace`, `RenameWorkspace`, `MoveWorkspace`, `MoveWorkspaceToFolder`, `SetShowWork`, `CreateSideSession`, `RenameSideSession` |
| `group` | group | `CreateGroup`, `RenameGroup`, `MoveGroup` |
| `listing` | listing | `ListFolders`, `CreateFolder` |
| `history` | history | `GetHistory` |
| `usage` | usage | `GetUsage` |
| `resources` | resources | `GetResources` |
| `settings` | workspace settings | `GetWorkspaceSettings`, `SetWorkspaceSettings` |
| `aiEnvironment` | ai environment | `GetAiEnvironment`, `SetAiEnvironment` |
| `loop` | loop, optional | `GetLoop` |
| `names` | array of string | `ListLocalModels`, `ListImageModels` |
| `files` | files | `ListFiles` |
| `searchResults` | array of search result | `SearchFiles` |
| `content` | content | `ReadFile` |
| `diagram` | string | `RenderDiagram`: the SVG document. |
| `attachmentPath` | string | `UploadAttachment`: the path of the stored file on the server. |
| `state` | string | `GetWorkspaceState`: the workspace's state, as in the `StateChanged` event. |
| `git` | git status | `GetGitStatus` |
| `gitBranch` | branch state | `GetGitBranchState` |
| `gitChanges` | git changes | `GetGitChanges`, `GetGitCommitChanges`, `GetSessionChanges` |
| `gitHistory` | git history | `GetGitHistory` |
| `gitDiff` | git diff | `GetGitDiff`, `GetSessionDiff` |
| `commitMessage` | string | `GenerateCommitMessage` |
| `savedInputs` | array of saved input | `GetSavedInputs`, `SaveInput`, `QueueInput`, `TranscribeSavedInput`, `DiscardSavedInput`, `SetSavedInputAutoCommit` |
| `savedInput` | saved input | `TakeSavedInput` |
| `errors` | array of error | `GetErrors` |
| `overLimit` | limit pause | `Prompt`, together with `error`: the window that made the server refuse the prompt. |
| `scripts` | script runs | `GetScripts` |
| `queue` | queued work, optional | `GetQueuedWork`, `CancelQueuedWork` (what was removed), and a `Prompt` that had to wait; omitted when nothing waits. |
| `swarm` | swarm status | Every swarm command except `GetSwarmTask`. |
| `swarmTask` | swarm task detail | `GetSwarmTask` |
| `event` | event | Pushed by the server, with `id` zero (see Events). |

### Commands

While a workspace runs a delegated task, `Prompt`, `QueueInput`, `ResumeQueue`, `SetAutoCommit`, `SetSavedInputAutoCommit`, `UploadAttachment`, `StartLoop`, `RenameWorkspace`, `MoveWorkspaceToFolder`, `SetWorkspaceSettings`, `GenerateCommitMessage`, `RunScript`, `DelegateTask`, `CreateSideSession` and every git command except `GetGitStatus` (which answers that it is not a repository) and `GitCancel` are refused, and `StopWorkspace` and `CancelWorkspace` cancel the task.

A missing required property fails with a message that starts `The request needs` and names it (usually `a <field>.`; `workspaceId` reports `a workspace` and `groupId` reports `a group`).

A side session is a workspace to every command that takes a `workspaceId`, with these exceptions. Until it is `Ready` it refuses everything but `CancelIntegration` while `Integrating` and `DeleteSideSession` while `Creating`. It refuses renaming, moving and deleting it as a workspace, side sessions of its own, and `GitCheckout`, `GitCreateBranch`, `GitCreateTag`, `GitSetRemote`, `GitDeleteBranch`, `GitDeleteTag`, `GitSquash`, `GitRebase`, `GitMerge` and `GitFetch`. `GitSync` succeeds without doing anything, `GitCommit` does not push, and `GetGitStatus` gives the session's name as its branch. The git actions of any workspace refuse the branches of side sessions. `RetrySwarmTask` and `ApplySwarmTask`, which name only a task, are refused for a task of a side session that is not `Ready`, and the server itself sends such a session no prompt.

| Command | Request properties | Response property | Notes |
| --- | --- | --- | --- |
| `ListWorkspaces` | none | `catalog` | The groups in their custom order and the workspaces, each with when its AI session was last loaded (`lastUpdated`), read from Live AI at each call. Clients put the working workspaces of a group (`busy`) first, then those that run a loop (`looping`), and order each of these by it, latest first. |
| `CreateWorkspace` | `path` (existing folder on the server), optional `service`, optional `apiKey`, optional `groupId` | `workspace` | The workspace runs on `service` (the environment's default provider when omitted) for good; the provider cannot be changed afterwards. The AI session is made together with the workspace, on `apiKey` when one is given. A workspace whose session cannot be made (a key for a provider that takes none, or local models without a model for every capability) refuses the request and nothing is created. |
| `RenameWorkspace` | `workspaceId`, `name` (new folder name) | `workspace` | Refused unless the workspace is Ready, and while a side session of it is being integrated. |
| `MoveWorkspaceToFolder` | `workspaceId`, `path` (existing folder the workspace works in from now on) | `workspace` | Tells the workspace's AI session that the workspace is in `path` now, which is what tracks the place of a workspace; no file is moved by the server, so the folder has to exist already (the project may have been moved on disk, even from a folder that is gone). The workspace keeps its identifier, session, group and place. Refused unless the workspace is Ready, while a side session of it is being integrated, when `path` does not exist, when it already hosts another workspace, and when the workspace has side sessions that `path` is not the repository of. Moving it to the folder it is in changes nothing. |
| `MoveWorkspace` | `workspaceId`, optional `groupId` | `workspace` | Omitting `groupId` takes the workspace out of any group. The group must exist. It goes to the end of the group. |
| `CreateGroup` | `name`, optional `parentId` | `group` | Names are trimmed, must be non-empty and unique ignoring case among the groups with the same parent. The parent must exist. |
| `RenameGroup` | `groupId`, `name` | `group` | Same name rules as `CreateGroup`. |
| `MoveGroup` | `groupId`, optional `parentId` | `group` | Omitting `parentId` moves it to the top level. Refused into itself or anything inside it, and when the name is taken in the new place. It goes to the end of its new parent. |
| `ShiftGroup` | `groupId`, `offset` | none | Moves the group among the groups with the same parent; it stops at the first or last place. |
| `DeleteGroup` | `groupId` | none | Its workspaces and groups are kept and move into its parent. |
| `DeleteWorkspace` | `workspaceId`, optional `deleteFolder` | none | Also removes the workspace's waiting work, ends its delegated tasks and permanently deletes its side sessions and their branches, even when the folder is kept. Refused unless the workspace is Ready. |
| `CreateSideSession` | `workspaceId` (the main workspace), `name`, `start`, optional `text` | `workspace` | Makes a side session, answering while it is still `Creating`; it turns `Ready` once its files are set up, and then `text` is sent to it. Needs the workspace to be the top folder of a git repository with a commit, and the side session storage to be outside every workspace. Names are unique among the workspace's sessions ignoring case, at most 60 characters, and not `Main`. |
| `RenameSideSession` | `workspaceId` (the side session, as for the next six, which answer that it no longer exists for any other), `name` | `workspace` | Changes the label only, under the name rules of `CreateSideSession`. |
| `DeleteSideSession` | `workspaceId` | none | Permanently discards the session. Refused while it is integrating. |
| `IntegrateSideSession` | `workspaceId` | none | Starts integrating the session into its main workspace; permanent. Refused while the session has AI work, a loop, queued work, a running script, queued inputs (paused ones too), an automatic commit under way or still to come, a delegated task under way or whose end its AI has yet to be told, or is paused at the usage limit. |
| `CancelIntegration` | `workspaceId` | none | Gives the session back; nothing main's AI already changed is undone. |
| `SetIntegrateWhenFinished` | `workspaceId`, `enabled` | none | Whether the session is integrated by itself once its AI marked its task complete and nothing of it runs. |
| `GetSessionChanges` | `workspaceId` | `gitChanges` | The files that differ from what the session started with, committed or not. |
| `GetSessionDiff` | `workspaceId`, `path` | `gitDiff` | One of those files compared with what it was when the session started. |
| `ListFolders` | optional `path` (absolute; the server's `defaultFolder` when omitted) | `listing` | `listing.path` is empty when listing file system roots. |
| `CreateFolder` | `path` (the parent), `name` | `listing` | Listing of the parent, including the new folder. |
| `GetHistory` | `workspaceId`, optional `before`, `limit`, `characters` | `history` | One chunk, oldest first, of the chat the server keeps: the user's prompts and the AI's messages. |
| `GetUsage` | `service` | `usage` | The service's usage limits as the server last read them, at most a minute old. A reading is kept for a little less than a minute, counted from when it began, so a client asking once a minute gets a new one each time. Clients ask once a minute for the focused workspace's service and as soon as they reconnect, and go on asking after a failed ask, since a lost connection reopens by itself. |
| `GetResources` | none | `resources` | What the server's computer has and what the workspaces take of it now, at most 1.5 seconds old. Clients ask every 3 seconds, only while it is on screen. |
| `StartLoop` | `workspaceId`, `loop` | none | Starts a loop of the AI: the prompt is sent over and over on the interval until the condition ends it or it is cancelled. The first run starts when the AI and the workspace are free and no commit or queued input goes first. Refused unless the workspace is Ready or Working or the changes are being committed after work, while a loop already runs there, for settings that are not valid, and for a usage window the service does not report. |
| `CancelLoop` | `workspaceId` | none | Cancels the loop at once without stopping the run in progress, which finishes, and removes a run that waits for the local AI unless a message of the user joined it; does nothing when no loop runs. |
| `GetLoop` | `workspaceId` | `loop` | The loop that runs in the workspace, or none. |
| `GetWorkspaceSettings` | `workspaceId` | `settings` | How the workspace's AI session is run, as the session stores it (the server keeps none of its own): the provider, capability, effort, whether the shell, files outside the workspace, the web and image generation are on, the instructions, the workspace's own review and expand prompts next to the global ones, and descriptors, which are lines the session writes about how it runs (the model it chose and so on). Makes the session when the workspace has none yet. |
| `SetWorkspaceSettings` | `workspaceId`, `settings` | `settings` | Gives the session every value in `settings` (the provider, the descriptors and the global prompts in it are ignored) from its next prompt on; the session stores them. The review and expand prompts, the status interval, whether the AI may delegate and what new side sessions get (`sessionFiles`, `sessionSetupScript`, ignored on a side session) are stored with the workspace instead, and an omitted or blank one of these but the delegation clears it. Refused for a capability or effort the AI does not know, instructions the session refuses and combinations the provider cannot do. |
| `GetAiEnvironment` | none | `aiEnvironment` | The environment of the server's AI. API keys are never sent, only whether one is set. |
| `SetAiEnvironment` | `aiEnvironment` | `aiEnvironment` | Replaces the environment and applies it to every workspace from the next session on. A key left out stays as it is and an empty one is removed. Refused for an empty command, an address that is not http or https, a context size below one, local models for some capabilities but not all, OpenAI image generation without a key, local image generation without a checkpoint for every capability, an unknown `localWorkMode` and a `delegatedTaskLimit` outside 1 to 16. A change of the mode or the limit applies to waiting work at once. |
| `ConfigureOllamaService` | `enabled`, optional `password` | `aiEnvironment` | Only when `ollamaServiceControl` is `true`: writes (or removes) the systemd drop-in with the Ollama variables of the optimized context, restarts the service and waits for it to answer, with root rights through `sudo`, then stores `localOptimizeContext` as `enabled`. Refused with the reason when the password is not accepted, the account may not use `sudo` or the service did not come back. Nothing changes in the rest of the environment. |
| `ListLocalModels` | optional `url` | `names` | The models the local model server offers (Ollama by default). Refused when the server cannot be reached. |
| `ListImageModels` | optional `url` | `names` | The checkpoints the Stable Diffusion server offers. Refused when the server cannot be reached. |
| `Prompt` | `workspaceId`, `text` | none, or `queue` | In a workspace on a local model the prompt may have to wait for the local AI: the answer then carries `queue`, its `MessageAdded` arrives once it starts, and further prompts join the waiting one. Otherwise answers as soon as the server has the text, also while the AI is working, when it is steered into the work in progress and, if the AI may have missed it, asked again as a turn of its own afterwards. The message and the AI's replies arrive as events. Refused unless the workspace is Ready or Working (so while a git action runs). Also refused, with `overLimit` in the response, while a usage window of the workspace's provider is at or past the maximum (or the workspace is in an automatic pause), unless `ignoreLimit` is true or the text is the pause prompt (`pause current work`). |
| `CancelLimitPause` | `workspaceId` | none | Ends the automatic pause of the workspace without telling the AI to resume, and gives up on what was to follow the work: auto commit goes off (unless the commit is under way), the queued inputs are paused, and the loop of the workspace ends if it has one. The AI is not paused again while it finishes what it is doing. |
| `UploadAttachment` | `workspaceId`, `name`, `data` | `attachmentPath` | Stores the file in the data folder, outside the workspace, and answers its server path (up to 20 MB). A client sends one for each attachment and then a `Prompt` with a line `Attached file: <path>` for each, so the AI can read it. Accepted in any state, since an input queued during a git action may need it. |
| `TranscribeSavedInput` | `workspaceId`, `data` (a 16 kHz, mono, 16 bit WAV recording) | `savedInputs` | Adds a saved input that is `isConverting` and answers the list with it at once; the server turns the recording (up to 4 MB) into text in the background with its built-in Whisper model, or its speech command when one is set (two minutes of processing at most), and every client gets a `SavedInputsChanged` event at each step. When the text is ready the entry keeps its `id` and `time` and gets the `text`, becoming a saved input like any other (subject to the same limits); when it fails, because nothing was heard, voice input is turned off on the server or its model cannot be loaded, it stays with the reason in `error`. Refused for an empty recording. A conversion that is not finished is not stored, so it does not survive a restart of the server. |
| `DiscardSavedInput` | `workspaceId`, `inputId` | `savedInputs` | Removes the saved input without taking it: a conversion that is running is cancelled and its text is never saved, and an entry with an `error` is dismissed. Nothing happens when it is gone. Every client gets a `SavedInputsChanged` event. |
| `GetSavedInputs` | `workspaceId` | `savedInputs` | The chat inputs saved in the workspace: the queued ones first, in the order they will be sent, then the others, the last saved first. |
| `QueueInput` | `workspaceId`, `text`, optional `autoCommit` | `savedInputs` | Adds a queued input behind those already queued and resumes any that are paused. The server sends it once nothing is in its way, at once when the workspace is free and ahead of further runs of a loop, with auto commit as the input says, and removes it from the list when it was sent. Refused for an empty text, one longer than 100000 characters, or when 200 inputs are queued already. Accepted in any state. |
| `SetSavedInputAutoCommit` | `workspaceId`, `inputId`, `enabled` | `savedInputs` | Sets whether the work a saved or queued input starts is committed. Refused once it is gone. Every client gets a `SavedInputsChanged` event. |
| `ResumeQueue` | `workspaceId` | none | Resumes the paused queued inputs, which the server then sends once nothing is in their way; nothing happens when none is paused. Accepted in any state. When any resumed, every client gets a `SavedInputsChanged` event. |
| `SetAutoCommit` | `workspaceId`, `enabled` | none | Turns auto commit on or off for the work under way; every client gets `AutoCommitChanged`. Turning it on is refused unless the AI works (or its work is paused at the usage limit and to be resumed) and while a run of a loop is under way; once the commit is under way, turning it off is refused (turning it on changes nothing), since only stopping the workspace ends it. |
| `SaveInput` | `workspaceId`, `text` | `savedInputs` | Saves the text exactly as it is as the newest saved input and answers the list; beyond 200 entries the oldest saved ones are dropped, never a queued one or the one just saved. Refused for an empty text or one longer than 100000 characters. Every client gets a `SavedInputsChanged` event. |
| `SetShowWork` | `workspaceId`, `showWork` | `workspace` | Sets whether the chat of the workspace shows the AI's work, for every client: the server keeps it with the workspace (it starts as the `showWork` of `newWorkspaces`) and sends a `ShowWorkChanged` event when it changed. Nothing happens when it already is so. |
| `GetErrors` | `workspaceId` | `errors` | The errors the AI of the workspace ended with in the last 30 days, the newest first. Entries older than that are removed by the server, when it starts, every 12 hours and whenever the list is read or an error is added. Only the errors of the workspace named are listed. |
| `TakeSavedInput` | `workspaceId`, `inputId` | `savedInput` | Removes the saved input and answers it, so only one client gets it; refused once it is gone, and for an entry that is still being converted or has an `error`. Every client gets a `SavedInputsChanged` event. |
| `UpdateServer` | none | none | Makes the server look for the newest release on GitHub, download its server executable and restart with it, which closes every connection. Answers once the download is complete, a second before the restart. Refused when there is no newer release, GitHub cannot be reached, the server does not run from an installed executable or the folder is not writable. Not tied to a workspace and allowed in any state. |
| `ListFiles` | `workspaceId`, optional `path` (relative to the workspace folder; empty or omitted for the folder itself), optional `showHidden` | `files` | Entries matched by a `.filehide` file are left out unless `showHidden` is true. |
| `ReadFile` | `workspaceId`, `path` (relative to the workspace folder) | `content` | |
| `RenderDiagram` | `text` (Mermaid source) | `diagram` | Draws the diagram as SVG in the colors of the clients' dark theme. Not tied to a workspace and allowed in any state. Refused when the text is empty or over 100,000 characters, cannot be drawn or takes over 20 seconds. |
| `SearchFiles` | `workspaceId`, `text`, optional `searchContent`, optional `showHidden` | `searchResults` | Walks the workspace folder, skipping `.git`, symbolic links and (unless `showHidden`) what `.filehide` files hide. By name it matches `text` as a case insensitive subsequence of the path, ranking file name matches and runs of consecutive characters higher, and returns at most 50 files. By content it returns at most 200 lines containing `text`, ignoring case, skipping binary files and files over 1 MB. |
| `WatchWorkspace` | `workspaceId` | none | Says that this connection has the workspace open, in place of the one it had open before. The server watches the whole folder of every workspace some connection has open, however deep, and pushes what changes in it to every connection as `FilesChanged` and `GitChanged` events, also when the change was made outside Auto Remote. The watch ends with the connection, and a connection that reconnects sends it again. |
| `CloneWorkspace` | `url`, `path` (existing folder on the server), optional `name`, optional `service`, optional `apiKey`, optional `groupId` | `workspace` | The server runs `git clone` into a new folder of that name (the repository's name when omitted) inside `path` and creates a workspace in it. Refused when the folder exists; a failed clone leaves nothing behind. It answers when the clone is done, which can take minutes. |
| `GetGitStatus` | `workspaceId` | `git` | `isRepo` is false for a folder that is not the root of a repository, and when git is not installed on the server. |
| `GetGitChanges` | `workspaceId` | `gitChanges` | Paths that differ from the head commit plus the ones git ignores; empty for a folder that is not a repository. |
| `GetGitHistory` | `workspaceId`, optional `skip`, `limit` | `gitHistory` | Commits of every branch and tag, newest first; empty before the first commit. |
| `GetGitCommitChanges` | `workspaceId`, `commit` | `gitChanges` | The paths a commit changed relative to its first parent. |
| `GetGitDiff` | `workspaceId`, `path`, optional `commit` | `gitDiff` | The whole file with its changes marked: the commit against its first parent, or without `commit` the working tree against the head. |
| `GitCommit` | `workspaceId`, `text` | none | Stages everything, commits, then pushes the branch to `origin` when there is one; a failing push is ignored. Refused unless the workspace is Ready, when there is nothing to commit, when the AI gives no message and when the message it ends with has twelve words or more. |
| `GenerateCommitMessage` | `workspaceId` | `commitMessage` | Asks the AI for a message of eight words or less for the pending changes (the changed paths and the diff of tracked files, cut at 30000 characters). On a local model it first waits for the local AI, and it fails when the AI has not answered 20 minutes after that. Refused unless the workspace is Ready, and when there is nothing to commit. |
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
| `GitCancel` | `workspaceId` | none | Cancels the git action running in the workspace; that action then fails with a message saying it was cancelled and undone (an automatic Sync stopped while restoring changes keeps them in the stash and says so). While the workspace is Managing it is the same as `StopWorkspace`. |
| `StopWorkspace` | `workspaceId` | none | Ends the work of the AI or the git action at once and answers when the workspace is Ready again, keeping what the work did (a git action is undone). Refused when nothing runs or the workspace is already Stopping or Canceling. |
| `CancelWorkspace` | `workspaceId` | none | Ends the AI's work and reverts what it did, answering when the workspace is Ready again. None of the clients sends it. Only for the AI's work: refused for a git action (stop it), when nothing runs, when it is already Stopping or Canceling, in a workspace that is not a git repository, and once the work under way finished integrating a side session, which reverting would lose. |
| `GetQueuedWork` | `workspaceId` | `queue` | The work of the workspace that waits for the local AI. |
| `CancelQueuedWork` | `workspaceId`, optional `keep` | `queue` | Removes what waits, never touching what runs, and answers what was removed. When anything waited, auto commit goes off and the queued inputs are paused; with `keep` its text becomes a saved input. For a delegated workspace it cancels the task. |
| `GetSwarm` | none | `swarm` | The swarm settings, paired servers, pairings waiting on the drive and delegated tasks. |
| `SetSwarm` | `swarmServer` | `swarm` | Changes the settings; `fingerprint` is ignored. Refused for a blank name, folders that are not full paths or a task limit outside 1 to 100. |
| `SetSwarmPeer` | `swarmPeer` | `swarm` | Changes whether work is sent and accepted, the mode, the address and the permissions; the read only properties are ignored. A worker uses a sender's git remote only while its own mode for that sender is `GitRemote`. |
| `RevokeSwarmPeer` | `peerId` | `swarm` | Forgets the server; tasks given to it fail and tasks it gave are cancelled. |
| `CreatePairingCode` | none | `swarm` | A code another server can pair with once within 15 minutes (`pairingCode`). |
| `PairWithCode` | `address`, `code` | `swarm` | Pairs with the server that made the code; everything starts turned off. |
| `WriteDriveInvitation` | none | `swarm` | Writes an invitation into the drive folder. |
| `ConfirmDrivePairing` | `peerId` | `swarm` | Pairs with the server of a pairing on the drive, writing the acceptance of an invitation. |
| `SyncDrive` | none | `swarm` | Reads from and writes to the drive; `report` says what happened. |
| `DelegateTask` | `workspaceId`, `task` | `swarm` | Delegates a task by hand. Refused for a delegated workspace, a workspace that is not a git repository with a commit, or no suitable worker. |
| `GetSwarmTask` | `taskId` | `swarmTask` | Everything about a task, with the newest part of its conversation. |
| `CancelSwarmTask` | `taskId` | `swarm` | Cancels an outgoing or incoming task. |
| `RetrySwarmTask` | `taskId` | `swarm` | Delegates a failed, cancelled or lost task again. |
| `ApplySwarmTask` | `taskId` | `swarm` | Applies a finished task's changes to the files of its workspace, which must be Ready, without committing; `report` says what happened and names any conflicts. |
| `DiscardSwarmTask` | `taskId` | `swarm` | Removes an ended task with its result branch. |
| `GetWorkspaceState` | `workspaceId` | `state` | Reads the state, which a client does when it opens a workspace and then follows with `StateChanged` events. |
| `GetScripts` | `workspaceId` | `scripts` | The runs the server holds for the workspace, with their output. |
| `RunScript` | `workspaceId`, `path` (a `.cs` or `.task` file) | none | Starts the run and answers once it started; progress arrives as events. Accepted in every state of the workspace, since scripts run beside any work (except in a workspace that runs a delegated task); refused for a file that is already running (or that a running task lists), for a path outside the workspace folder or that is missing, and for a task that lists anything but existing `.cs` files. A kept run of the same file is replaced. The `environment` is added to the environment of the scripts only when the connection comes from the server's own machine (a loopback address, or the address the server is listening on), and only the four display variables are taken from it. |
| `StopScript` | `workspaceId`, `path` | none | Kills the script, or every script of the task, and the task stops. Does nothing when the run is not in progress. |
| `SendScriptInput` | `workspaceId`, `path`, `text` | none | Writes the line to the standard input of the running script and adds it, with a newline, to the output. Refused when the script is not running. |
| `RemoveScript` | `workspaceId`, `path` | none | Forgets a finished run, and the runs of a task's scripts. Refused while it is in progress. |

### Payload objects

Nested objects are shown after the table of the object that contains them. Optional properties are omitted when null.

**workspace**

| Property | Type | Meaning |
| --- | --- | --- |
| `id` | GUID | The workspace's identifier. |
| `folder` | string | Absolute path of the workspace's folder on the server. The display name is not sent; clients take the last segment of this. |
| `sessionId` | string, optional | The live AI session that holds the workspace's folder, provider and AI configuration (32 hexadecimal digits). Informational: the server manages it and replaces it, keeping the workspace, when the session can no longer be used. |
| `groupId` | GUID, optional | The group the workspace is filed in; omitted when it is at the top level. |
| `lastUpdated` | timestamp, optional | When the workspace's AI session was last loaded, which is what workspaces are ordered by in their group (the latest first, after the ones a loop runs in); refreshed whenever the workspaces are listed. |
| `service` | string | The provider the workspace's prompts run on, chosen when the workspace is created and never changed; `ClaudeCode`, `Codex` or `Local`. |
| `reviewPrompt`, `expandPrompt` | string, optional | The prompts of the workspace's own that its review and expand buttons send; omitted when it uses the environment's. |
| `showWork` | bool | Whether the chat of the workspace shows the AI's work (see `SetShowWork`). |
| `allowDelegation` | bool | Whether its AI may delegate tasks to other servers. |
| `statusMinutes` | number, optional | The status interval the workspace has of its own; omitted when it uses the environment's. |
| `sessionFiles`, `sessionSetupScript` | string, optional | What is copied into new side sessions of the workspace (one relative path per line) and the script run in them. |
| `side` | side session, optional | What makes the workspace a side session of another; omitted for a workspace of its own. Clients show such a one as `parentName / name`. |

**side session** (`workspace.side`)

| Property | Type | Meaning |
| --- | --- | --- |
| `parentId` | GUID | The main workspace. |
| `parentName`, `name` | string | The main workspace's name and the session's. |
| `state` | string | `Creating`, `Ready`, `Integrating` or `Deleting`. |
| `startedFrom` | string | `Commit` or `Files`. |
| `integrateWhenFinished` | bool | See `SetIntegrateWhenFinished`. |
| `error` | string, optional | Why setting it up, or starting its integration, failed. |
| `unsentTask` | string, optional | A first task that has not been sent: it waits for the setup while the session is `Creating`, and stays when the setup or sending it failed, or when the usage limit is reached. `error` and `unsentTask` are cleared when the session is prompted, an input is queued in it or a queued one is sent, a loop is started in it or its integration starts, and `unsentTask` when it is sent. |

**catalog** (`ListWorkspaces`)

| Property | Type | Meaning |
| --- | --- | --- |
| `groups` | array of group | The groups in their custom order. |
| `workspaces` | array of workspace | The workspaces in their custom order. |
| `sessions` | array of workspace | The side sessions of all workspaces in the order they were made; they are not among `workspaces`, and `busy`, `looping` and `queued` cover them too. `WorkspacesChanged` says when they or their `side` changed. |
| `busy` | array of GUID | The workspaces in any state but `Ready` right now; `StateChanged` events keep it current. |
| `looping` | array of GUID | The workspaces a loop of the AI runs in right now; `LoopChanged` events keep it current. |
| `queued` | array of GUID | The workspaces with work waiting for the local AI; `QueueChanged` events keep it current. |
| `delegations` | array of delegation | The workspaces that run a task another server delegated: `workspaceId`, `from` (the sender's name) and `title`. |

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
| `older` | string, optional | The cursor to send as `before` to read the messages before this chunk, the decimal position of the chunk's first message; omitted when there are none. |
| `working` | bool | `true` while the AI is working in the workspace. |
| `autoCommit` | string, optional | `Off` (also when omitted), `On` while the changes are to be committed once the work is over, or `Committing` while they are being committed. |
| `limitPause` | limit pause, optional | The automatic pause the workspace is in because of a usage window; omitted when it is in none. A client takes it from here when it opens the workspace, reconnects or reads the history again, and follows it with `LimitPauseChanged` events. |
| `showWork` | bool | `true` when the workspace shows the AI's work: then `messages` include the work messages (`isWork`) and the chunks count them; otherwise they leave them out and the chunks count only what was said. Also the way a client learns the state, after it opens the workspace, reconnects or reads the history again on `ShowWorkChanged` or `MessageDelta`. |

**limit pause** (`limitPause` of the history and of `LimitPauseChanged`)

| Property | Type | Meaning |
| --- | --- | --- |
| `window` | string | The name of the usage window that is at or past the maximum, as the provider reports it. |
| `percentUsed` | number | The share of that window that is used, in percent, as of the last reading. |
| `maxPercent` | number | The maximum of the global settings the window is held to. |
| `pausedAt` | string | When the AI was told to pause, ISO 8601 UTC. |
| `nextCheckAt` | string | When the server reads the windows again, ISO 8601 UTC. |

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
| `waitForLimit` | bool, optional | Whether the loop waits when the usage limit of the service stops its prompts and goes on by itself (see `limitCheckAt`), instead of ending; `true` when omitted. |

**loop** (`GetLoop`, and `loop` of `LoopChanged`)

| Property | Type | Meaning |
| --- | --- | --- |
| `settings` | loop settings | What the loop was started with. |
| `startedAt` | string | When the loop was started, ISO 8601 UTC. |
| `nextRunAt` | string, optional | The earliest time the next prompt is sent (the interval after the start of the last run), absent before the first run; it waits further while the workspace is busy. |
| `limitCheckAt` | string, optional | While the loop waits because the usage limit stopped its prompts, the time it looks at the limit again (every 15 minutes); absent otherwise. |
| `isRunning` | bool | `true` while a run is under way, from when its prompt is sent (also while it waits for the local AI) until its work is over. |
| `runs` | number | How many runs the loop has made; a run the limit cut off is not counted. |

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
| `isDelegationAllowed` | bool | Whether the AI may delegate tasks to paired servers. It belongs to the workspace, not the session. |
| `instructions` | string, optional | Standing instructions for the AI. |
| `reviewPrompt`, `expandPrompt` | string, optional | The prompts the workspace's review and expand buttons send instead of the ones of the environment; omitted (or blank when setting) means the environment's. They belong to the workspace, not to the session. |
| `statusMinutes` | number, optional | The status interval of the workspace itself, 1 to 1440; omitted (or omitted when setting) means the environment's. It belongs to the workspace, not to the session. |
| `sessionFiles`, `sessionSetupScript` | string, optional | As on the workspace; omitted (or blank when setting) means none. Ignored when setting a side session's settings. |
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
| `ollamaCommand` | string | The command that starts Ollama (`ollama` by default; blank sets the default), which the server runs itself when `localServer` is on this machine and nothing answers there. |
| `localOptimizeContext` | bool | `true` to start that Ollama with `OLLAMA_KV_CACHE_TYPE=q4_0` and `OLLAMA_FLASH_ATTENTION=1`. It always has `OLLAMA_LOAD_TIMEOUT=0`. |
| `localServerNote` | string, optional | Read only: what the server can say about the Ollama of the local models (started by it with these settings, already running outside it so its variables cannot be set, not on this machine, or not startable). Never stored and ignored when setting. |
| `ollamaServiceControl` | bool, optional | Read only: `true` when Ollama of the local models is a systemd service (`ollama.service`) on the machine of the server at a loopback address, which `ConfigureOllamaService` can change. Clients then confirm a change of `localOptimizeContext` through that command and otherwise just send the setting. Never stored and ignored when setting. |
| `ollamaServiceNeedsPassword` | bool, optional | Read only: `true` when `sudo` on the server asks for a password, so the client has to ask for one. Never stored and ignored when setting. |
| `localModels` | capability names | The local model for each capability (`fast`, `balanced`, `strong`, `max`); all four or none. |
| `localContexts` | capability contexts, optional | The context size in tokens for each capability, each optional. |
| `imageGeneration` | string | `None`, `OpenAi` or `Local`. |
| `localImageServer` | string | Address of the Stable Diffusion server. |
| `localImageModels` | capability names | The checkpoint for each capability; all four when `imageGeneration` is `Local`. |
| `localWorkMode` | string | `Yield` (default: one local AI session at a time) or `Simultaneous`. |
| `delegatedTaskLimit` | number | Delegated tasks at once under `Simultaneous`, 1 to 16 (default 1). |
| `statusMinutes` | number | How often, in minutes (1 to 1440, default 5), the AI is told to report its status in every workspace that has no interval of its own. |
| `claudeMaxWindowPercent`, `codexMaxWindowPercent` | number | The share of a usage window of Claude Code and of OpenAI Codex, the providers that report usage windows, from 1 to 100 (default 95, fractions allowed, one number for each provider), at which the server tells the AI of a workspace that uses it to pause. There is no single maximum, and the local models, which report none, have no such setting; omitted by an older server and then 95. |
| `reviewPrompt`, `expandPrompt` | string | The prompts the review and expand buttons send in every workspace that has none of its own; trimmed, and a blank one is replaced by the default (`review workspace and fix any problems or inconsistencies`, `expand upon content in workspace in areas that make sense`). |
| `newWorkspaces` | workspace defaults | What the AI session of a new workspace starts with: `capability`, `effort`, `isShellAllowed`, `isSystemAccessAllowed`, `isWebAllowed`, `isDelegationAllowed`, `isImageGenerationAllowed` (which only takes effect while `imageGeneration` is set) and `instructions`, a string where empty means none (the wire leaves out what is null, so none is not null). It starts balanced and high, with the web allowed, the shell, files outside the workspace and image generation not allowed, and no instructions. `showWork` says whether the chat of a new workspace shows the AI's work (tool calls and results, dimmed), off by default. A workspace keeps the values it was created with whatever this says later. |

**resources** (`GetResources`)

| Property | Type | Meaning |
| --- | --- | --- |
| `supported` | boolean | `false` when the server cannot measure (it is not on Linux); everything else is then empty. |
| `machine` | machine, optional | The whole computer. |
| `workspaces` | array of resource workspace | The workspaces that work or still have processes running, the one taking the most memory first. |
| `server` | resource usage | The server's own process. |
| `other` | resource usage | Processes the server started that belong to no workspace. |

**machine** (`resources.machine`): `totalMemory`, `availableMemory` (what can be taken without swapping), `totalSwap` and `freeSwap` in bytes, `cores`, and `cpu` (how busy all cores together are, 0 to 100, omitted until measured twice).

**resource workspace** (items of `resources.workspaces`): `id` (the main session), `name`, `isWorking` (any session works or runs a script), `total` (resource usage) and `sessions`, the ones that work or have processes, the main session first, each with `id`, `name` (`Main` for the main session), `isWorking` and `usage` (resource usage).

**resource usage**

| Property | Type | Meaning |
| --- | --- | --- |
| `memory` | number | Bytes, with shared memory divided between those sharing it. |
| `cpu` | number, optional | Share of the whole computer's processor, 0 to 100; omitted until measured twice. |
| `processes` | number | How many processes. |
| `heaviest` | array of process | The ones taking the most memory plus any keeping the processor busy, the one taking the most memory first; each with `id`, `command` (shortened when long), `memory` and optional `cpu`. |

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

**saved input** (items of `savedInputs`, and the `savedInput` of `TakeSavedInput`)

| Property | Type | Meaning |
| --- | --- | --- |
| `id` | GUID | What `TakeSavedInput` takes it by. |
| `text` | string | The text that was in the chat input; empty while the entry is being converted from speech or when that failed. |
| `time` | string | When it was saved (for a recording, when it was sent), ISO 8601 UTC. |
| `isConverting` | bool, optional | `true` while the server turns the recording of the entry into text; omitted for `false`. |
| `isQueued` | bool, optional | `true` for a queued input, which the server sends by itself; omitted for `false`. `TakeSavedInput` answers it without this. |
| `isPaused` | bool, optional | `true` for a queued input that waits for `ResumeQueue`, for work to go well again or for another input to be queued; omitted for `false`. |
| `autoCommit` | bool | Whether the work the input starts is committed; `true` unless turned off. |
| `error` | string, optional | Why the recording could not be turned into text; omitted otherwise. |

**error** (items of `errors`, and the `errors` of `ErrorsChanged` events)

| Property | Type | Meaning |
| --- | --- | --- |
| `time` | string | When the error happened, ISO 8601 UTC. |
| `message` | string | The message the `Failed` event carried. |

**message** (items of `history.messages`, and carried by `MessageAdded` events)

| Property | Type | Meaning |
| --- | --- | --- |
| `role` | string | Author: `User` (the person using the client) or `Assistant` (the AI). |
| `text` | string | The message text. |
| `isWork` | bool, optional | `true` for work the AI did on the way, a tool call as one short line made by the server from the tool and its arguments (`Read src/Program.cs`, `Run dotnet build`, `Write notes.md (12 lines)`; a tool without a wording of its own is its name with up to three short arguments), its result as a count or one short line that never carries the content (`→ 120 lines`, `→ 3 matches`, `→ Failed: no such file`, `→ No output`), or the reasoning of the AI (also `isThought`). Omitted for what the user or the AI said. Always `Assistant`. Published as `MessageAdded` events whatever the workspace shows; a client that does not show the work ignores them. |
| `isThought` | bool, optional | `true` for the reasoning of the AI, always work too (so only shown with the work): the text it reasoned, cut at 20,000 characters, with the time it started, stored and published when the reasoning ends in the place it happened. A client that streamed `MessageDelta` pieces with `thought` replaces them with this message. Omitted for everything else. |
| `time` | string, optional | When the message was written, ISO 8601 UTC; the time the server published it (a message taken over from an older server's conversation has the time the conversation tool stored, absent when it stored none). |

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
| `truncated` | bool | `true` when `text` is only the start of the file. |

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
| `busy` | bool | `true` while a git action that the user waits for runs in the workspace, whichever client started it; the automatic fetch and pull do not count. |
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
| `hasMore` | bool | `true` when older commits exist beyond the chunk. |

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
| `kind` | string | `Branch`, `Remote`, `Tag` or `Session`: the branch of a side session, named as the session; `isCurrent` marks it in that session's own history. |
| `isCurrent` | bool | `true` for the checked out branch. |

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
| `isTask` | bool | `true` for a `.task` file. |
| `taskPath` | string, optional | The task that runs this script. |
| `scripts` | array of string, optional | The scripts a task runs, in order. |
| `output` | string, optional | Everything written so far, the earliest part cut beyond 500000 characters; omitted in events. |
| `trimmed` | bool | `true` when the earliest part of the output was cut. |

**git diff** (`GetGitDiff`)

| Property | Type | Meaning |
| --- | --- | --- |
| `path` | string | The file. |
| `lines` | array of line | The whole file in order, added and removed lines mixed in; empty for a binary file. |
| `isBinary` | bool | `true` for a binary file. |
| `truncated` | bool | `true` when `lines` is only the start (5000 lines). |

**line** (item of `gitDiff.lines`)

| Property | Type | Meaning |
| --- | --- | --- |
| `kind` | string | `Unchanged`, `Added` or `Removed`. |
| `text` | string | The line without its terminator. |
| `oldNumber` | number, optional | The line number before the change; absent for an added line. |
| `newNumber` | number, optional | The line number after the change; absent for a removed line. |

**queued work** (`queue`)

| Property | Type | Meaning |
| --- | --- | --- |
| `position` | number | Its place in the queue, 1 being next. |
| `text` | string | The waiting prompt, with its attachment lines at the end; empty for other waiting work such as a commit message. |
| `running` | array of string | The names of the workspaces whose local AI work runs. |
| `delegated` | bool | Whether it is a delegated task. |

**swarm status** (`swarm`)

| Property | Type | Meaning |
| --- | --- | --- |
| `server` | swarm server | `name`, `fingerprint` (read only), `acceptDelegatedWork`, `delegatedFolder` (empty: inside the data folder), `maxTasksPerJob` and `driveFolder`. |
| `peers` | array of swarm peer | The paired servers. |
| `drivePairings` | array | Pairings on the drive to confirm: `id`, `name`, `fingerprint` and `kind` (`Invitation` or `Acceptance`). |
| `pairingCode` | object, optional | The working pairing code: `code` and `expiresAt`. |
| `tasks` | array of swarm task | Outgoing and incoming tasks, newest first. |
| `report` | string, optional | What the command did, to show. |

**swarm peer**

| Property | Type | Meaning |
| --- | --- | --- |
| `id`, `name`, `fingerprint` | string | The server; the identifier and fingerprint come from its keys. |
| `sendWork`, `acceptWork` | bool | Whether tasks are delegated to it and its tasks accepted. |
| `mode` | string | `GitRemote`, `Direct` or `AirGapped`. |
| `address` | string | The `host:port` this server connects to; empty to wait for it. |
| `permissions` | object | `isShellAllowed`, `isSystemAccessAllowed`, `isWebAllowed`, `isImageGenerationAllowed` for its tasks. |
| `capabilities` | array | From its last profile: `capability`, `model`, optional `context`. Read only, as are the rest. |
| `acceptsOurWork`, `freeSlots` | bool, number | What its profile says about this server's tasks. |
| `profileAt`, `lastSeen` | timestamp, optional | When its profile was made and anything last arrived. |
| `isConnected` | bool | Whether a connection is open. |
| `problem` | string, optional | Why the last connection or delivery failed. |

**swarm task** (items of `tasks`)

| Property | Type | Meaning |
| --- | --- | --- |
| `id`, `jobId` | GUID | The task and its job. |
| `isIncoming` | bool | Whether another server delegated it here. |
| `title`, `peerName` | string | The title and the other server. |
| `workspaceId`, `workspaceName` | GUID (optional), string | The workspace delegating it, or for an incoming one the workspace running it, which it only has while it has one. |
| `capability` | string | The capability it needs. |
| `state` | string | `Waiting`, `Pending`, `InTransit`, `Queued`, `Running`, `Done`, `Failed`, `Cancelled` or `Lost`. |
| `error`, `summary`, `branch` | string, optional | Why it did not succeed, the start of the report and the result branch. |
| `isApplied`, `isManual` | bool | Whether its changes were applied and whether the user delegated it. |
| `createdAt`, `updatedAt` | timestamp | When it was made and last changed. |

**swarm task detail** (`swarmTask`): `task` (as above), `instructions`, `expectedResult`, optional `report` and `messages` (the newest part of the worker's conversation).

**swarm task request** (`task` of `DelegateTask`): `title`, `instructions`, optional `expectedResult`, `capability`, optional `peerId` (omitted: any networked worker) and `dependsOn` (identifiers of earlier tasks of the workspace).

## Events

The server pushes events to every authenticated connection, as responses with `id` zero and the `event` property set. Client request ids start at one, so zero never collides. Events of one connection are delivered in order.

**event**

| Property | Type | Meaning |
| --- | --- | --- |
| `kind` | string | `MessageAdded`, `WorkingChanged`, `FilesChanged`, `GitChanged`, `Failed`, `ScriptChanged`, `ScriptOutput`, `ScriptRemoved`, `StateChanged`, `WorkspacesChanged`, `LoopChanged`, `SavedInputsChanged`, `ErrorsChanged`, `ShowWorkChanged`, `LimitPauseChanged`, `MessageDelta`, `QueueChanged`, `SwarmChanged`, `SwarmTaskChanged` or `AutoCommitChanged`. |
| `workspaceId` | GUID | The workspace concerned; the empty GUID for `WorkspacesChanged`, which is about all of them, and for `SwarmChanged` and `SwarmTaskChanged`. |
| `message` | message | `MessageAdded`: the message, from the user or the AI. |
| `working` | bool | `WorkingChanged`: whether the AI is working now. |
| `limitPause` | limit pause | `LimitPauseChanged`: the automatic pause the workspace is in now, sent when it starts and each time the server reads the windows again and they are still past the maximum; omitted when the pause ended. |
| `path` | string | `FilesChanged`: a folder relative to the workspace folder whose entries or their contents changed (empty for the workspace folder itself); one event per folder. `ScriptOutput` and `ScriptRemoved`: the file of the run. |
| `script` | script run | `ScriptChanged`: the run that started or changed state, without its output. |
| `state` | string | `StateChanged`: the state the workspace entered: `Ready`, `Working`, `Managing`, `Stopping` or `Canceling`. |
| `loop` | loop, optional | `LoopChanged`: the loop that runs in the workspace, absent once it ended. |
| `savedInputs` | array of saved input | `SavedInputsChanged`: the saved inputs of the workspace now, in the order of `GetSavedInputs`. |
| `autoCommit` | string | `AutoCommitChanged`: `Off`, `On` or `Committing`. `Committing` is sent before the `WorkingChanged` that says the AI stopped, and the workspace goes from `Working` to `Managing` without being `Ready` in between, so a client counts a committing workspace as git working. |
| `showWork` | bool | `ShowWorkChanged`: whether the workspace shows the AI's work now; a client reads the history again, which includes or leaves out the work messages. |
| `errors` | array of error | `ErrorsChanged`: the errors of the workspace now, the newest first; sent right after the `Failed` event of the same workspace, and only for that workspace. |
| `text` | string | `FilesChanged`: `all` when too much changed to say where, so everything is read again. `QueueChanged`: `dropped` when a waiting prompt was removed or could not start, as opposed to starting, so a client that sent it stops waiting for its result. `ScriptOutput`: what the run wrote since the last event. `LoopChanged`: why a loop ended by itself. `MessageDelta`: the next piece of the message the AI is writing, in order; only sent for a workspace that shows the AI's work, about every 100 milliseconds with what was written meanwhile. The finished message follows as `MessageAdded` (for the reasoning, the message with `isThought`), and a client puts it in the place of the pieces; pieces of a message that never finishes (the AI stopped or failed) are dropped, and nothing of them is stored. |
| `thought` | bool, optional | `MessageDelta`: `true` when the piece is the reasoning of the AI and not its answer; omitted for the answer. |
| `error` | string | `Failed`: what went wrong, usually why the AI could not do its work. |
| `isNotice` | bool, optional | `Failed`: `true` when what went wrong is beside the work of the workspace, which goes on: an integration that could not start, or files of a removed side session left on disk. A loop does not end for it. |
| `queue` | queued work, optional | `QueueChanged`: what of the workspace waits now, omitted once nothing does; sent for every waiting workspace whenever the queue changes, since positions move. |
| `taskId` | GUID | `SwarmTaskChanged`: the delegated task whose state or conversation changed. |
| `time` | string (ISO 8601) | `Failed`: when the failure happened, taken by the server so every client shows the same time (the error history records the same one). A client shows the failure as the error line of the chat with this time and clears it when a user `MessageAdded` of the workspace arrives, whichever client sent that input. |

`ScriptChanged` is pushed when a run starts and each time its state changes, a task and each of its scripts separately; `ScriptOutput` carries what a running script wrote, batched about every 100 milliseconds, in order with the state events; `ScriptRemoved` says a run was forgotten or replaced by a new run of the same file. A client reads `GetScripts` after subscribing and applies the events it received meanwhile.

`WorkspacesChanged` says the workspaces or groups changed on the server (one was created, deleted, renamed, moved, reordered or configured, by any client); it carries no details, so a client lists the workspaces again with `ListWorkspaces`, which it also does after reconnecting. A new AI session being attached to a workspace is not announced. The client keeps what it has open: the focused workspace stays open untouched unless it was deleted (the view clears and the drawer opens) or its folder or AI settings changed (it is reopened). A side session on screen that is removed or starts to be integrated gives way to its main session.

`SwarmChanged` says the swarm's settings, paired servers or tasks changed, and `SwarmTaskChanged` that one task did; both have the empty workspace GUID and carry no details, so a client that shows them reads them again.

`GitChanged` says a git action started or finished in the workspace (the automatic fetch and pull only say it when they changed something), so a client reads the git state again; the server also sends it when the head, the index or the refs of the repository change outside Auto Remote, and a client does the same when a `FilesChanged` event arrives, when a `WorkingChanged` event says the AI stopped, and after its own git actions.

A client that opens a workspace subscribes first and then reads the newest history chunk, holding the events that arrive meanwhile: the leading `MessageAdded` events whose messages the chunk already ends with are duplicates and are dropped, the rest are appended, and later events are appended as they come. A client that may have missed events reads the newest chunk again the same way. `WorkingChanged` is not guaranteed to be balanced; the last one received is the truth.

## Requests are multiplexed

Every `RemoteRequest` carries a client-chosen `id` that the matching `RemoteResponse` echoes. The server handles each request on its own task and serializes only the writes, and the client keeps a table of pending requests fed by a single read loop. This lets slow requests such as renaming or deleting a large folder run without freezing the file browser and workspace list. The trade-off is that the client must tolerate responses and events in any order relative to each other.

## Errors

A request the server understands but refuses (missing folder, name taken, workspace busy) comes back as a normal response with `error` set, and the client raises it as `RemoteException`. Only a malformed line or a broken socket closes the connection. When the connection drops, every pending request fails together with an `IOException`.

## Disconnects do not cancel the AI's work

The AI keeps working after a client leaves, because the server records every message as it happens. A reconnecting client sees the conversation by reading the history and learns whether the AI is still working from it. A connection's file watch ends with the connection. Events are not replayed to a connection that was away, so a client that loses its connection reopens it by itself (retrying after one second, then two, four, eight and every fifteen), replays its file watch, and reads again everything its screens show: the newest messages and whether the AI works, the scripts, the git state, the files and the workspace state. A mobile or browser client that was in the background for a few seconds does not trust its connection, which may have died without telling, and replaces it the same way when it comes back.
