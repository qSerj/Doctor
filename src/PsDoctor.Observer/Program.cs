using System.Text;
using PsDoctor.Observer;

Console.OutputEncoding = Encoding.UTF8;

if (args.Length > 0 && args[0] == "--etw-helper")
{
    var data = args.Length == 3 && args[1] == "--data" ? args[2] : ObserverOptions.DefaultDataDirectory;
    return await EtwHelper.RunAsync(data);
}

if (args is ["--watchdog"])
{
    using var stopping = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping.Cancel(); };
    return await new Watchdog(PsDoctor.Infrastructure.Installation.InstalledLayout.Current, Environment.ProcessPath!)
        .RunAsync(stopping.Token);
}

var (options, error) = ObserverOptions.Parse(args);
if (options is null)
{
    Console.Error.WriteLine(error);
    return 2;
}

var app = ObserverHost.Build(options);
try
{
    await app.RunAsync();
}
catch (IOException exception)
{
    // Занятый порт или чужой адрес — сбой окружения, как код 3 у CLI.
    Console.Error.WriteLine(exception.Message);
    return 3;
}
return 0;
