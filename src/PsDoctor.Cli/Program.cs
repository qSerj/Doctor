using System.Text;

namespace PsDoctor.Cli;

internal static class Program
{
    public static int Main(string[] args)
    {
        // Отчёт машинный и несёт кириллицу: без этого консоль Windows превратит её в мусор,
        // а перенаправленный в файл отчёт станет нечитаемым для следующего инструмента.
        Console.OutputEncoding = Encoding.UTF8;

        return Runner.Run(args, Console.Out, Console.Error, DateTimeOffset.UtcNow);
    }
}
