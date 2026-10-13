using Android.App;
using Android.Content.PM;
using Avalonia.Android;

namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The Android activity that hosts the Avalonia application.
/// </summary>
[Activity(Label = "Auto Remote", Icon = "@mipmap/appicon", Theme = "@style/Theme.AppCompat.NoActionBar", MainLauncher = true, ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public sealed class MainActivity : AvaloniaMainActivity
{
}
