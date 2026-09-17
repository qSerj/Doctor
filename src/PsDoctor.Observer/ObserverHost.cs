using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PsDoctor.Core.Observation;
using PsDoctor.Infrastructure.Observation;

namespace PsDoctor.Observer;

public static class ObserverHost
{
    /// <summary>Пауза, после которой поток без фактов шлёт комментарий: так клиент и прокси видят живое соединение.</summary>
    public static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Собирает хост с маршрутами. Запуск — у вызывающего: Program и тесты. Запускатель программы —
    /// подмена в тестах; без него — ProShow под заданием на Windows и отказ в запуске на других системах.
    /// </summary>
    public static WebApplication Build(ObserverOptions options, IProgramLauncher? launcher = null, TimeSpan? actionTimeout = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(options.Address, options.Port));

        var app = builder.Build();
        var expected = Encoding.UTF8.GetBytes(options.Key);

        // Проверка ключа стоит перед всеми маршрутами: открытых маршрутов у наблюдателя нет.
        app.Use(async (context, next) =>
        {
            if (!HasKey(context.Request.Headers.Authorization.ToString(), expected))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Bearer";
                return;
            }
            await next(context);
        });

        var build = BuildInfo.Of(typeof(ObserverHost).Assembly);
        var health = new ObserverHealth(build.Version, build.Commit);
        var service = new ObservationService(
            options.DataDirectory ?? ObserverOptions.DefaultDataDirectory,
            launcher ?? DefaultLauncher(options),
            health,
            actionTimeout);
        var stopping = app.Lifetime.ApplicationStopping;
        // Остановка наблюдателя закрывает сеанс, но не программу.
        app.Lifetime.ApplicationStopped.Register(() => service.DisposeAsync().AsTask().GetAwaiter().GetResult());

        app.MapGet(ObserverRoutes.Health, () => Results.Json(health, ObservationJson.Options));

        app.MapPost(ObserverRoutes.Scenarios, async (HttpContext context) =>
        {
            RunScenarioRequest? request;
            try
            {
                request = await context.Request.ReadFromJsonAsync<RunScenarioRequest>(ObservationJson.Options, context.RequestAborted);
            }
            catch (JsonException)
            {
                request = null;
            }
            if (request?.Text is null)
            {
                return Results.Json(new ObserverError(ObserverErrors.BadRequest), ObservationJson.Options, statusCode: StatusCodes.Status400BadRequest);
            }
            var result = service.Run(request.Text);
            return result.Accepted is not null
                ? Results.Json(result.Accepted, ObservationJson.Options, statusCode: result.Status)
                : Results.Json(result.Error, ObservationJson.Options, statusCode: result.Status);
        });

        app.MapPost(ObserverRoutes.CancelScenario, () =>
            service.Cancel() is { } session
                ? Results.Json(new CancelAccepted(session), ObservationJson.Options)
                : Results.Json(new ObserverError(ObserverErrors.NothingRunning), ObservationJson.Options, statusCode: StatusCodes.Status409Conflict));

        app.MapGet(ObserverRoutes.Sessions, () => Results.Json(service.Sessions(), ObservationJson.Options));

        app.MapPost("/sessions/{id}/stop", async (string id) =>
        {
            var (status, error) = await service.StopAsync(id);
            return error is null
                ? Results.Json(new CancelAccepted(id), ObservationJson.Options)
                : Results.Json(error, ObservationJson.Options, statusCode: status);
        });

        app.MapGet("/sessions/{id}/dialogs", (string id) =>
        {
            var (dialogs, status, error) = service.Dialogs(id);
            return dialogs is not null
                ? Results.Json(dialogs, ObservationJson.Options)
                : Results.Json(error, ObservationJson.Options, statusCode: status);
        });

        app.MapGet("/sessions/{id}/facts", (HttpContext context, string id) => WriteFactsAsync(context, service, id));

        app.MapGet("/sessions/{id}/stream", (HttpContext context, string id) => StreamAsync(context, service, id, stopping));

        app.MapGet("/sessions/{id}/raw", (string id) =>
            Results.Json(
                new ObserverError(service.Live(id) is not null || service.JournalPath(id) is not null ? ObserverErrors.NoRaw : ObserverErrors.UnknownSession),
                ObservationJson.Options,
                statusCode: StatusCodes.Status404NotFound));

        return app;
    }

    private static IProgramLauncher DefaultLauncher(ObserverOptions options) =>
        OperatingSystem.IsWindows()
            ? new ProShowLauncher(options.ProgramPath ?? ProShowLauncher.DefaultProgramPath)
            : new UnsupportedLauncher();

    /// <summary>Журнал сеанса JSON Lines — сколько есть на момент запроса, с номера.</summary>
    private static async Task WriteFactsAsync(HttpContext context, ObservationService service, string id)
    {
        if (!TryReadQuery(context, out var after, out var kinds))
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, ObserverErrors.BadRequest);
            return;
        }
        var live = service.Live(id);
        var path = live is null ? service.JournalPath(id) : null;
        if (live is null && path is null)
        {
            await WriteErrorAsync(context, StatusCodes.Status404NotFound, ObserverErrors.UnknownSession);
            return;
        }

        context.Response.ContentType = "application/x-ndjson; charset=utf-8";
        var writer = new StreamWriter(context.Response.Body, new UTF8Encoding(false));
        await using (writer.ConfigureAwait(false))
        {
            foreach (var fact in live?.After(after) ?? ReadJournal(path!, after))
            {
                if (kinds is null || kinds.Contains(fact.Kind))
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(fact, ObservationJson.Options));
                }
            }
        }
    }

    /// <summary>
    /// Поток Server-Sent Events: всё с номера, затем новые факты по мере записи. Закрытый сеанс отдаётся
    /// целиком и завершается событием <c>end</c>; живой — так же, когда закроется.
    /// </summary>
    private static async Task StreamAsync(HttpContext context, ObservationService service, string id, CancellationToken stopping)
    {
        if (!TryReadQuery(context, out var after, out var kinds))
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, ObserverErrors.BadRequest);
            return;
        }
        // Переподключение по правилам SSE: номер последнего полученного события.
        if (long.TryParse(context.Request.Headers["Last-Event-ID"].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var lastEvent))
        {
            after = Math.Max(after, lastEvent);
        }

        var live = service.Live(id);
        var path = live is null ? service.JournalPath(id) : null;
        if (live is null && path is null)
        {
            await WriteErrorAsync(context, StatusCodes.Status404NotFound, ObserverErrors.UnknownSession);
            return;
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopping);
        var token = cancellation.Token;
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.Headers.CacheControl = "no-cache";
        var body = context.Response.Body;
        try
        {
            await context.Response.StartAsync(token);
            if (live is null)
            {
                foreach (var fact in ReadJournal(path!, after))
                {
                    await WriteEventAsync(body, fact, kinds, token);
                }
                await WriteRawAsync(body, "event: end\ndata: {}\n\n", token);
                return;
            }

            while (true)
            {
                // Признак закрытия читается до хвоста: закрытый журнал уже не прирастёт, и хвост полон.
                var completed = live.IsCompleted;
                foreach (var fact in live.After(after))
                {
                    await WriteEventAsync(body, fact, kinds, token);
                    after = fact.Number;
                }
                if (completed)
                {
                    await WriteRawAsync(body, "event: end\ndata: {}\n\n", token);
                    return;
                }
                await body.FlushAsync(token);

                var wake = live.WhenAfter(after);
                if (await Task.WhenAny(wake, Task.Delay(Heartbeat, token)) != wake)
                {
                    token.ThrowIfCancellationRequested();
                    await WriteRawAsync(body, ": ping\n\n", token);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Клиент ушёл или наблюдатель останавливается: он переподключится с номера.
        }
        catch (IOException)
        {
        }
    }

    private static IEnumerable<Fact> ReadJournal(string path, long after)
    {
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), Encoding.UTF8);
        foreach (var fact in FactJournalReader.ReadAfter(reader, after))
        {
            yield return fact;
        }
    }

    private static async Task WriteEventAsync(Stream body, Fact fact, HashSet<string>? kinds, CancellationToken token)
    {
        if (kinds is not null && !kinds.Contains(fact.Kind))
        {
            return;
        }
        var json = JsonSerializer.Serialize(fact, ObservationJson.Options);
        await WriteRawAsync(body, $"id: {fact.Number.ToString(CultureInfo.InvariantCulture)}\nevent: fact\ndata: {json}\n\n", token);
    }

    private static async Task WriteRawAsync(Stream body, string text, CancellationToken token)
    {
        await body.WriteAsync(Encoding.UTF8.GetBytes(text), token);
        await body.FlushAsync(token);
    }

    private static Task WriteErrorAsync(HttpContext context, int status, string error)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new ObserverError(error), ObservationJson.Options);
    }

    /// <summary><c>after</c> — неотрицательное число, <c>kind</c> — виды через запятую.</summary>
    private static bool TryReadQuery(HttpContext context, out long after, out HashSet<string>? kinds)
    {
        after = 0;
        kinds = null;
        var text = context.Request.Query["after"].ToString();
        if (text.Length > 0 && !long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out after))
        {
            return false;
        }
        var kind = context.Request.Query["kind"].ToString();
        if (kind.Length > 0)
        {
            kinds = kind.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
        }
        return true;
    }

    private static bool HasKey(string header, byte[] expected)
    {
        const string scheme = "Bearer ";
        if (!header.StartsWith(scheme, StringComparison.Ordinal))
            return false;
        var presented = Encoding.UTF8.GetBytes(header[scheme.Length..].Trim());
        return CryptographicOperations.FixedTimeEquals(presented, expected);
    }
}

/// <summary>Запускатель там, где запускать нечем: наблюдатель вне Windows отвечает на всё, кроме запуска.</summary>
public sealed class UnsupportedLauncher : IProgramLauncher
{
    public bool IsProgramRunning() => false;

    public IProgramRun Launch(string showPath, IFactRecorder facts, IProgramEvents events)
    {
        ArgumentNullException.ThrowIfNull(facts);
        facts.Record(ProgramFactKinds.LaunchFailed, new LaunchFailed(showPath, "platform", 0));
        throw new ProgramLaunchException("запуск программы есть только на Windows");
    }
}

/// <summary>
/// Версия и коммит сборки. Коммит берётся из суффикса InformationalVersion, который SDK
/// дописывает из SourceRevisionId; скрипт лаборатории передаёт его явно, с пометкой -dirty.
/// </summary>
public sealed record BuildInfo(string Version, string? Commit)
{
    public static BuildInfo Of(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        var plus = informational.IndexOf('+');
        return plus < 0
            ? new BuildInfo(informational, null)
            : new BuildInfo(informational[..plus], informational[(plus + 1)..]);
    }
}
