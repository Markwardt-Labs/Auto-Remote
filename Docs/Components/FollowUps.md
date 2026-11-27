# Auto Commit and Queued Inputs

What follows the AI's work: committing its changes by itself, and sending inputs that were queued while it worked. The server does both, so they happen whether or not any client is connected.

## Auto commit

Auto commit belongs to the work under way only. It starts off, a user turns it on or off while the AI works (every client of the server sees the same state), and it goes off again as soon as the work is over, whether it committed or not. A prompt can also bring it along: one sent with `autoCommit` turns it on for the work it starts or joins, before that work can end, and off again if the prompt is refused. A prompt without it leaves the setting alone, so it never turns off what the user turned on. Work that takes a ready workspace but never starts, such as when the AI session cannot be opened, turns off what was turned on meanwhile.

The commit is the one the Auto Commit action makes: the AI writes the message, then everything uncommitted is committed (and pushed when there is an `origin`). It takes all uncommitted changes, whoever made them, including what stopped or failed work left. Nothing is done outside a git repository or when there is nothing to commit.

It only commits work that went well: the AI replied and was neither stopped, cancelled nor failed. A stop turns it off the moment it is asked for, and also covers an AI that ends by itself before hearing of the stop. A stop during the commit holds at whatever point it comes: nothing more is committed, the commit is not tried again later and the queue stays paused. An AI working inside a git action, resolving its conflicts, ends no work of the user's.

Nothing else may start between the work and its commit, so the workspace goes from `Working` straight into `Managing` with no ready moment in between: the work's end is told while the workspace is still taken, and passes the state on. Everything that starts work already waits for a ready workspace or refuses one that git has, and prompts are refused while a commit is under way. Clients learn of a git action by reading the git status, which is too late here, so `AutoCommitChanged` with `Committing` is sent before the AI is said to have stopped, and clients count it as git working.

## What a saved input keeps

Every saved input says whether the work it starts is committed (`autoCommit`, on unless turned off, also for dictated and recovered ones), which the list can change for each. It goes with the input: loaded into the message box, it is shown there as a button of its own that can be changed, and sending, queuing or saving the input again uses it, until the box is emptied. The client keeps it with the draft, so a restart does not turn a loaded input into a typed one. Typed input has none: sending it leaves auto commit as it is, and queuing or saving it keeps it with auto commit on.

## Work that is not over

The AI ending its turn does not always end the work. While it is paused at the usage limit and still to be resumed, or while it is still to hear the results of tasks it delegated, auto commit stays on and the commit is made once nothing of that is left. Loops, requests to integrate a side session and queued inputs wait meanwhile. Cancelling the automatic resume gives the work up: auto commit goes off.

## Loops

The runs of a loop are never committed by themselves: auto commit cannot be turned on while a run is under way, which the loop says with `isRunning`. Everything else in a looping workspace, queued inputs and messages sent between runs, is committed as without a loop, and starting a loop leaves the auto commit of the work under way alone. Since a commit takes everything uncommitted, it also takes what earlier runs left.

A commit under way or due and a queued input that is to be sent go ahead of a loop's next run. So an input queued during a run is sent as soon as that run ends, at once between runs, and a loop started while queued inputs run (also while one of their commits is made) makes its first run once the queue is done. A local model's run that waits for its turn gives up its place to a queued input and takes a new one afterwards, without counting as cancelled by the user.

## Locks

A change of a workspace's state is published while the states are locked, and a stop is acted on right then. So following the work never takes its own lock and then reads or changes a state: it takes the two together, the states first (`Together`), or the two directions would lock each other up.

## Queued inputs

A queued input is a saved input marked `isQueued`. Queued inputs are listed first, in the order they will be sent, ahead of the saved ones. They can be loaded into the message box (which takes them out of the queue) or deleted like any other, and the limit on saved inputs drops them last.

The oldest is sent once nothing is in its way: the workspace is ready, no commit is under way or due, no run of a loop is under way, no work waits for the local AI, the work is not paused or waiting for delegated results, no side session is waiting to be integrated into it, and the usage limit allows new work (otherwise the workspace shows the limit pause until it does). Each is sent with auto commit as its own `autoCommit` says, which the user can still turn off or on while it works, and a commit is made before the next starts. Work that is on its way to a local AI counts as in the way too. A queued input never joins other work: auto commit is turned on for it only once it has the workspace (for a local model, once it is that workspace's waiting or starting work), so one that other work got ahead of changes nothing and is sent later. It leaves the list only once it was sent, so one that cannot be sent is not lost, and one that was sent is never sent again, also when the list could not be written. A queue holds at most 200 inputs, and queued ones are never dropped to make room.

A local model's queued input then takes its turn in the local work queue, which stores whether its work is to be committed, so it still is after a restart.

A side session with a commit due or any queued input is not integrated.

## When the queue pauses

Running queued work after something went wrong would pile more work on it, and each commit would take the broken changes with it. So every queued input is paused (`isPaused`, kept on disk) when work is stopped, cancelled or fails (waiting work of a local model too, whether a user cancels it or it cannot start), when an automatic commit fails, when a queued input cannot be sent, when the automatic resume is cancelled (the work it still finishes then resumes nothing by going well), and when the server starts, since the work they waited for did not end. They resume when a user asks (`ResumeQueue`), after work next goes well (the runs of a loop do not count, since they go on by themselves) or when another input is queued, and then run oldest first.

## What clients explain

Both a pause and the order of work are easy to miss, so clients show them above the conversation rather than only in the saved inputs, which open only while the message box is empty (for the same reason the button that opens them counts the queued inputs).

A prompt never turns auto commit off, so while it is on for the current work, the setting of a loaded input cannot keep what is sent into that work from being committed. Its button is therefore shown as already on and disabled; the input still keeps its own setting for queuing and saving.

Clients work out what a loop waits for from the loop, the queued inputs, auto commit and the workspace state. A loop counts as waiting only once its next run is due: before that its countdown stays, and queued inputs are only said to go first.
