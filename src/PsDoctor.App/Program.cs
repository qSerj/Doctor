using Avalonia;
using System.Runtime.Versioning;

namespace PsDoctor.App;

[SupportedOSPlatform("windows")]
internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        using var claim = SingleInstance.Claim(out var first);
        if (!first)
        {
            SingleInstance.WakeFirst();
            return;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
