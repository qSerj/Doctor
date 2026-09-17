using System.Text;

namespace PsDoctor.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Отчёт машинный и несёт кириллицу: без этого консоль Windows превратит её в мусор,
        // а перенаправленный в файл отчёт станет нечитаемым для следующего инструмента.
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length > 0 && args[0] == "observe")
        {
            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cancellation.Cancel();
            };
            return await ObserveCommand.RunAsync(args[1..], Console.In, Console.Out, Console.Error, Environment.GetEnvironmentVariable, cancellation.Token);
        }

        return Runner.Run(args, Console.Out, Console.Error, DateTimeOffset.UtcNow);
    }
}
