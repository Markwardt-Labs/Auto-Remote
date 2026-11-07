# Stored data and migrations

The server keeps its state in the data folder as JSON files, and a later release must be able to read what an earlier one wrote. Every file the server owns therefore carries a `version`, written first in the file, and a release upgrades older files itself when it reads them.

## Versions

A file without a `version` is the initial version, 1: it is what the server stored before files were versioned, and it is also what a file looks like to a release that never wrote a number. The current version of a file is 1 plus the number of upgrade steps its owner has, and each step turns one version into the next (step `i` takes version `i + 1` to `i + 2`). Steps are only ever appended, never edited, because files of every version exist in the wild; a step works on the JSON itself rather than on the types of the day, so it keeps working when the types change.

Reading a file runs the steps it still needs, in order, and then stores the result. Before it does, the original is kept next to the file as `name.vN.bak` (N being the version it had; a backup that already exists is never overwritten), and the new content is written to a temporary file and moved over the old one, so a crash cannot leave a half written file. A file already at the current version is left as it is. A file that is not valid JSON, or whose version does not exist (below 1), stops the server with a message naming the file. A file written by a newer release than the one running (a higher version) is refused and never overwritten, so going back to an older release cannot destroy data; the message names the file and both versions.

Writing always stamps the current version.

## The files

`workspaces.json` (workspaces and groups) is at version 3. Its steps: from 1, drop the model and effort an older release stored per workspace (they belong to the AI session now) and rename the `OpenCode` service to `Local`; from 2, keep only `id`, `sessionId` (renamed from `aiSessionId`) and `groupId` of a workspace, since the folder and provider are read from its session, and mark it `adopt` (below). A workspace that has no usable session (none yet, or the tool's own hyphenated identifier of an older release) keeps its `folder` and `service` instead, and the server gives it a session when it starts and drops them then. `environment.json` (the AI environment, readable by the server user only since it can hold API keys) is at version 1.

## Sessions stored by Live AI 1.x

The `liveai` folder belongs to Live AI, and Live AI versions and upgrades its own link files: a session stored by an older version of the library loads in the current one, the original is kept beside it as a `.bak` file, and the tool's own conversation is never touched, so the history carries over. What the first layout never stored (the shell, web and image generation switches, and the standing instructions) keeps its default, and Live AI does not say which sessions that concerns, so the server keeps track itself: the step that turns version 2 of `workspaces.json` (or the first one) into version 3 marks every workspace that has a usable session with `adopt`, because those sessions were stored by a release before the server's settings were kept in the session. When the server starts it gives each marked session what it ran with before, whatever the defaults of new workspaces say: the shell, the web and files outside the workspace allowed, image generation when the environment has an image service, and instructions to work in the foreground (loading the session and disposing it stores that), and then clears the mark. A session that cannot be loaded (its folder is gone) keeps the mark and is tried again at the next start. A workspace that has no usable session gets a new one made with the defaults, and is not marked.

A conversation made before Live AI was used at all, whose workspace holds the tool's own session identifier written with hyphens, has no link and cannot be continued; its workspace starts a new session.
