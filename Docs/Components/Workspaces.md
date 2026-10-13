# Workspaces

A workspace is a folder on the server's machine that the user works in. An AI session is a conversation with the workspace's AI service (Claude Code or OpenAI Codex) that the server creates and attaches to a workspace the first time it is prompted. The two are separate: the workspace is the durable thing the user sees and manages, the AI session is replaceable.

## A workspace is its folder

A workspace stores the folder path, its AI settings, the attached AI session and the group it is filed in (if any); its display name is always the folder's name. Renaming a workspace therefore renames the folder on disk, and the server refuses when another workspace lives inside that folder or the target name exists. Creating a workspace requires an existing folder (the client can create one while browsing, starting from the config's `defaultFolder`), and one folder hosts at most one workspace. A workspace can also be cloned: the server runs `git clone` into a new folder inside an existing one and creates the workspace there.

## Deleting

Deleting removes the workspace and its stored conversation. Deleting the folder as well is the default and recursive, so the server refuses it for a file system root, the server user's home folder or any ancestor of it, and any folder that contains or sits inside another workspace. Rename and delete are refused unless the workspace is ready.

## AI sessions and the live conversation

For Claude Code, the first prompt to an idle workspace starts a non-interactive process with the workspace folder as its working directory, streaming JSON on both stdin and stdout, and the arguments from `claudeArguments` in the config. The process stays alive: later prompts are written to its stdin, including while it is working, and Claude Code folds them into the work in progress. One process per workspace is shared by every client.

Codex cannot take a prompt while it works, so for a Codex workspace the server queues prompts and runs `codex exec` once per prompt, resuming the same Codex session each time; the workspace counts as working until the queue is empty. Codex picks its own session id, which the server reads from the first run and records the same way.

What the process says is turned into the conversation as it arrives: each piece of text Claude Code writes becomes an assistant message right away, and a final result marks the work as finished. The workspace counts as working from the moment a prompt is sent until that result, and the server corrects itself if more output arrives after one. The user's prompt is recorded when it is sent.

A new Claude Code AI session id is passed with `--session-id`; the server records it on the workspace once the AI first answers, and later processes use `--resume`. Claude Code keeps AI sessions in its own storage, scoped to the working directory, so an AI session can disappear: the workspace folder was renamed, or the data was cleaned up. When resuming reports that no conversation exists (`--resume` for Claude Code, `codex exec resume` for Codex), the server silently starts a new AI session in the same workspace, attaches it in place of the lost one and sends the prompts again. Nothing the user sees changes, because the transcript is the server's own.

An idle process is ended after five minutes, and also before a rename, a delete or a service, model or effort change, so those apply from the next prompt. If the process fails or exits while working, the work ends and the failure is reported as an event; the next prompt starts a fresh process. Because prompts cannot ask for permission, every Claude Code run passes `--dangerously-skip-permissions` and every Codex run `--dangerously-bypass-approvals-and-sandbox`; `claudeArguments` and `codexArguments` are appended after them.

## Service, model and effort

Each workspace has an AI service, a model and an effort (low to max). Claude Code offers Sonnet, Opus and Haiku and defaults to Sonnet on high; Codex offers Terra, Sol and Luna (the `gpt-5.6` models) and defaults to Terra on medium. A new workspace uses Claude Code. The client offers only the models of the chosen service and switches to that service's defaults when the service changes, and the server refuses a model the service does not offer. Every run passes the choices on the command line, so a change applies from the next process, including to an AI session that is resumed. Switching the service detaches the AI session, because a session only exists in the service that created it; the history is the server's own and stays.

## States

A workspace is always in exactly one state: Ready (nothing runs), Working (the AI is running), Running (a script or task is running) or Managing (a git action is running). The server owns the state in one place, a tracker that the prompt service, the script runner and the git workflows each enter when their work starts and leave when it ends, so the check and the change are one step and two requests racing for an idle workspace cannot both win. Work only starts from Ready: a workspace in any other state refuses the others (a prompt while a script runs, a git action while the AI works, a script during a git action, and rename and delete in every state but Ready) with a message naming what is in progress, and the work that holds the workspace has to end first. The one exception is AI input: a prompt or attachment is accepted in Ready and Working, where it joins the work in progress, and refused in Running and Managing.

A git action can use the AI itself to resolve conflicts. That AI work is part of Managing rather than a change of state, so the workspace stays Managing until the action ends, and if the AI is somehow still busy then, the workspace falls to Working instead of Ready. The automatic fetch and pull is not a state: it runs only from Ready, is not reported as busy and does not stop other work from starting. A fetch the user asks for is a git action like any other.

Clients show the state: the header is blue in every state but Ready, the message box is locked in Running and Managing (and says why), buttons that start work are disabled or refuse with a message, and the Managing state offers Cancel and undo. They work it out from the events and statuses they already receive (the AI working, a script running, a git action busy), in that order of priority, while the server remains the authority.

## Groups

Workspaces can be filed into groups, which can contain other groups to any depth; the client shows the result as a tree. A group exists only in the server's workspaces file: it is organization for Auto Remote and has nothing to do with the folder hierarchy on disk, so filing a workspace never moves anything and the workspace folders keep their real parents. A workspace is in at most one group and a group has at most one parent. Names are unique ignoring case among the groups with the same parent. A group cannot be moved into itself or into anything inside it. Deleting a group keeps what is in it and moves its workspaces and groups up into its parent (to the top level if it had none). The server keeps workspaces and groups in a custom order that every client shows: new ones go to the end, renaming keeps the place, moving into another group puts the item at the end of it, and the order is changed one place at a time with the shift commands, which only swap among siblings (the workspaces of one group, or the groups of one parent) and stop at the ends. Groups are managed from the workspace listing, not from a workspace's settings, and live server side so every client sees the same organization.

## Attachments

Files attached to a prompt are kept in an `attachments` folder in the data folder, one subfolder per workspace, under a name prefixed with a random id, and are deleted with the workspace. The AI reads them by path, as it reads any file.

## History

The server keeps its own transcript per workspace (one JSON line per message, in a `history` folder in the data folder) so the client can show past conversation without depending on Claude Code's storage. Every message is recorded when it happens and gets the next position in the conversation, which events carry so clients can place them. The client reads the history in chunks, newest first, and asks for the chunk before the oldest one it has when the user scrolls to the top, until the start of the history.

## Files

Browsing a workspace's files is read only and confined to the workspace folder: paths are resolved and rejected if they leave it. Text files are returned up to 256 KiB, and files that look binary are refused.

The view is live: a connection can watch one folder of one workspace (the one its client is showing), and the server reports changes to that folder, collapsed over a quarter second, so the client refreshes the listing and any open preview. Only the folder itself is watched, not its sub-folders, which keeps watching cheap on large trees; watching another folder replaces the watch, and it ends with the connection.
