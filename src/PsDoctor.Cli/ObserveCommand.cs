using System.Globalization;
using System.Net;
using System.Text.Json;
using PsDoctor.Core.Observation;
using PsDoctor.Core.Scenarios;
using PsDoctor.Observer.Client;

namespace PsDoctor.Cli;

/// <summary>Коды возврата <c>psdoctor observe</c>.</summary>
public static class ObserveExitCodes
{
    /// <summary>Команда выполнена; у <c>run</c> — сценарий выполнен.</summary>
    public const int Done = 0;

    /// <summary>Сценарий принят, но не выполнен: шаг сорвался, встал неожиданный диалог, отменён.</summary>
    public const int ScenarioNotCompleted = 1;

    /// <summary>Наблюдатель отказал: сценарий не разобран, программа уже запущена, сеанса нет. Тело отказа — в stdout.</summary>
    public const int Refused = 2;

    /// <summary>Сбой окружения: нет связи, чужой ключ, оборван поток, неверные аргументы.</summary>
    public const int Environment = 3;
}

/// <summary>
/// <c>psdoctor observe</c> — команды к наблюдателю из оболочки; этим пользуется агент. Вывод машинный:
/// факты — JSON Lines, строка на факт, в том же виде, что в журнале сеанса. Диагностика — только в stderr.
/// </summary>
public static class ObserveCommand
{
    public const string UrlVariable = ObserverConnection.UrlVariable;
    public const string KeyFileVariable = ObserverConnection.KeyFileVariable;

    /// <summary>Сколько раз <c>run</c> переподключается к оборванному потоку фактов, прежде чем считать это сбоем окружения.</summary>
    private const int StreamRetries = 3;

