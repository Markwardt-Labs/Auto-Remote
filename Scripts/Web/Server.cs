#:package Markwardt.ScriptUtilities@0.2.0
#:property TreatWarningsAsErrors=true

// Runs the server locally with a throwaway dev config (access key "dev") under Scripts/Data/, rewritten on each
// start and git-ignored. File-based app (dotnet run) - run from the repo root, e.g.
// `dotnet run Scripts/Web/Server.cs`.

using Markwardt.ScriptUtilities;

string config = "Scripts/Data/config.json";

Directory.CreateDirectory("Scripts/Data");
File.WriteAllText(config, """{ "port": 52678, "webPort": 52679, "key": "dev", "dataFolder": "Scripts/Data" }""");

Script.Log("Dev server: WebSocket port 52679, access key \"dev\" (localhost from the browser). Its self-signed certificate must be trusted once: open https://localhost:52679 and accept it.");
(await Script.Run("dotnet", "run", "--project", "Server/Server.csproj", "--", config)).Verify();
