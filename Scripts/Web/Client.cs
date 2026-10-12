#:package Markwardt.ScriptUtilities@0.2.0
#:property TreatWarningsAsErrors=true

// Serves Client/Web/index.html on http://localhost:8080/ for local development, reading the file on every
// request so edits show on refresh, and opens it in the default browser. File-based app (dotnet run) - run from the repo root, e.g.
// `dotnet run Scripts/Web/Client.cs`.

using System.Diagnostics;
using System.Net;
using Markwardt.ScriptUtilities;

string page = "Client/Web/index.html";
using HttpListener listener = new();
listener.Prefixes.Add("http://localhost:8080/");
listener.Start();
Script.Log("Web client: http://localhost:8080/ (Ctrl+C to stop).");

if (OperatingSystem.IsWindows())
{
    Process.Start(new ProcessStartInfo("http://localhost:8080/") { UseShellExecute = true });
}
else
{
    await Script.Run(false, OperatingSystem.IsMacOS() ? "open" : "xdg-open", "http://localhost:8080/");
}

while (true)
{
    HttpListenerContext context = await listener.GetContextAsync();
    byte[] content = await File.ReadAllBytesAsync(page);
    context.Response.ContentType = "text/html; charset=utf-8";
    context.Response.Headers["Cache-Control"] = "no-store";
    context.Response.ContentLength64 = content.Length;
    await context.Response.OutputStream.WriteAsync(content);
    context.Response.Close();
}
