using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
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
            var menu = new NativeMenu();
            var status = new NativeMenuItem("Состояние ProShow");
            status.IsEnabled = false;
            menu.Add(status);
            var open = new NativeMenuItem("Открыть Doctor");
            open.Click += (_, _) => ShowWindow(window);
            menu.Add(open);
            var project = new NativeMenuItem("Открыть проект…");
            project.Click += async (_, _) => { ShowWindow(window); await window.OpenProjectAsync(); };
            menu.Add(project);
            var problem = new NativeMenuItem("Решить проблему");
            problem.Click += async (_, _) => { ShowWindow(window); await window.ShowProblemAsync(); };
            menu.Add(problem);
            var settings = new NativeMenuItem("Настройки пока недоступны");
            settings.IsEnabled = false;
            menu.Add(settings);
            var quit = new NativeMenuItem("Выход");
            quit.Click += (_, _) => desktop.Shutdown();
            menu.Add(quit);
            var tray = new TrayIcon
            {
                Icon = new WindowIcon(iconStream),
                ToolTipText = "Doctor для ProShow",
                Menu = menu,
                IsVisible = true,
            };
            tray.Clicked += (_, _) => ShowWindow(window);
            var statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            statusTimer.Tick += (_, _) => { window.RefreshStatus(); status.Header = window.TrayStatus; };
            statusTimer.Start();
            status.Header = window.TrayStatus;
            desktop.Exit += (_, _) => { statusTimer.Stop(); tray.Dispose(); };
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
