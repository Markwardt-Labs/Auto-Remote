# Usage

Runnable examples of `IAppInfo` in different situations.

## Read the product identity

```csharp
IAppInfo info = new IAppInfo.Factory().Create(typeof(Program).Assembly);
Console.WriteLine($"{info.Name} {info.Version}");
```

The version comes from the assembly, so a published build reports the release version it was built with.

## Run the server with a config file

`config.json` (or the path passed as the first argument):

```json
{
  "port": 52678,
  "webPort": 52679,
  "key": "change-me",
  "dataFolder": "/home/you/.local/share/AutoRemote",
  "defaultFolder": "/home/you",
  "claudeCommand": "claude",
  "claudeArguments": [],
  "codexCommand": "codex",
  "codexArguments": []
}
```

```
Markwardt.AutoRemote.Server config.json
```

Only `key` is required; the other values shown are the defaults (`defaultFolder` defaults to the server user's home folder, and the example shows what that is on one machine). `port` (default 52678) is for the mobile and desktop clients (TLS over TCP, the mobile and desktop clients) and `webPort` (default 52679) for the web client (TLS WebSocket); both must be 1 to 65535 and different, and the key non-empty, otherwise the server prints the problem and exits with code 1. `dataFolder` is where the server stores workspaces, the groups they are filed in (groups can contain groups) and conversations (a `history` folder); it is created when missing and defaults to the `AutoRemote` folder of the server user's application data. `defaultFolder` must be an existing folder: it is where a client starts when browsing for a new workspace's folder. `claudeCommand` and `codexCommand` must resolve to the Claude Code and Codex executables on the server's machine (only for the services in use), and `claudeArguments` and `codexArguments` are added to every run of that service. Prompts always run without permission prompts (`--dangerously-skip-permissions` for Claude Code, `--dangerously-bypass-approvals-and-sandbox` for Codex), since they run unattended and cannot ask.

All connections use TLS. The server presents the PFX named by `certificateFile` (with `certificatePassword` if it has one); without it, the server creates a self-signed certificate as `certificate.pfx` in the data folder on first start and prints its SHA-256 fingerprint on every start. A client connects (TCP on `port`, WebSocket on `webPort`) and sends its access key as a JSON line. The server replies with a JSON line saying whether it was accepted, and keeps the connection open or closes it. Keys are compared in constant time, the line is limited to 1024 bytes, and a client has 5 seconds to send it.

The server writes a timestamped line to its console for each connection: when it arrives, when TLS is established, whether the client was accepted or why it was refused (wrong key, no handshake line within 5 seconds, a connection that closed or sent something else, no WebSocket upgrade), when it disconnects and how long it was connected, and any error that ended it. Keys are never logged. The clients log the same stages from their side (connecting, TCP connected, TLS established with the certificate fingerprint, accepted, or the stage that failed and why, and a lost connection) to the console: the terminal on desktop, logcat on Android (which `Scripts/Mobile/Client.cs` streams) and the browser console on the web.

## Talk to the server from code

```csharp
IServerConnection.IFactory factory = new IServerConnection.Factory(new IMessageChannel.Factory());
await using IServerConnection connection = await factory.Connect(new ConnectionSettings { Host = "192.168.1.20", Port = 52678, AccessKey = "change-me" }, cancellation);

FolderListing start = await connection.ListFolders(null, cancellation);
FolderListing project = await connection.CreateFolder(start.Path, "my-project", cancellation);
WorkspaceInfo workspace = await connection.CreateWorkspace(project.Path, cancellation);
await connection.ConfigureWorkspace(workspace.Id, AiService.ClaudeCode, AiModel.Opus, AiEffort.High, cancellation);

WorkspaceGroup group = await connection.CreateGroup("Clients", null, cancellation);
WorkspaceGroup nested = await connection.CreateGroup("Acme", group.Id, cancellation);
await connection.MoveWorkspace(workspace.Id, nested.Id, cancellation);

connection.EventReceived += remoteEvent =>
{
    if (remoteEvent.Kind == RemoteEventKind.MessageAdded)
    {
        Console.WriteLine($"{remoteEvent.Message?.Role}: {remoteEvent.Message?.Text}");
    }
};

await connection.Prompt(workspace.Id, "Create a hello world program", cancellation);
await connection.Prompt(workspace.Id, "Make it print in French", cancellation);
ChatHistory newest = await connection.GetHistory(workspace.Id, null, 30, cancellation);
ChatHistory older = await connection.GetHistory(workspace.Id, newest.Start, 30, cancellation);
await connection.WatchFiles(workspace.Id, [string.Empty], cancellation);
WorkspaceFiles files = await connection.ListFiles(workspace.Id, string.Empty, cancellation);
```

The workspace is named `my-project` after its folder, and the first prompt starts an AI session attached to it. The groups only organize workspaces in Auto Remote and create nothing on disk. `Prompt` returns at once; the conversation arrives as events as the AI writes it, and a prompt sent while the AI is working joins that work. History is read in chunks, newest first. After `WatchFiles`, `FilesChanged` events report changes to those folders (each call replaces the folders watched before). A refusal such as an unknown folder throws `RemoteException`.

## Use the client app

The Android app (`Client/Mobile/`) and the desktop app (`Client/Desktop/`, a window that opens maximized and runs on Windows, Linux and macOS) are the same client and use the same TCP port; the desktop app keeps its settings in the user's application data unless started with `--data-folder <path>`. Everything below applies to both.

1. On first launch, enter the server's host, TCP port and access key. They are saved on the device, the client accepts whatever certificate the server presents, and later launches reconnect straight to the workspaces. Everything else, the workspaces, groups, settings and conversations, lives on the server.
2. The menu button opens the drawer listing workspaces and groups as a tree. Tap a workspace to focus it, tap a group to collapse or expand it. A group has buttons to add a group inside it (+), rename it (pencil), move it (arrow) and delete it (cross); a workspace has a move button. Moving asks for the group to put it in, or the top level; a group cannot go into itself or anything inside it. Deleting a group keeps what is in it and moves that up a level. New workspace, Clone workspace, New group and Change server are at the bottom.
3. A new workspace is a folder on the server: browse from the server's default folder, create folders as needed, then start the workspace in the folder you are viewing. The workspace takes the folder's name. Cloning is the same screen with a repository URL and an optional folder name added: the server clones the repository into a new folder inside the folder you pick and starts a workspace in it. Groups in the drawer are different: they only organize workspaces in Auto Remote and are not created on the server's disk.
4. A focused workspace has a gear at the top right that opens its settings (press it again to go back). The mobile client shows chat and files as two tabs along the bottom, plus a History tab when the workspace is a git repository; the desktop and web clients have no tabs and show the file system, the content of the selected file and the chat (with a History tab beside Chat when the workspace is a git repository) as three panels side by side with draggable separators between them, the file system much narrower (a seventh of the width) and the other two equal at first. The message box has no background or border, only the send button beside it. Chat is a live conversation: the AI's messages appear as it writes them, you can keep sending messages while it works and they join that work, and the newest messages load first with older ones loading as you scroll to the top. Files shows the workspace folder as a tree like the Auto Dev app, names only: tap a folder to open or close it in place (a button closes every folder) and tap a file to preview it. Double-tap a folder to make it the root of the tree, and the up arrow at the top goes back to its parent. The tree updates by itself when an open folder changes, for example while the AI edits files. A markdown file (`.md`, `.markdown`) is shown rendered; other files are shown as monospaced text with line numbers, colored by syntax when the language is recognized (for example C#, JSON, YAML, XML and project files, JavaScript, TypeScript, Python, shell, SQL, CSS, HTML, INI, `.env`, CSV and Dockerfiles). Text files have a find bar (the magnifier button or Ctrl+F) with a match count, match case and whole word options, and next and previous match (Enter and Shift+Enter) that marks the matches in the text. Settings holds the AI service, model and effort (Claude Code with Sonnet on high by default), and Rename and Delete. Rename renames the workspace's folder on the server. Delete removes the workspace, and by default its folder and everything in it; untick the box to keep the folder.
5. A workspace whose folder is a git repository shows its git state, and every git action runs on the server. At the top of the file system sits the version section: the branch (or the tag or a note when the head is not on a branch), a tag at the head, the head commit's message and abbreviated hash, and an asterisk with an accent border while there are uncommitted changes. Tapping it offers Commit (stages everything, asks for a message and pushes the branch when there is a remote), Reset (discards every uncommitted change after a confirmation), Branch (creates a branch at the head, checks it out and pushes it), Tag (annotated, pushed) and Remote. The first time a commit, tag or history rewrite needs it and git does not know who you are, the client asks for a name and email and saves them in the server user's global git configuration. The files in the tree are colored like Auto Dev: new files green, changed files and the folders containing changes yellow, ignored files and folders dim. The changes button next to Collapse All switches the tree to Changes Mode, a tree of only the changed files (deleted ones in red); tapping a file there shows its diff in the content panel, the whole file with added and removed lines marked and the line numbers before and after. The History tab lists the commits of every branch newest first with branch and tag badges, the author, time and hash; tapping a commit lists the files it changed as a tree, and tapping one of those shows its diff in the content panel. Older commits load on request, and a Fetch button next to Refresh fetches the remote. Tapping a branch or tag badge on a commit opens its menu: Checkout (a branch, a tag, or with the commit's own "Check out this commit" button a plain commit, which detaches the head; changes that are not committed are discarded after a confirmation that says so), Squash, Rebase and Merge between the badge's branch and the checked out branch in either direction, and Delete (locally and on the remote, after a confirmation). Squash and a rebase or merge of a branch with several commits ask for the message of the one commit that replaces them, defaulting to the tip's message; rebase and merge need a clean working tree and tell you when there is nothing to do. A rebase or merge that conflicts is handed to the workspace's AI: a "Working…" panel with Cancel and undo covers the screen while it works, the chat shows what it was asked and does, and when it cannot resolve the conflicts everything is put back and you are told. A merge fast-forwards the target, pushes it and deletes the merged branch everywhere. While a workspace is focused its checked out branch is fetched and pulled every minute and whenever the history opens (changes that are not committed are stashed and restored around the pull, with the AI resolving conflicts of the restore). Everything refreshes by itself when files change and when the AI finishes working. Commit, Reset and Branch are refused while the AI is working in the workspace.
6. A git remote icon sits at the top right of a focused workspace, left of the usage icon: it sets the `origin` remote of the workspace, turning a folder that is not a repository into one first, or repoints the remote of an existing repository (the field starts with the current URL).
7. While the AI is working in the focused workspace, the header bar with the workspace title turns blue, on every tab and in every layout.
8. Next to the gear is a usage icon, colored by how fast the session limit of the workspace's AI service is being used: green when well under an even pace, blue when under, the normal color when even, orange when over and red when well over or used up. Tapping it opens the session and week usage with their reset times. The server reads Claude Code's limits from its `/usage` report and Codex's from the newest Codex session recorded under `~/.codex` (so they are as fresh as the last Codex run, and a window that already reset shows 0%), reuses a result for a minute, and clients ask once a minute.

## Use the web client

`Client/Web/index.html` is a single static file with the same screens and behavior as the client apps: serve it from any static host such as GitHub Pages (or open it from disk) and enter the server's host, web port and access key. It always connects with `wss`. The settings are saved in the browser. With a self-signed server certificate, open `https://host:webPort` in a tab once and accept the certificate; the connect screen links there. In a desktop browser Enter sends a message and Shift+Enter adds a line.
