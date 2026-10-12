# Project

Documentation for this repository's own tooling and workflow.

## Scripts

`Scripts/` holds file-based C# apps (`dotnet run Scripts/<Name>.cs`) for automated repository
actions.

- `Scripts/Test.cs` - runs the test suite with coverage collection and prints a summary.
- `Scripts/Mobile/Server.cs` - runs the server with a git-ignored dev config and app data under `Scripts/Data/`, rewritten on each start (TCP port 52678, access key `dev`, reachable from the emulator at `10.0.2.2`).
- `Scripts/Mobile/Client.cs` - deploys the Android client (`Client/Mobile/`) to an emulator, starting the first AVD if no device is attached, launches it and streams its log until interrupted. An emulator the script started is stopped when the script ends; an already attached device is left running. Finds the Android SDK via `ANDROID_HOME`, `ANDROID_SDK_ROOT` or the default install folder.
- `Scripts/Mobile/Run.task` - runs the two Mobile scripts together.
- `Scripts/Desktop/Server.cs` - runs the same dev server for the desktop client (TCP port 52678, access key `dev`, `localhost`).
- `Scripts/Desktop/Client.cs` - builds and runs the desktop client (`Client/Desktop/`) until its window is closed, with its saved settings under `Scripts/Data/` (via the desktop client's `--data-folder <path>` argument, which makes it use that folder instead of the user's application data).
- `Scripts/Desktop/Run.task` - runs the two Desktop scripts together.
- `Scripts/Web/Server.cs` - runs the same dev server for the browser (`localhost`, WebSocket port 52679, access key `dev`); its self-signed certificate has to be accepted once by opening `https://localhost:52679`.
- `Scripts/Web/Client.cs` - serves `Client/Web/index.html` at `http://localhost:8080/`, re-reading the file on each request, and opens it in the default browser.
- `Scripts/Web/Run.task` - runs the two Web scripts together.
- `Scripts/Verify.cs` - applies formatting fixes and regenerates the coverage badge.
- `Scripts/Publish.cs` - cuts a release (see Publishing below). A manual, human-only action.

## Publishing

Run `Scripts/Publish.cs` locally to cut a release:

1. It prompts for the version to publish (e.g. `1.2.3`) - `Server/Server.csproj` carries no `<Version>` of its
   own, so this is what actually gets built and published.
2. It creates the GitHub Release (and its underlying tag) for that version locally via `gh release
   create` - done locally because a repo ruleset blocks the default `GITHUB_TOKEN` from creating tags.
3. It dispatches `build.yml`'s `workflow_dispatch` trigger with the version as input. The workflow
   verifies the dispatcher has Admin permission on the repo, refuses to run from anything but `main`,
   publishes the server and the desktop client (Windows and Linux x64) as self-contained single-file
   executables, the Android client as a signed APK and the web client
   as a single HTML file, and uploads them alongside `THIRD-PARTY-NOTICES.txt` as release assets.
4. It waits for that run to finish, rolling the release/tag back if the workflow fails, so a failed
   publish never leaves one behind. If it can't confirm the run happened at all, it leaves the release
   in place instead, rather than risk deleting one that's still running.

## Workflows

- `.github/workflows/build.yml` - builds, verifies formatting (`dotnet format --verify-no-changes`,
  never applies fixes), and runs tests on every push/PR to `main`; its `publish` job (see Publishing
  above) only runs on `workflow_dispatch`, gated on the dispatcher having Admin permission on the repo
  and the run being on `main`.
