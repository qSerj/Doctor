using PsDoctor.Core;
using PsDoctor.Infrastructure;

namespace PsDoctor.Cli;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("psdoctor <путь к .psh>");
            Console.Error.WriteLine("На Э0 умеет только опознать файл шоу. Разбор появится на Э1.");
            return 2;
        }

        var path = args[0];
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"Файл не найден: {path}");
            return 1;
        }

        using var reader = ShowFileEncoding.OpenRead(path);
        var firstLine = reader.ReadLine() ?? string.Empty;
        if (!ShowFile.LooksLikeShowFile(firstLine))
        {
            Console.Error.WriteLine("Это не файл шоу: первая строка не совпала с сигнатурой.");
            return 1;
        }

        Console.WriteLine($"Файл шоу опознан: {Path.GetFileName(path)}");
        return 0;
    }
}
