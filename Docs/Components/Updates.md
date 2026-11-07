# Updates

The server and the desktop and Android clients update themselves from the newest GitHub release. The web client has no update system: it is a file to replace by hand.

## Finding the release

`IReleaseFeed` reads the latest release of `Markwardt-Labs/Auto-Remote` from the public GitHub API (no token), which leaves out drafts and pre-releases. The tag is the version, so a tag that is not `major.minor.patch` with an optional suffix is ignored. Downloads are checked against the size and the SHA-256 digest GitHub states for each asset, and a mismatch discards the file. Versions are ordered by `AppVersion`: numbers first, and a suffixed version is earlier than the final release.

The release assets this relies on are `AutoRemote-Server-linux`, `AutoRemote-Server-win.exe`, `AutoRemote-linux`, `AutoRemote-win.exe` and `AutoRemote-android.apk`, all x64.

## Replacing a running program

`IExecutableInstaller` works in two steps. `Stage` downloads the asset next to the running executable (so the move never crosses a file system) and changes nothing else. `Apply` moves it into place as the last thing the program does with its own files. On Linux the file is renamed over the running one. Windows refuses that but allows renaming a running executable, so the old one becomes `.old` and is deleted on the next start, and is put back if the new file cannot be moved. A program started through `dotnet` has no executable of its own to replace, so it never offers or accepts an update.

The split exists because a single-file program loads assemblies lazily from its executable on disk, so after `Apply` a process that needs one it has not loaded yet fails with a missing assembly. Before applying, the server and the desktop client therefore run a harmless process (`/bin/true`, or `cmd /c exit`) so that everything starting one needs is loaded, and after applying they only start the new executable and exit.

## The server

A client sends `UpdateServer`; the server compares its version with the newest release itself, stages the download and asks `IServerRestart` to shut it down after a second, so the response gets out first. Once its sessions and listener are closed it applies the update. Where it comes back depends on who started it:

- Under a service manager (systemd sets `INVOCATION_ID`) the server exits with code 75 and relies on the manager restarting it on failure, because the manager would stop a process the server started itself. A unit with `Restart=on-failure` or `Restart=always` works; one without a restart policy leaves the server down.
- Otherwise it starts the new executable with the same command line once its own sessions and listener are closed.

The restart ends AI work in progress, like any restart. Only one update runs at a time. If the update cannot be applied the old version starts again.

## The clients

The server reports its version in the handshake. `MainViewModel` compares it with its own on every connect and reconnect, and a difference, or no version from an older server, shows `VersionMismatchViewModel` instead of the workspaces: Update (when a newer release exists) or Change server. That screen is also shown when a reconnect finds a changed server, and it goes away again once the versions match.

`UpdateViewModel` looks up the newest release on connect, after every reconnect and every hour (GitHub allows 60 anonymous requests an hour per address, and one lookup an hour leaves plenty). An update is offered when the release is newer than the client (and the client can install itself) or than the server. Confirming runs the server update first, then the client, so a client that restarts has already told the server. The global settings screen has an Updates section (not in the web client) that shows the versions of the app and its server, a Check for updates button that looks up the newest release at once and says what it found, and an Update button, which opens the same confirmation, when something newer exists. A server too old to know the command is reported as too old to update itself, and the client is left alone. From the confirmation on, the app shows the updating screen (the status and the download progress) in place of whatever was showing, and the work runs in the background of the app. The screen cannot be left while the update runs. A failure returns to the earlier screen with its error in the confirmation. A client that restarts after its update usually starts before the server is back, so a saved server that cannot be reached is tried again every few seconds for two minutes while the connect screen shows the error; the app opens by itself when the server answers, and the retrying stops if the key is rejected or the user touches the connect screen. After a server-only update the screen is left by itself once the server is back (it can be closed before), and after the client handed its package to the Android installer it stays until closed. The earlier screen is restored as it was, unless the versions differ now, which the usual check settles.

How the client replaces itself is the head's business (`IClientUpdater`):

- Desktop stages and applies the executable, starts it again with the same command line and ends the process. Other windows are processes of their own and keep running the old version until closed.
- Android downloads the APK to the cache and hands it to the system installer through a `FileProvider`. The user has to allow the app to install packages the first time, and the APK must be signed with the key of the installed app, which the release builds are, so a debug build cannot be updated this way.
