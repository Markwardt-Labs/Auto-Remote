# Server config

The server reads one JSON file at startup (the path passed as the first argument, `config.json` by default). Names are case-insensitive. A missing file, invalid JSON or a value that fails validation is printed and the server exits with code 1.

| Setting | Default | Description |
| --- | --- | --- |
| `key` | required | Key a client must send to connect; must not be empty. |
| `port` | `52679` | The one port every client connects to, over secure WebSocket; 1 to 65535. |
| `dataFolder` | `AutoRemote` folder in the server user's application data | Folder holding all data the server stores; created when missing. |
| `defaultFolder` | the server user's home folder | Existing folder where browsing for a new workspace's folder starts. |
| `claudeCommand` | `claude` | Claude Code executable run for prompts; must resolve on the server's machine. |
| `claudeArguments` | `[]` | Extra arguments added to every Claude Code run. |
| `codexCommand` | `codex` | Codex executable run for prompts; must resolve on the server's machine. |
| `codexArguments` | `[]` | Extra arguments added to every Codex run. |
| `speechModel` | `small.en` | Whisper model that turns speech into text inside the server for voice input: a model name (`tiny.en`, `base.en`, `small.en`, `medium.en`, `small`, `large-v3-turbo` and so on, as named at `huggingface.co/ggerganov/whisper.cpp`) or the path of an existing `ggml` model file; empty turns the built-in voice input off. See Voice input below. |
| `speechCommand` | none | Executable that turns a recording into text instead of the built-in model; when set, `speechModel` is not used. |
| `speechArguments` | `[]` | Arguments of the speech command, in which `{file}` is replaced by the path of the recorded WAV file. |
| `dotnetCommand` | `dotnet` | .NET executable that runs scripts and tasks with `run --file`; only needed for scripts. |
| `certificateFile` | none | PFX certificate presented to clients. |
| `certificatePassword` | none | Password of the PFX certificate, if it has one. |

## Voice input

The clients record speech and the server turns it into text, so it works the same on every client. By default the server does it itself with Whisper (whisper.cpp through Whisper.net), with nothing to install: the native libraries are inside the server executable and are extracted into `native/` in the data folder on first use, and the model named by `speechModel` is downloaded once into `models/` there. The download starts when the server starts (it is about 470 MB for `small.en`; a model that is already on disk is used as it is), and a recording made before it is done waits for it. The text is made on the GPU through Vulkan when the machine has a working Vulkan driver (NVIDIA, AMD and Intel all provide one), and on the CPU otherwise; the model is kept loaded for ten minutes after the last recording, so the first recording after that takes a few seconds longer. English-only models (`.en`) are faster and more accurate for English; the others detect the language.

`speechCommand` replaces all of this with any program: the recording is a temporary 16 kHz, mono, 16 bit WAV file, deleted afterwards, and the command runs with `speechArguments` (`{file}` replaced by the file's path, no shell involved, standard input closed), must print only the text to its standard output and exit with 0; a non-zero exit shows the last line of its standard error to the user, and it is ended after two minutes.

## Data location

The server stores all its data in `dataFolder`, by default the `AutoRemote` folder of the server user's application data (`~/.local/share/AutoRemote` on Linux) rather than the working directory: `workspaces.json` (workspaces, groups and settings), the per-workspace conversations in a `history` folder, `paused.json` (AI work that is paused, only while some is), the Whisper `models` and the extracted `native` libraries of voice input, and the self-signed `certificate.pfx` created on first start when `certificateFile` is not set.

## Fixed behavior

Prompts always run with `--dangerously-skip-permissions` (Claude Code) or `--dangerously-bypass-approvals-and-sandbox` (Codex), since they run unattended and cannot ask, and always do their work in the foreground (see Workspaces); `claudeArguments` and `codexArguments` come after them. Which service a workspace uses is a workspace setting, not a server setting; the executables are only needed for the services in use.
