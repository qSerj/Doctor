using System.Text;
using PsDoctor.Observer;

Console.OutputEncoding = Encoding.UTF8;

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
