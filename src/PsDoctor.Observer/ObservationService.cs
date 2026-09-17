using PsDoctor.Core.Observation;
using PsDoctor.Core.Scenarios;

namespace PsDoctor.Observer;

/// <summary>
/// Сеансы и сценарии наблюдателя. Живой сеанс — не больше одного, выполняемый сценарий — не больше одного:
/// программа на стенде одна, и второй экземпляр путает кэши и число процессов.
/// </summary>
public sealed class ObservationService : IAsyncDisposable
{
    private readonly string directory;
    private readonly IProgramLauncher launcher;
    private readonly ObserverHealth build;
    private readonly TimeSpan? actionTimeout;
    private readonly Func<DateTime> utcNow;
    private readonly Lock gate = new();
    private ObservationSession? current;
    private CancellationTokenSource? scenario;

    public ObservationService(string directory, IProgramLauncher launcher, ObserverHealth build, TimeSpan? actionTimeout = null, Func<DateTime>? utcNow = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(build);
        this.directory = directory;
        this.launcher = launcher;
        this.build = build;
        this.actionTimeout = actionTimeout;
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public string DataDirectory => directory;

    /// <summary>Итог приёма сценария: принят — сеанс и номер, после которого пойдут его факты; иначе отказ.</summary>
    public sealed record RunResult(RunScenarioAccepted? Accepted, int Status, ObserverError? Error);

    public RunResult Run(string text)
    {
        var parsed = ScenarioParser.Parse(text ?? "");
        if (parsed.Scenario is null)
        {
            return Refuse(StatusCodes.Status400BadRequest, new ObserverError(ObserverErrors.BadScenario, parsed.Errors));
        }

        lock (gate)
        {
            if (scenario is not null)
            {
                return Refuse(StatusCodes.Status409Conflict, new ObserverError(ObserverErrors.ScenarioRunning));
            }

            var session = current is { IsFinishing: false } ? current : null;
            if (parsed.Scenario.Steps[0] is LaunchStep)
            {
                // Отказ до сеанса, с кодом: запущенную программу не трогаем и сеанс под неё не открываем.
                // Сеанс, который закрывается, но ещё не дописал последний факт, — тоже живая программа.
                if (current is { Log.IsCompleted: false } || launcher.IsProgramRunning())
                {
                    return Refuse(StatusCodes.Status409Conflict, new ObserverError(ObserverErrors.ProgramRunning));
                }
                session = ObservationSession.Open(
                    directory,
                    SessionIds.New(utcNow(), id => File.Exists(Path.Combine(directory, id + SessionIds.JournalExtension))),
                    build,
                    OnFinished);
                current = session;
            }
            else if (session is null)
            {
                return Refuse(StatusCodes.Status409Conflict, new ObserverError(ObserverErrors.NoSession));
            }

            var after = session.Log.LastNumber;
            var cancellation = new CancellationTokenSource();
            scenario = cancellation;
            var task = session.RunScenario(parsed.Scenario, new SessionActions(session, launcher), actionTimeout, cancellation.Token);
            _ = task.ContinueWith(_ => OnScenarioDone(cancellation), TaskScheduler.Default);
            return new RunResult(new RunScenarioAccepted(session.Id, after), StatusCodes.Status202Accepted, null);
        }
    }

    /// <summary>Отменяет выполняемый сценарий. Сеанс — <c>null</c>, если отменять нечего.</summary>
    public string? Cancel()
    {
        lock (gate)
        {
            if (scenario is null || current is null)
            {
                return null;
            }
            scenario.Cancel();
            return current.Id;
        }
    }

    /// <summary>Прекращает наблюдение: сценарий отменяется, программа продолжает работать.</summary>
    public async Task<(int Status, ObserverError? Error)> StopAsync(string id)
    {
        ObservationSession? session;
        lock (gate)
        {
            session = current?.Id == id ? current : null;
            if (session is not null)
            {
                scenario?.Cancel();
            }
        }
        if (session is null)
        {
            return JournalPath(id) is null
                ? (StatusCodes.Status404NotFound, new ObserverError(ObserverErrors.UnknownSession))
                : (StatusCodes.Status409Conflict, new ObserverError(ObserverErrors.SessionFinished));
        }
        await session.FinishAsync(SessionEndReasons.Stopped).ConfigureAwait(false);
        return (StatusCodes.Status200OK, null);
    }

    public IReadOnlyList<SessionSummary> Sessions()
    {
        ObservationSession? live;
        lock (gate)
        {
            live = current;
        }
        var ids = Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*" + SessionIds.JournalExtension)
                .Select(Path.GetFileNameWithoutExtension)
                .Where(SessionIds.IsValid)
                .Select(id => id!)
            : [];
        return ids
            .Order(StringComparer.Ordinal)
            .Select(id => id == live?.Id
                ? new SessionSummary(id, !live.Log.IsCompleted, live.Log.LastNumber)
                : new SessionSummary(id, false, null))
            .ToList();
    }

    /// <summary>Открытые диалоги живого сеанса; отказ — у закрытого и неизвестного.</summary>
    public (IReadOnlyList<DialogInfo>? Dialogs, int Status, ObserverError? Error) Dialogs(string id)
    {
        ObservationSession? session;
        lock (gate)
        {
            session = current?.Id == id ? current : null;
        }
        if (session is not null && !session.IsFinishing)
        {
            return (session.Dialogs(), StatusCodes.Status200OK, null);
        }
        return session is null && JournalPath(id) is null
            ? (null, StatusCodes.Status404NotFound, new ObserverError(ObserverErrors.UnknownSession))
            : (null, StatusCodes.Status409Conflict, new ObserverError(ObserverErrors.SessionFinished));
    }

    /// <summary>Журнал в памяти, если сеанс ещё открыт в этом процессе.</summary>
    public FactLog? Live(string id)
    {
        lock (gate)
        {
            return current?.Id == id ? current.Log : null;
        }
    }

    /// <summary>Путь к журналу сеанса на диске или <c>null</c>, если такого сеанса нет.</summary>
    public string? JournalPath(string id)
    {
        if (!SessionIds.IsValid(id))
        {
            return null;
        }
        var path = Path.Combine(directory, id + SessionIds.JournalExtension);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Остановка наблюдателя: сеанс закрывается, программа продолжает работать.</summary>
    public async ValueTask DisposeAsync()
    {
        ObservationSession? session;
        lock (gate)
        {
            session = current;
            scenario?.Cancel();
        }
        if (session is not null)
        {
            await session.FinishAsync(SessionEndReasons.ObserverShutdown).ConfigureAwait(false);
        }
    }

    private static RunResult Refuse(int status, ObserverError error) => new(null, status, error);

    private void OnScenarioDone(CancellationTokenSource cancellation)
    {
        lock (gate)
        {
            if (scenario == cancellation)
            {
                scenario = null;
            }
        }
        cancellation.Dispose();
    }

    private void OnFinished(ObservationSession session)
    {
        lock (gate)
        {
            if (current == session)
            {
                current = null;
            }
        }
    }
}