    /// <summary>Пауза между попытками переподключения.</summary>
    private static readonly TimeSpan StreamRetryPause = TimeSpan.FromSeconds(2);

    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        TextReader stdin,
        TextWriter stdout,
        TextWriter stderr,
        Func<string, string?> environment,
        CancellationToken cancellationToken = default,
        HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdin);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        ArgumentNullException.ThrowIfNull(environment);

        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException e)
        {
            stderr.WriteLine(e.Message);
            WriteUsage(stderr);
            return ObserveExitCodes.Environment;
        }
        if (options.Command is null or "help")
        {
            WriteUsage(options.Command is null ? stderr : stdout);
            return options.Command is null ? ObserveExitCodes.Environment : ObserveExitCodes.Done;
        }

        var url = options.Url ?? environment(UrlVariable);
        var keyFile = options.KeyFile ?? environment(KeyFileVariable);
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var address))
        {
            stderr.WriteLine($"Не указан адрес наблюдателя: --url или {UrlVariable}.");
            return ObserveExitCodes.Environment;
        }
        if (keyFile is null)
        {
            stderr.WriteLine($"Не указан файл ключа: --key-file или {KeyFileVariable}.");
            return ObserveExitCodes.Environment;
        }
        string key;
        try
        {
            key = (await File.ReadAllTextAsync(keyFile, cancellationToken).ConfigureAwait(false)).Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"Не удалось прочитать ключ {keyFile}: {e.Message}");
            return ObserveExitCodes.Environment;
        }

        using var client = new ObserverClient(address, key, handler);
        var output = new Output(stdout);
        try
        {
            return options.Command switch
            {
                "health" => output.Json(await client.HealthAsync(cancellationToken).ConfigureAwait(false)),
                "attach" => output.Json(await client.AttachAsync(cancellationToken).ConfigureAwait(false)),
                "raw" => await RawAsync(client, options, stdout, cancellationToken).ConfigureAwait(false),
                "sessions" => output.Lines(await client.SessionsAsync(cancellationToken).ConfigureAwait(false)),
                "cancel" => output.Json(await client.CancelAsync(cancellationToken).ConfigureAwait(false)),
                "confirm" => output.Json(await client.ConfirmAsync(cancellationToken).ConfigureAwait(false)),
                "stop" => await StopAsync(client, options, output, stderr, cancellationToken).ConfigureAwait(false),
                "dialogs" => await DialogsAsync(client, options, output, stderr, cancellationToken).ConfigureAwait(false),
                "facts" => await FactsAsync(client, options, output, cancellationToken).ConfigureAwait(false),
                "run" => await RunScenarioAsync(client, options, stdin, output, stderr, cancellationToken).ConfigureAwait(false),
                _ => Unknown(options.Command, stderr),
            };
        }
        catch (ArgumentException e)
        {
            stderr.WriteLine(e.Message);
            return ObserveExitCodes.Environment;
        }
        catch (ObserverException e) when (e.Error is not null)
        {
            output.Json(e.Error);
            stderr.WriteLine(e.Message);
            return ObserveExitCodes.Refused;
        }
        catch (ObserverException e)
        {
            stderr.WriteLine(e.Status == HttpStatusCode.Unauthorized ? "Наблюдатель не принял ключ (401)." : e.Message);
            return ObserveExitCodes.Environment;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException)
        {
            stderr.WriteLine(output.LastNumber is { } number
                ? $"Связь с наблюдателем оборвана после факта {number.ToString(CultureInfo.InvariantCulture)}: {e.Message}"
                : $"Нет связи с наблюдателем: {e.Message}");
            return ObserveExitCodes.Environment;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (output.LastNumber is { } number)
            {
                stderr.WriteLine($"Прервано после факта {number.ToString(CultureInfo.InvariantCulture)}.");
            }
            return ObserveExitCodes.Environment;
        }
    }

    private static async Task<int> RunScenarioAsync(
        ObserverClient client,
        Options options,
        TextReader stdin,
        Output output,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        string text;
        if (options.Steps.Count > 0)
        {
            text = string.Join('\n', options.Steps);
        }
        else if (options.Arguments.Count == 1)
        {
            text = options.Arguments[0] == "-"
                ? await stdin.ReadToEndAsync(cancellationToken).ConfigureAwait(false)
                : await File.ReadAllTextAsync(options.Arguments[0], cancellationToken).ConfigureAwait(false);
        }
        else
        {
            stderr.WriteLine("run: нужен файл сценария, «-» для stdin или --step.");
            return ObserveExitCodes.Environment;
        }

        var accepted = await client.RunAsync(text, cancellationToken).ConfigureAwait(false);
        stderr.WriteLine($"сеанс {accepted.Session}, факты сценария после {accepted.After.ToString(CultureInfo.InvariantCulture)}");

        ScenarioStatus? status = null;
        var attempts = 0;
        while (true)
        {
            try
            {
                await foreach (var fact in client.StreamAsync(accepted.Session, output.LastNumber ?? accepted.After, null, cancellationToken)
                    .ConfigureAwait(false))
                {
                    output.Fact(fact);
                    if (status is null && fact.Kind == ScenarioFactKinds.ScenarioFinished)
                    {
                        status = fact.Data.GetProperty("status").Deserialize<ScenarioStatus>(ObservationJson.Options);
                        if (!options.Follow)
                        {
                            break;
                        }
                    }
                }
                break;
            }
            // Обрыв соединения — не конец сеанса: номер последнего факта известен, журнал на диске, сервер
            // отдаёт продолжение с этого номера. Рендер идёт часами, и одно соединение его не переживает.
            catch (Exception e) when ((e is IOException or HttpRequestException) && attempts < StreamRetries)
            {
                attempts++;
                stderr.WriteLine($"Поток фактов оборван после факта "
                    + $"{(output.LastNumber ?? accepted.After).ToString(CultureInfo.InvariantCulture)}: {e.Message}; "
                    + $"попытка {attempts.ToString(CultureInfo.InvariantCulture)} из {StreamRetries.ToString(CultureInfo.InvariantCulture)}.");
                await Task.Delay(StreamRetryPause, cancellationToken).ConfigureAwait(false);
            }
        }
        return status == ScenarioStatus.Completed ? ObserveExitCodes.Done : ObserveExitCodes.ScenarioNotCompleted;
    }

    private static async Task<int> FactsAsync(ObserverClient client, Options options, Output output, CancellationToken cancellationToken)
    {
        if (options.Arguments.Count != 1)
        {
            throw new ArgumentException("facts: нужен сеанс.");
        }
        var facts = options.Follow
            ? client.StreamAsync(options.Arguments[0], options.After, options.Kinds, cancellationToken)
            : client.ReadFactsAsync(options.Arguments[0], options.After, options.Kinds, cancellationToken);
        await foreach (var fact in facts.ConfigureAwait(false))
        {
            output.Fact(fact);
        }
        return ObserveExitCodes.Done;
    }

    private static async Task<int> RawAsync(ObserverClient client, Options options, TextWriter stdout, CancellationToken cancellationToken)
    {
        if (options.Arguments.Count != 3
            || !DateTime.TryParse(options.Arguments[1], CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var from)
            || !DateTime.TryParse(options.Arguments[2], CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var to)
            || from > to)
            throw new ArgumentException("raw: нужны сеанс, начало и конец UTC в формате ISO 8601.");
        using var data = new MemoryStream();
        await client.DownloadRawAsync(options.Arguments[0], from, to, data, cancellationToken).ConfigureAwait(false);
        data.Position = 0;
        using var reader = new StreamReader(data);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            await stdout.WriteLineAsync(line).ConfigureAwait(false);
        return ObserveExitCodes.Done;
    }
    private static async Task<int> StopAsync(ObserverClient client, Options options, Output output, TextWriter stderr, CancellationToken cancellationToken)
    {
        var session = await SessionAsync(client, options, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            stderr.WriteLine("stop: живого сеанса нет.");
            return ObserveExitCodes.Refused;
        }
        await client.StopAsync(session, cancellationToken).ConfigureAwait(false);
        return output.Json(new CancelAccepted(session));
    }

    private static async Task<int> DialogsAsync(ObserverClient client, Options options, Output output, TextWriter stderr, CancellationToken cancellationToken)
    {
        var session = await SessionAsync(client, options, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            stderr.WriteLine("dialogs: живого сеанса нет.");
            return ObserveExitCodes.Refused;
        }
        return output.Lines(await client.DialogsAsync(session, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Сеанс из аргумента, иначе живой.</summary>
    private static async Task<string?> SessionAsync(ObserverClient client, Options options, CancellationToken cancellationToken) =>
        options.Arguments.Count == 1
            ? options.Arguments[0]
            : (await client.SessionsAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(s => s.Active)?.Id;

    private static int Unknown(string command, TextWriter stderr)
    {
        stderr.WriteLine($"Неизвестная команда observe: {command}");
        WriteUsage(stderr);
        return ObserveExitCodes.Environment;
    }

    private static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine("psdoctor observe <команда> [ключи]");
        writer.WriteLine();
        writer.WriteLine("  health                          версия и коммит наблюдателя");
        writer.WriteLine("  run <файл|-> | --step <шаг>…    выполнить сценарий, факты сценария — в stdout");
        writer.WriteLine("  attach                          подключиться к работающему ProShow без команд ему");
        writer.WriteLine("  sessions                        сеансы");
        writer.WriteLine("  facts <сеанс>                   журнал сеанса");
        writer.WriteLine("  raw <сеанс> <от UTC> <до UTC>   сырьё ETW за интервал, JSON Lines");
        writer.WriteLine("  cancel                          отменить выполняемый сценарий");
        writer.WriteLine("  confirm                         оператор сделал сказанное: засчитать шагу wait confirm");
        writer.WriteLine("  dialogs [сеанс]                 открытые диалоги с кнопками, строка на диалог");
        writer.WriteLine("  stop [сеанс]                    прекратить наблюдение, программа не закрывается");
        writer.WriteLine();
        writer.WriteLine($"  --url <адрес>                   адрес наблюдателя, иначе {UrlVariable}");
        writer.WriteLine($"  --key-file <файл>               ключ Bearer, иначе {KeyFileVariable}");
        writer.WriteLine("  --follow                        run: до закрытия сеанса; facts: ждать новые факты");
        writer.WriteLine("  --after <номер>                 facts: только после этого номера");
        writer.WriteLine("  --kind <вид,вид>                facts: только эти виды");
        writer.WriteLine();
        writer.WriteLine($"Коды возврата: {ObserveExitCodes.Done} — выполнено; {ObserveExitCodes.ScenarioNotCompleted} — сценарий не выполнен; "
            + $"{ObserveExitCodes.Refused} — отказ наблюдателя; {ObserveExitCodes.Environment} — сбой окружения.");
    }

    /// <summary>Машинный вывод и номер последнего выведенного факта — с него продолжают после обрыва.</summary>
    private sealed class Output(TextWriter writer)
    {
        public long? LastNumber { get; private set; }

        public void Fact(Fact fact)
        {
            writer.WriteLine(JsonSerializer.Serialize(fact, ObservationJson.Options));
            writer.Flush();
            LastNumber = fact.Number;
        }

        public int Json<T>(T value)
        {
            writer.WriteLine(JsonSerializer.Serialize(value, ObservationJson.Options));
            return ObserveExitCodes.Done;
        }

        public int Lines<T>(IEnumerable<T> values)
        {
            foreach (var value in values)
            {
                writer.WriteLine(JsonSerializer.Serialize(value, ObservationJson.Options));
            }
            return ObserveExitCodes.Done;
        }
    }

    private sealed class Options
    {
        public string? Command { get; private set; }

        public List<string> Arguments { get; } = [];

        public List<string> Steps { get; } = [];

        public string? Url { get; private set; }

        public string? KeyFile { get; private set; }

        public bool Follow { get; private set; }

        public long After { get; private set; }

        public List<string>? Kinds { get; private set; }

        public static Options Parse(IReadOnlyList<string> args)
        {
            var options = new Options();
            for (var i = 0; i < args.Count; i++)
            {
                string Value() => ++i < args.Count ? args[i] : throw new ArgumentException($"У ключа {args[i - 1]} не указано значение.");
                switch (args[i])
                {
                    case "--url":
                        options.Url = Value();
                        break;
                    case "--key-file":
                        options.KeyFile = Value();
                        break;
                    case "--step":
                        options.Steps.Add(Value());
                        break;
                    case "--follow":
                        options.Follow = true;
                        break;
                    case "--after":
                        var after = Value();
                        options.After = long.TryParse(after, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                            ? number
                            : throw new ArgumentException($"--after: ожидается номер, получено «{after}».");
                        break;
                    case "--kind":
                        options.Kinds = [.. Value().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
                        break;
                    case "--help" or "-h":
                        options.Command = "help";
                        break;
                    default:
                        if (args[i].StartsWith("--", StringComparison.Ordinal))
                        {
                            throw new ArgumentException($"Неизвестный ключ: {args[i]}");
                        }
                        if (options.Command is null)
                        {
                            options.Command = args[i];
                        }
                        else
                        {
                            options.Arguments.Add(args[i]);
                        }
                        break;
                }
            }
            return options;
        }
    }
}
