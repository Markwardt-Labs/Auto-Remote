#:package Markwardt.ScriptUtilities@0.2.0
#:property TreatWarningsAsErrors=true

// Deploys the Android client to an emulator, starting the first AVD if no device is attached, then launches it
// and streams its log output (console output, crashes) until interrupted. An emulator this script started is
// stopped again when it ends (an already attached device is left alone). Needs the Android workload and an
// Android SDK with at least one AVD, found via ANDROID_HOME, ANDROID_SDK_ROOT or the default install folder.
// Supports RunMobile.task and is not run directly. File-based app (dotnet run) - run from the repo root, e.g.
// `dotnet run Scripts/Support/MobileClient.cs`.

using System.Diagnostics;
using Markwardt.ScriptUtilities;

string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
string sdk = new[]
{
    Environment.GetEnvironmentVariable("ANDROID_HOME"),
    Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"),
    Path.Combine(home, "Android", "Sdk"),
    Path.Combine(home, "Library", "Android", "sdk"),
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk"),
}.FirstOrDefault(path => !string.IsNullOrEmpty(path) && Directory.Exists(path)) ?? throw new InvalidOperationException("Android SDK not found. Set ANDROID_HOME to its folder.");

string adb = Path.Combine(sdk, "platform-tools", "adb");

string package = "com.markwardt.autoremote";

Process? started = null;

void StopEmulator()
{
    if (started is not { HasExited: false })
    {
        return;
    }

    using Process stop = Process.Start(new ProcessStartInfo(adb, ["emu", "kill"]) { RedirectStandardOutput = true, RedirectStandardError = true })!;
    stop.WaitForExit(5000);

    if (!started.WaitForExit(10000))
    {
        started.Kill(true);
    }
}

AppDomain.CurrentDomain.ProcessExit += (_, _) => StopEmulator();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    StopEmulator();
    Environment.Exit(0);
};

if (!(await Script.Run(false, adb, "devices")).Output.Contains("\tdevice"))
{
    string emulator = Path.Combine(sdk, "emulator", "emulator");
    string avd = (await Script.Run(false, emulator, "-list-avds")).Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault()
        ?? throw new InvalidOperationException("No Android device is attached and no AVD exists.");

    Script.Log($"Starting emulator {avd}...");
    started = Process.Start(new ProcessStartInfo(emulator, ["-avd", avd, "-no-metrics"]) { RedirectStandardOutput = true, RedirectStandardError = true })!;
    started.OutputDataReceived += (_, _) => { };
    started.ErrorDataReceived += (_, _) => { };
    started.BeginOutputReadLine();
    started.BeginErrorReadLine();
    (await Script.Run(false, adb, "wait-for-device")).Verify();
}

while (!(await Script.Run(false, adb, "shell", "getprop", "sys.boot_completed")).Output.Trim().Equals("1"))
{
    await Script.Wait(2);
}

(await Script.Run(false, adb, "shell", "input", "keyevent", "KEYCODE_WAKEUP")).Verify();
(await Script.Run(false, adb, "shell", "wm", "dismiss-keyguard")).Verify();

(await Script.Run("dotnet", "build", "Client/Mobile/Mobile.csproj", "-f", "net10.0-android", "-t:Install", $"-p:AndroidSdkDirectory={sdk}")).Verify();

await Script.Run(false, adb, "shell", "am", "force-stop", package);
(await Script.Run(false, adb, "logcat", "-c")).Verify();
(await Script.Run(false, adb, "shell", "monkey", "-p", package, "-c", "android.intent.category.LAUNCHER", "1")).Verify();

string pid = "";

for (int attempt = 0; attempt < 20 && pid.Length == 0; attempt++)
{
    pid = (await Script.Run(false, adb, "shell", "pidof", package)).Output.Trim();

    if (pid.Length == 0)
    {
        await Script.Wait(0.5);
    }
}

if (pid.Length == 0)
{
    throw new InvalidOperationException($"{package} did not start.");
}

Script.Log($"Launched {package} (pid {pid}). Streaming log, Ctrl+C to stop.");
await Script.Run(adb, "logcat", "-v", "time", $"--pid={pid}", "*:I");
