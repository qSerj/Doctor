using Avalonia;
using System.Runtime.Versioning;

namespace PsDoctor.App;

[SupportedOSPlatform("windows")]
internal static class Program
{
    /// <summary>Автозапуск при входе: только значок в трее, без окна (Э6.2, часть Д). Дежурство тихое.</summary>
    public const string TrayKey = "--tray";

    [STAThread]
    public static void Main(string[] args)
    {
        using var claim = SingleInstance.Claim(out var first);
        if (!first)
        {
            // Автозапуск при уже открытом Doctor окна не будит: его никто не просил.
            if (!args.Contains(TrayKey))
            {
                SingleInstance.WakeFirst();
            }
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
