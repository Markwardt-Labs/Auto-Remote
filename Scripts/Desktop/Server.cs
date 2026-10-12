#:package Markwardt.ScriptUtilities@0.2.0
#:property TreatWarningsAsErrors=true

// Runs the server locally with a throwaway dev config (access key "dev") under Scripts/Data/, rewritten on each
// start and git-ignored. File-based app (dotnet run) - run from the repo root, e.g.
// `dotnet run Scripts/Desktop/Server.cs`.

using Markwardt.ScriptUtilities;

string config = "Scripts/Data/config.json";

Directory.CreateDirectory("Scripts/Data");
File.WriteAllText(config, """{ "port": 52679, "key": "dev", "dataFolder": "Scripts/Data" }""");

Script.Log("Dev server: port 52679, access key \"dev\" (localhost from the desktop client).");
(await Script.Run("dotnet", "run", "--project", "Server/Server.csproj", "--", config)).Verify();
