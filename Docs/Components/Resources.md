# Resources

The AI of every workspace runs on one computer, and what it starts (builds, test runs, servers) can together take enough memory that the system ends the server. The server measures what each workspace and session takes so it can be seen before that happens. It only measures: nothing is limited or stopped by it. It reads the Linux process file system; a server on anything else says it cannot measure.

## Which processes count

Every process the server started, and whatever those started. Processes that detached from what started them, such as shared build servers, are no longer below the server and are found two ways:

- Processes seen once are followed until they end.
- When systemd started the server as a service of its own, everything in the service's control group counts, which is everything the server ever started. A server started any other way (from a shell) shares its control group with unrelated programs, so the group is ignored there, and a process that detached before it was ever seen is not counted.

The server looks every 2 seconds while anything works and every 10 seconds otherwise, because who started a process can only be told while the starter is still running.

## Which session a process belongs to

No process says which session it is for, so the first of these that applies decides:

1. Its parent belongs to a session: it is the same one.
2. It runs a script of a workspace.
3. It is a shell running a command that the AI of a session ran through its shell tool, which also covers a command run in a folder outside the workspace. When several sessions ran the same command, the one whose folder it runs in wins, else the latest.
4. Its working folder is inside the folder of a workspace or side session, the innermost one. This is how the AI tools themselves are told apart, since each session has its own.

What was decided stays with the process after its parent ends and it moves elsewhere; a decision made by folder alone is corrected when the command becomes known. A process that fits nothing, such as a usage check or a local model server, counts as "other". Something shared, such as a build server one workspace started and others use, counts against the one that started it. A removed side session's leftover processes stay with its workspace.

## What is measured

- **Memory** is the proportional set size, which divides shared memory between the processes sharing it so that totals do not count it twice. Where it cannot be read, the resident size is used.
- **CPU** is the share of the whole computer used since the sample before: one fully busy core of 16 reads as about 6%. It is empty until there are two samples. A process that starts and ends between two samples is missed.
- A list of processes names the ones taking the most memory plus any keeping the processor busy, so a loop that takes hardly any memory still shows.
- A report is kept for 1.5 seconds, so several clients cost no more than one. Memory is only read when a report is asked for.

## In the clients

The Resources section of the workspace settings lists the focused workspace's sessions; the one in the AI environment lists every workspace that works or still has processes running (a stopped one with leftovers reads "Not working"), then the server itself and the other processes. Both show the computer's memory, swap and processor, and warn when less than a tenth of the memory is available. In the workspace list, a working workspace has a line with its memory and CPU.

Clients ask every 3 seconds, and only while one of these is on screen: an expanded section in a showing settings screen, or the open workspace list. The sidebar line is a separate observable object that each row holds, because the list is rebuilt by comparing rows and a changing number in a row would replace the row and drop the selection.
