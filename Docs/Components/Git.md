# Git

Git support lets a client show and drive the git repository of a workspace without ever running git itself. Everything here runs in the server's `GitService`, reached through the commands listed in the protocol document.

## What is a repository

A workspace is a repository only when its folder is the root of one (`git rev-parse` reports an empty prefix inside the work tree). A workspace inside a larger repository, or a folder with no repository, reports `isRepo` false, and so does a server without git installed. `GitSetRemote` is the one way to make a plain folder a repository: it runs `git init` and adds `origin`.

## Reading state

The status comes from a handful of cheap commands: the branch from `symbolic-ref`, the head commit's subject and abbreviated hash from `log -1`, a tag from `describe --exact-match`, pending changes from `status --porcelain` and the remote from `remote get-url origin`. Changes come from one `status --porcelain=v1 -z --ignored=matching --untracked-files=all`: every untracked file is listed on its own, an ignored folder is one entry ending in `/`, and a rename is reported by its new path as modified. Clients derive the color of a file or folder from that list: its own entry first, then an ignored ancestor, and for a folder what lies under it (all new is new, anything else is modified). Read commands set `GIT_OPTIONAL_LOCKS=0` so they never fight the index lock of an AI that is running git itself.

History is `git log` over every branch, tag and remote branch plus the head, in date order, paged with `--skip` and one extra commit read to learn whether more exist. Branches and tags come from the `%D` decorations; a decoration naming a remote-tracking branch is told apart from a local branch containing a slash by checking it against `for-each-ref refs/remotes`. Each commit also carries its parents (`%P`), which is all a client needs to draw the branch graph: it walks the commits newest first keeping one lane per open branch line, so a commit takes the lane of the child that expects it, its first parent stays in that lane, further parents of a merge open or join other lanes, and lines that are not needed any more end where branches meet. The layout is done on the client because it only depends on the commits it already loaded, so older chunks just extend it. A commit's changed files are diffed against its first parent, or listed as all added for a root commit.

## Diffs

Clients get the whole file with its changes marked rather than hunks, so the server asks git for a unified diff with a million lines of context and parses it into one sequence: unchanged, added and removed lines, each with the line number before and after. This reuses git's own diff algorithm, so neither the server nor the clients carry one. The working tree is diffed against `HEAD`; a new untracked file, which `diff HEAD` does not list, is diffed against `/dev/null` instead. Binary files come back flagged with no lines, and a diff stops at 5000 lines.

## Acting

Commit stages everything (`add -A`), refuses when nothing is staged, commits and then pushes the branch to `origin` when one is set; the push is best effort so a failing remote never turns a made commit into an error. Reset is `reset --hard` plus `clean -fd`. Branch is `checkout -b`, tag is an annotated tag at the head. Failures return git's own message, for example the missing identity ("Please tell me who you are"), since the server runs as a user who is expected to have git set up.

Commit, reset and branch are refused while the AI is working in the workspace so they cannot race its edits; tags and the remote are harmless then.

## Workflows

Everything beyond a single command runs through `WorkspaceVersioning`, a port of Auto Dev's versioning service. `Perform` is the gate: one action per workspace at a time (a second is refused), and only while the workspace is Ready (a git action puts it in the Managing state), a `GitChanged` event when it starts and ends, and a snapshot taken first (checked out branch, head, whether changes were pending, every local branch tip). When the action is cancelled (`GitCancel`, `StopWorkspace`, `CancelWorkspace`, or the server shutting down; a client disconnecting does not cancel it, since mobile clients drop and reconnect) or fails unexpectedly, the snapshot is restored: an in-progress rebase or merge is aborted, the original branch is checked out again, the head is moved back (a hard reset only when nothing was pending, so uncommitted work is never discarded) and branches that were rewritten or deleted are put back. A refusal with a message (`RequestException`) is a normal answer and restores nothing by itself; the workflows clean up after their own failures.

Checkout accepts a local branch, a branch that exists only on the remote (a tracking branch is created), a tag or a commit hash (the last two detach); pending changes are discarded first only when asked to. Delete removes the branch or tag locally and, if it exists there according to `ls-remote`, on the remote.

Squash builds one commit from the branch tip's tree on top of the merge base with `commit-tree` and moves the branch with `update-ref`, so it works on any branch and never touches the working tree; it then force-pushes. Rebase squashes first when asked, skips the replay when the branch is already built on the target, checks the branch out (git can only rebase in the working tree), rebases, force-pushes and returns to the original branch. Merge rebases the source onto the target when needed, fast-forwards the target (checking it out), pushes it and deletes the source everywhere, which is why the target is left checked out. Both need a clean working tree.

Conflicts are resolved by the workspace's AI: the server prompts it with the conflicted files and waits until it is idle again (polling, up to 20 minutes), up to three attempts, then continues the rebase. When the AI cannot be started, does nothing, or the attempts run out, the rebase is aborted and the action fails with a message; the conversation shows what the AI was asked and did.

The automatic fetch and pull are `GitSync`, driven by the clients (when a workspace gets the focus, every minute while it is focused, and when the history opens). It fetches with pruning; local branches other than the checked out one are moved to match the remote only when they have nothing of their own (they were in step before the fetch, or are an ancestor of the remote), so unpushed commits are never orphaned; and when the checked out branch's remote counterpart was deleted (for example by someone else's merge cleanup) the head is detached at the same commit and the branch deleted. It then fast-forwards the checked out branch when the remote is ahead, stashing and restoring pending changes around the pull and having the AI resolve conflicts of the restore; a failed restore keeps the stash. Sync is a background action: it is not reported as busy and sends `GitChanged` only when it changed refs, the head or the working tree, and it does nothing while the AI is working or another action runs.

Squash, rebase and merge need a git identity (git commits on the user's behalf); the status says whether git knows one, and clients ask for a name and email first (`GitSetIdentity`, written to the server user's global configuration).

## Cloning

`CloneWorkspace` creates the clone's folder inside an existing folder, named by the request or after the repository, and refuses an existing folder. A failed clone removes whatever it left. It runs for as long as the clone takes (up to 30 minutes), unlike every other git command, which is cut off after two minutes.

## Safety

Git always runs with an argument list and no shell. Anything a client controls that could be read as an option is validated: URLs may not start with `-`, commit arguments must be hexadecimal hashes, branch and tag names go through `check-ref-format`, and paths may not be absolute or contain `..`; the clone URL is also passed after `--`. Git never prompts (`GIT_TERMINAL_PROMPT=0`, ssh in batch mode unless `GIT_SSH_COMMAND` is set), so a missing credential fails fast with a message instead of hanging, and only the file, https, http, git and ssh transports are allowed.
