# Side sessions

A workspace can be worked by several AI sessions at once: the main session, which is the workspace itself, and any number of side sessions. Each side session has its own copy of the files, its own chat, scripts and loops, and its AI starts with what main's AI knew. There is no limit on how many exist; the user decides. A side session ends in one of two ways: it is deleted, which throws its work away, or it is integrated, which has main's AI bring its work into main. Both are permanent. There is no undo, backup or recovery.

To the server a side session is a hidden child workspace. It has its own identifier, so prompts, files, git, scripts, loops, saved inputs and events work on it as on any workspace, and clients get it in the catalog's `sessions` rather than among the workspaces.

## Where the files are

A side session is a git worktree on a branch of its own, so the workspace must be the top folder of a git repository with at least one commit. Its folder is `<root>/<workspace id>/<session id>`, where the root is `sessions` in the data folder unless the config names another. It is outside the workspace on purpose: tools that walk the workspace (builds, searches, file watchers, the AI itself) never see the copies. Changing the root only affects sessions made afterwards, since each one records its own. The branch is named from the session's identifier, so renaming a session changes only its label. Renaming or moving the workspace repairs the links between the repository and its worktrees.

It starts from one of two things, captured when it is made:

- **Current Commit**: the files of main's current commit.
- **Current Files**: main's files as they are, uncommitted changes and new files included. They stay uncommitted in the session.

Clients preselect Current Commit while main is working (its AI or a git action), so that half finished work is left behind, and Current Files otherwise. Either way the server records the starting files as a tree, kept alive by a ref of its own. That snapshot is what "changes since session start" and integration compare with, so what the session commits is covered too. It is built in a copy of the index, which leaves the real one untouched.

Files git ignores are not copied. A workspace can list files and folders to copy into new sessions and name a setup script to run in them. A first task given when the session is made is sent once both succeed, unless a usage window is at or past the maximum, since going past it takes the user's word. When it is not sent, the session is still usable, and it keeps the reason and the unsent task until the user prompts it.

## What the AI knows

The session's AI conversation is a fork of main's that holds only main's finished exchanges: work main is in the middle of is left out, and the side's AI could otherwise believe it should continue it. A main session without any gives the side a fresh conversation. The fork is closed and loaded again for the side, so it takes over none of the tools that act for main. It starts with main's AI settings.

The chat is separate from the conversation and starts empty. An empty log is stored when the session is made, and it is never filled from the AI conversation, even if the log is lost, so main's history can never show up as the side's. Standing instructions, which are not part of the chat, tell the AI where it works, what it started from, that the earlier conversation is background it must not continue, and what git it must not do.

## Git

A side session can read history and changes, commit and discard. The server refuses branch switching, merging, rebasing, squashing, pulling, remote and tag changes there, and syncing does nothing. Its commits are never pushed. In main, git actions refuse the branches of side sessions, and history shows each one as its own kind of ref under the session's name, from the moment the session exists. The AI and scripts are only told these rules; the shell is not a sandbox.

## Integration

Integration is done by main's AI rather than by git. The AI can adapt the work to whatever main has become, handle conflicts as it goes, and leave main with ordinary uncommitted changes: no commit, merge or branch is left behind. It runs unattended, so the AI decides conflicts itself, and nothing checks its result. Mixing with main's other uncommitted work is accepted; a user who minds waits until main is done.

1. It can start once the session has no AI work, loop, queued work, queued input, commit still to come, running script or delegated task under way (including results its AI has yet to be told), and is not paused at the usage limit. From then on nothing in the server can prompt it: reports of delegated tasks, the resuming after a usage pause and tasks applied to it are all refused. The session is locked as `Integrating`; a client showing it switches to main.
2. A fork of the side's AI writes a short handoff about the side's own work. The server writes it, the list of changed files and the diff against the starting snapshot into a folder beside the session's.
3. Main's AI is given read-only access, for its file tools, to the session's folder and that folder. The access applies at once, also to work already under way, and is given again whenever main's AI session is reopened.
4. The request joins main's current work, or starts new work when main is idle. Several integrations are each sent at once and main handles them together. A request waits while main cannot take input (a git action, a usage limit pause, the local AI queue), and goes before the next prompt of a loop.
5. Main's AI calls `finish_integration` when it is done. From then on, the work main is doing can be stopped but not cancelled, because reverting it would throw away the only copy of the session's work. The server then removes the access and everything of the session: folder, branch, starting snapshot, chat, AI conversation, saved inputs, records of delegated tasks and integration files. What remains is main's uncommitted changes and the AI's report in main's chat.

The finish tool is on main sessions at all times, not only during an integration, because an AI only sees a change of its tools from its next prompt on, and a request that joins running work would arrive without it. It does nothing for an identifier that is not under way. A delegated workspace is not offered it.

While a session is being integrated, its workspace cannot be renamed or moved, since the request names its folder. A workspace with side sessions can only be moved to a folder holding the same repository.

A request that ends without a finish (the turn ended, an error, a server restart) is given once more when main can take it. After that, or once the user stops main's work, it only goes along with whatever main does next. The session stays `Integrating` until it is finished or cancelled. When an integration cannot even be prepared, the session is `Ready` again with the reason, and main reports the failure.

**Integrate When Finished** is a switch per side session, off by default. The side's AI has a tool to mark its task complete; when the switch is on, integration starts once nothing of the session runs. Prompting the session again takes the mark back, and so does stopping or cancelling its work, a failure of it, and a server restart, since the work that made the mark may not have ended well.

## Cancelling

An integration can be cancelled until the finish succeeds. The session is unlocked first, which makes a late finish do nothing, so a cancellation racing the AI leaves the session either whole or gone. Then the access and integration files are removed, a request still waiting is withdrawn and never sent again, and when main's AI already has it and is working, it is told to stop; one that is on its way while it is cancelled is followed by the same message if it still arrives. The rest of main's work goes on. Nothing is undone: main may hold some of the session's changes, which is the user's to clean up, and the AI may make a few more edits before it stops. Integrating again is no different from the first time.

## Deleting, and what protects other files

Deleting a session ends its work and removes all of it, after the client has asked. An integrating session must be cancelled first. A session still being made or set up is first waited for to end, so nothing is added to it afterwards. Deleting the workspace deletes its sessions and their branches even when the workspace folder is kept; a deletion that is refused, such as of a folder too important to delete, is refused before any of them goes. A session that was still being made or removed when the server stopped is removed when it starts.

Removal deletes only what the server recorded making. The recorded folder must be exactly where the server would put one under the recorded root, still resolve to a place inside that root, hold no workspace, other session or home folder, and hold no git repository of its own; links are never followed out. A damaged record or a changed setting therefore leaves files in place and reports it, rather than deleting a workspace or an unrelated folder. The report on the main workspace is a notice, which does not end its loop. A side session whose main workspace cannot be listed is not listed either, until it can be again.

## Clients

All clients show the sessions in a list that opens from the workspace's title, main first. The header names a side session as `workspace / session` with a marker, and so do notifications. The drawer keeps listing workspaces only, and one counts as working when any of its sessions does. A session that is being set up or integrated cannot be opened, and a removed one takes its notification with it. A first task given from a client is followed to its end whatever is on screen. "Changes Since Session Start" is a mode of the file tree. The chat of a side session says that it has main's context from session creation, and shows a failed setup there, with the unsent task put back into the message box.
