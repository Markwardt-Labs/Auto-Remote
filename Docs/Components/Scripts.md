# Scripts

A workspace's `.cs` files can be run as single-file C# scripts and `.task` files as ordered groups of them. They run on the server, in the workspace folder, because that is where the files and the tools are; a client only asks for a run, watches its output and answers its prompts.

## Running

`RunScript` starts `dotnet run --file <script>` (the `dotnetCommand` setting names the executable, so the server's machine needs the .NET SDK for scripts) with the workspace folder as working directory and answers once the process started. The `--file` form works even when the folder holds a project or solution file, which a bare path would make `dotnet run` build instead. Standard output and standard error are copied as raw text chunks as they arrive, not by line, so a prompt written without a newline (`Console.Write("name? ")`) is visible while the script waits for its answer. Standard input stays open: `SendScriptInput` writes a line to it and also adds that line to the output, standing in for the echo a terminal would give.

A `.task` file lists one script path per line. A line with only `-` is a wait: the scripts before it must all succeed before the ones after it start, and the scripts between waits run together. Blank lines are ignored, and a path listed twice runs once, at its first place. The whole task is validated before it starts, so a missing or non-`.cs` entry refuses the run instead of failing halfway. A failing or stopped batch ends the task, and the scripts that never ran are marked skipped.

## One thing at a time

The AI, a git action and scripts must never change the same working tree at once, so the server allows one script or task per workspace at a time and, as for every kind of work, only starts it when the workspace is Ready: `RunScript` is refused while the AI works or a git action runs, and `Prompt` (and rename and delete) while a script runs. The checks live in the server, so every client gets them whatever it shows; the desktop client also disables sending and refuses to start scripts using the state it follows. Clients show a running script like a working AI: the header turns blue.

## Runs are kept in memory

The server keeps the runs of each workspace, with their output, until a client removes them or a new run of the same file replaces them, so a client that connects or reconnects reads the runs with `GetScripts` and follows the rest as events. Nothing is written to disk: runs do not survive a server restart, and stopping the server kills running scripts. Output is cut at its start beyond 500000 characters per script, and the desktop client shows only the last 100000 of what it holds (copying gives all of it) because laying out a very long text block again on every update is what makes long outputs slow. Output events are batched about every 100 milliseconds for the same reason.
