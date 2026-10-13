using Android.App;
using Android.Runtime;
using Avalonia.Android;

namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The Android application object that starts Avalonia with <see cref="App" />.
/// </summary>
/// <param name="handle">The native handle supplied by the Android runtime.</param>
/// <param name="transfer">How the runtime hands over ownership of the handle.</param>
[Application(Icon = "@mipmap/appicon")]
public sealed class AndroidApp(IntPtr handle, JniHandleOwnership transfer) : AvaloniaAndroidApplication<App>(handle, transfer)
{
}
