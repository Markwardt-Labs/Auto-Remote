# Scripts

A workspace's `.cs` files can be run as single-file C# scripts and `.task` files as ordered groups of them. They run on the server, in the workspace folder, because that is where the files and the tools are; a client only asks for a run, watches its output and answers its prompts.

## Running

`RunScript` starts `dotnet run --file <script>` (the `dotnetCommand` setting names the executable, so the server's machine needs the .NET SDK for scripts) with the workspace folder as working directory and answers once the process started. The server usually runs as a background service with no display of its own, so a script that opens a window would find neither X11 nor Wayland: the client therefore sends its own `DISPLAY`, `WAYLAND_DISPLAY`, `XAUTHORITY` and `XDG_SESSION_TYPE` with the request, and the server adds them to the script's environment, but only for a connection from its own machine (a remote client's display variables would point at a different machine), and only those four names. The `--file` form works even when the folder holds a project or solution file, which a bare path would make `dotnet run` build instead. Standard output and standard error are copied as raw text chunks as they arrive, not by line, so a prompt written without a newline (`Console.Write("name? ")`) is visible while the script waits for its answer. Standard input stays open: `SendScriptInput` writes a line to it and also adds that line to the output, standing in for the echo a terminal would give.

A `.task` file lists one script path per line. A line with only `-` is a wait: the scripts before it must all succeed before the ones after it start, and the scripts between waits run together. Blank lines are ignored, and a path listed twice runs once, at its first place. The whole task is validated before it starts, so a missing or non-`.cs` entry refuses the run instead of failing halfway. A failing or stopped batch ends the task, and the scripts that never ran are marked skipped.

## Beside everything else

A script is a process of its own, so running one never locks the workspace: scripts are not a workspace state and need none. Any number of scripts and tasks run at once, beside each other, beside AI work (the AI can be prompted while scripts run and scripts can start while it works) and beside git actions, and the header stays as it is for the AI and git alone. A running script does not stop loops from sending their next prompt either, and the Stop button of a workspace ends the AI's work or the git action, never a script: a script is stopped from the Script tab.

Runs are told apart by the path of their file, and a script's path is also the name of its entry in a task, so a file can only be in one run at a time. `RunScript` is refused when the file, or a script a task lists, is already running or is still waiting its turn in a task that runs, with a message naming the file; once a run ended, running its file again replaces the kept run. Concurrent runs can of course change the same files, which is for the person starting them to weigh, and the server leaves it alone.

Changing the folder of a workspace under a running script would break the script, so renaming, moving or deleting a workspace first stops its scripts and waits for their processes to end; every other request ignores them.

## Runs are kept in memory

The server keeps the runs of each workspace, with their output, until a client removes them or a new run of the same file replaces them, so a client that connects or reconnects reads the runs with `GetScripts` and follows the rest as events. Nothing is written to disk: runs do not survive a server restart, and stopping the server kills running scripts. Output is cut at its start beyond 500000 characters per script, and every client shows only the last 100000 of what it holds (copying gives all of it) because laying out a very long text block again on every update is what makes long outputs slow. Output events are batched about every 100 milliseconds for the same reason.
