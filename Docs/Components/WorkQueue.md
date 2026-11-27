# Local AI work queue

A machine that runs its own models can usually serve one AI session well and two badly: parallel generations share one GPU, each parallel slot of a model needs its own context cache, and two different models may not fit in memory at once. `WorkQueue` therefore decides when work for a local workspace starts. It never touches the requests the AI sends to the model server: scheduling happens between whole pieces of work, which is why no gateway between the AI tool and the model server is needed. Claude Code and OpenAI Codex workspaces pass straight through.

## Modes

The environment's `localWorkMode` chooses between them.

- `Yield` (the default): one local AI session works at a time. Work of another workspace waits, and running work is never interrupted. Direct work (the user's, their loops and the follow-ups of delegated jobs) always starts before delegated tasks; within each kind it is first in, first out. A server takes new delegated work only when nothing runs or waits.
- `Simultaneous`: work starts at once. Only delegated tasks are limited, to `delegatedTaskLimit` at a time.

Changing the mode takes effect immediately: switching to `Simultaneous` starts what waits, and switching back lets what runs finish before anything else starts. `Yield` cannot control other programs that use the same model server.

## What waits

A prompt for a workspace that is already working is steered into that work, as without a queue. A prompt for a workspace whose earlier prompt still waits joins it, so a workspace has at most one waiting prompt; the lines naming its attachments are kept at the end, where the server looks for them. Work that is not a prompt of its own, such as writing a commit message or the AI resolving the conflicts of a rebase or merge, reserves the AI for its duration and waits the same way.

A request to integrate a side session is ordinary work for its main workspace: it is steered into running work or joins the waiting prompt, except that it goes ahead of a loop run that waits. Cancelling the integration takes its text out again.

A waiting prompt starts against the files as they are then, and the snapshot a cancellation reverts to is taken then too. It waits while a git action runs in its workspace, and work behind it may go first meanwhile. A waiting workspace is not busy: it counts as ready (clients show the waiting work, not a state), git actions stay available, and it sends no ready notification. If a waiting prompt cannot start, the workspace reports why and its text is kept as a saved input so it is not lost; the same happens, without an error, to one that is stopped or cancelled in the moment it starts, which is never queued again. Text saved this way may be longer than a saved input a client sends.

The report of a job of delegated tasks is a prompt of its own kind: it waits only when nothing else of its workspace does. A prompt the user sends while it waits takes it along, in its place in the queue, and from then on it is part of the user's prompt.

A request for a commit message gives up when the AI has not answered 20 minutes after its turn came, since nothing else could end a question that hangs, and it reads the pending changes again once its turn comes.

The AI counts as busy from the moment work is chosen to start until the session says it finished, including work the AI starts by itself.

Removing the waiting work (`CancelQueuedWork`) never touches running work, which `StopWorkspace` and `CancelWorkspace` do. A client puts a prompt it sent itself back into its message box, above what is typed there and with its already uploaded attachments; a prompt that came from elsewhere is kept as a saved input instead, which the request asks for with `keep`.

## Loops

A loop submits each run like any other direct prompt, so a run waits its turn at the back of the queue and a loop has at most one waiting run. The interval is measured from when a run starts, not from when it was queued. Only running or waiting work counts as work: between two runs of a loop with an interval the AI is free and delegated work may start, so the next run may have to wait for it. A loop submits its next run only once it sees that the previous one ended, so waiting delegated work can start in between even with no interval, and the run then waits for it. Ending a loop removes its waiting run (unless the user added a message to it, which keeps it), and cancelling the waiting run of a loop ends the loop.

## Restarts

Waiting direct prompts are stored in `queue.json` and wait again after a restart, in order. Nothing restored starts until the integrations of side sessions have taken their requests out of it, since an integration sends its own again; once the server is stopping nothing more starts. Reports of delegated jobs are given again by the swarm. Runs of loops are dropped, since loops do not survive a restart, reservations belong to requests that ended with the server, and delegated tasks are submitted again by the swarm, which keeps its own record of them. Work that was running when the server stopped is not run again.
