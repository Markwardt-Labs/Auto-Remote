# Unattended Work

The AI is trusted to develop unattended: users give inputs in different ways, the server decides when they run, and the AI does the work, delegating and combining it as it sees fit. The server never judges the work or waits for approval. It schedules, tells the AI what it has to know, carries out the commit settings and records what happened. It does all of this itself, so it happens whether or not any client is connected.

## Order of work

A chat (a workspace or a side session) does one thing at a time. What runs by itself goes in this order: the commit of work that ended, work that is tried again, bringing in the work of side sessions and delegated tasks, the queued inputs, and the runs of a loop. What a chat is going to do, and why each thing waits, is its agenda, which the server works out so that every client says the same (`GetAgenda`, `AgendaChanged`), including the one status line shown above the chat.

## Outcomes

Every piece of work (a sent message, a queued input, a loop run, a bring-in, delegated results) is followed as a step until it ends:

- **Done**: the AI replied and was neither stopped nor failed. It is committed if its commit is on.
- **Unfinished**: the AI called `report_unfinished`, which every workspace's AI has. Nothing is committed for it. A bring-in that ends without confirming what it was given counts the same.
- **The AI could not run**: it failed before the AI did anything, for a reason that passes by itself (an outage, a usage limit, being signed out, a tool or model server that cannot be reached). A queued input or resumed work is kept and tried again after growing waits, without limit, so the queue is never used up while the AI is unavailable. A queued input that cannot even be sent stays queued the same way; one that fails to send for a reason of its own is moved to the drafts after three tries. A failure that passes while the work is paused at the usage limit leaves it paused.
- **Failed**: any other error. Queued and resumed work is tried again, telling the AI to check what was already done, and given up after three attempts. A sent message is not tried again, since whoever sent it sees the error.
- **Stopped**: the user stopped it.

Work that does not end well holds nothing else up. It is flagged for the user's attention in the activity, and the AI is told with its next turn what was not finished and that its changes are still uncommitted, so it can repair them or work around them. A later commit takes them along.

## What the AI is told

What the AI is sent for new work may get an addition the chat does not show: what did not end well since its last turn (kept until a turn the AI answered), why work is being tried again, and, for work that runs unattended, that nobody may be there, so it decides for itself, says what it assumed and calls `report_unfinished` when it cannot finish. A message that joins running work gets nothing. The standing instructions say what an unattended message means.

## Commits

A commit is the one the Auto Commit action makes: the AI writes the message, then everything uncommitted is committed and pushed when there is an `origin`. It takes all uncommitted changes, whoever made them. Nothing is done outside a git repository or when there is nothing to commit. A commit or push that fails is flagged and told to the next turn.

- **Send** never turns the commit on, so questions and unfinished replies are not committed. A message that joins running work is committed with it if its commit is on.
- **Queued inputs** each keep their own setting, on unless turned off.
- **Loops** commit each run when set to.
- **Bringing in** commits by default, which can be turned off in the agenda.
- Users can turn the commit of running work on or off, loop runs included.

So that every piece of work gets a commit of its own, queuing an input that is to be committed (or turning the commit of a queued one on) while other work is under way (running, paused at the usage limit or waiting for the local AI) turns the commit of that work on, and it is committed before the queued input is sent. It does not when the user turned that work's commit off, or when that work is a queued step kept uncommitted, which the user did to group it with the next. A commit that only queuing turned on goes off again once nothing queued is to be committed.

Nothing may start between work and its commit, so the workspace goes from `Working` straight into `Managing` with no ready moment in between: the work's end is told while the workspace is still taken, and passes the state on. Prompts are refused while a commit is under way. Clients learn of a git action by reading the git status, which is too late here, so `AutoCommitChanged` with `Committing` is sent before the AI is said to have stopped, and clients count it as git working.

Work paused at the usage limit is not over: its commit waits until the work was resumed and ended, or until the automatic resume is cancelled.

## Delegation

The turn that delegates is committed when it ends, without waiting for the results, so that bringing in other work is not held up for the time a task takes (days, by drive). The chat's queued inputs and loop wait for the results. The results come back as a bring-in turn of their own.

## Stop

Stop is the one thing that holds work back. When a chat has anything that runs by itself (queued inputs, a loop, work to try again, work to bring in, delegated results or automated work under way), stopping its work, cancelling it, cancelling work waiting for the local AI or cancelling an automatic resume leaves the chat stopped until the user resumes it (`ResumeAutomation`). Meanwhile no queued input, loop run, retry or bring-in starts, and a loop is paused rather than ended. Sending a message or queuing an input does not resume it. Nothing of the stopped work is committed or tried again, and `StopWorkspace` on a chat with nothing under way gives up on the work that waits to be tried again, which nothing else would end. A stop during a commit holds at whatever point it comes.

## Restarts

What every chat does is kept in `automation.json` and loops in `loops.json`. After a restart, queued inputs are sent as before (a chat whose queue an older release had paused is stopped instead), loops go on, stopped chats stay stopped, a commit that was still to come is made, and work that was under way is sent again with a note that the server restarted, so the AI checks what was already done, delegations included, before repeating anything. Work paused at the usage limit is taken up too, without counting the restart against it. Work that was under way through three restarts is given up, since it may be what stops the server. Bring-ins are asked for again by whoever asks for them. Waiting work of a local model stays in the local queue, which keeps whether it is to be committed.

## Activity

Each chat keeps the newest 500 entries of its activity, one for each piece of work: what it was asked, how it went and why, the AI's final reply (which says what it assumed), its commit and whether it was pushed, what happened on the way (retries, restarts, messages that joined) and, for a bring-in, what the brought sessions and tasks said and which files they changed, which stays after they are removed. It is for finding what to repair and is not a backup. Clients are told of every change (`ActivityChanged`), and show a notification for an entry that needs attention.

## Locks

A change of a workspace's state is published while the states are locked, and a stop is acted on right then. So following the work never takes its own lock and then reads or changes a state: it takes the two together, the states first (`Together`), or the two directions would lock each other up. What a stop has to know about work that waits elsewhere is looked up ahead of time for the same reason.

## Queued inputs

A queued input is a saved input marked `isQueued`. Queued inputs are listed first, in the order they will be sent; the others are drafts. A queued input is sent once the chat is ready and nothing goes before it: no commit, retry, bring-in or loop run, no work waiting for or on its way to the local AI, no pause at the usage limit, no delegated results outstanding and the usage limit allowing new work. It never joins other work. It leaves the list only once it was sent, and one that was sent is never sent again, also when the list could not be written. A queue holds at most 200 inputs, and queued ones are never dropped to make room.
