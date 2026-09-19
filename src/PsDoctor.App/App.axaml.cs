using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using PsDoctor.App.Views;
using System.Runtime.Versioning;

namespace PsDoctor.App;

[SupportedOSPlatform("windows")]
public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var window = new MainWindow();
            desktop.MainWindow = window;
            window.Closing += (_, args) =>
            {
                if (args.CloseReason == WindowCloseReason.WindowClosing)
                {
                    args.Cancel = true;
                    window.Hide();
                }
            };

            using var iconStream = AssetLoader.Open(new Uri("avares://PsDoctor.App/Assets/doctor-tray.png"));
            var open = new NativeMenuItem("Открыть Doctor");
            open.Click += (_, _) => ShowWindow(window);
            var quit = new NativeMenuItem("Выход");
            quit.Click += (_, _) => desktop.Shutdown();
            var menu = new NativeMenu();
            menu.Add(open);
            menu.Add(quit);
            var tray = new TrayIcon
            {
                Icon = new WindowIcon(iconStream),
                ToolTipText = "Doctor для ProShow",
                Menu = menu,
                IsVisible = true,
            };
            tray.Clicked += (_, _) => ShowWindow(window);
            desktop.Exit += (_, _) => tray.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void ShowWindow(MainWindow window)
    {
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
        window.RefreshStatus();
    }
}
