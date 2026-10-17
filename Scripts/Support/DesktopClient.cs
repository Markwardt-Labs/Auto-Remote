#:package Markwardt.ScriptUtilities@0.2.0
#:property TreatWarningsAsErrors=true

// Builds and runs the desktop client until its window is closed, keeping its saved settings under
// Scripts/Support/Data/ (git-ignored) instead of the real app data folder. Supports RunDesktop.task and is not
// run directly. File-based app (dotnet run) - run from the repo root, e.g. `dotnet run
// Scripts/Support/DesktopClient.cs`.

using Markwardt.ScriptUtilities;

Directory.CreateDirectory("Scripts/Support/Data");
(await Script.Run("dotnet", "run", "--project", "Client/Desktop/Desktop.csproj", "--", "--data-folder", Path.GetFullPath("Scripts/Support/Data"))).Verify();
