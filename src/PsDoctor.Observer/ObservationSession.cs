using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using PsDoctor.Core.Observation;
using PsDoctor.Core.Scenarios;

namespace PsDoctor.Observer;

/// <summary>
/// Сеанс наблюдения — один запуск программы: журнал на диске, журнал в памяти для потока,
/// сигналы выполняемому сценарию. Закрывается, когда в задании не осталось процессов,
/// когда наблюдение прекращено или когда сценарий кончился, так и не запустив программу.
/// </summary>
public sealed class ObservationSession : IProgramEvents
{
    /// <summary>Как часто сценарий узнаёт время: таймауты ожиданий считаются по этим сигналам.</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);

    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly StreamWriter file;
    private readonly Action<ObservationSession> onFinished;
    private readonly Lock gate = new();
    private readonly Timer ticker;
    private readonly TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IProgramRun? program;
    private bool launched;
    private bool mainExited;
    private string? title;
    private bool finishing;
    private Channel<ScenarioSignal>? signals;
    private Task? scenario;

    private ObservationSession(string id, string journalPath, StreamWriter file, Action<ObservationSession> onFinished)
    {
        Id = id;
        JournalPath = journalPath;
        this.file = file;
        this.onFinished = onFinished;
        Log = new FactLog(new FactJournalWriter(file, id, () => clock.Elapsed));
        ticker = new Timer(_ => Send(new TimeTick(clock.Elapsed)), null, TickInterval, TickInterval);
    }

    public string Id { get; }

    public string JournalPath { get; }

    public FactLog Log { get; }

    /// <summary>Сеанс закрывается или закрыт: новый сценарий в нём не выполняется.</summary>
    public bool IsFinishing
    {
        get
        {
            lock (gate)
            {
                return finishing;
            }
        }
    }

    public Task Finished => finished.Task;

    public static ObservationSession Open(string directory, string id, ObserverHealth build, Action<ObservationSession> onFinished)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, id + SessionIds.JournalExtension);
        // Файл открыт на запись одним писателем, читать его можно, пока сеанс идёт.
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var session = new ObservationSession(id, path, writer, onFinished);
        session.Log.Record(ProgramFactKinds.SessionStarted, new { startedAt = DateTimeOffset.Now, observer = build });
        return session;
    }

    /// <summary>Выполняет сценарий в этом сеансе. Одновременно — один: это проверяет вызывающий.</summary>
    public Task<ScenarioOutcome> RunScenario(Scenario text, IScenarioActions actions, TimeSpan? actionTimeout, CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<ScenarioSignal>(new UnboundedChannelOptions { SingleReader = true });
        lock (gate)
        {
            if (finishing)
            {
                throw new InvalidOperationException("сеанс закрывается");
            }
            signals = channel;
            // Состояние программы, известное до сценария, — сценарию в долг: заголовок и выход.
            // Открытые диалоги не передаются: иначе сценарий «press» из пульта остановился бы на том самом
            // диалоге, в котором нажимает, как на неожиданном. Сценарий ждёт только диалогов, вставших при нём.
            if (title is not null)
            {
                channel.Writer.TryWrite(new TitleChanged(clock.Elapsed, title));
            }
            if (mainExited)
            {
                channel.Writer.TryWrite(new ProgramExited(clock.Elapsed, null));
            }
            var task = Execute(text, actions, actionTimeout, channel, cancellationToken);
            scenario = task;
            return task;
        }
    }

    /// <summary>Запуск программы — действие <c>launch</c>. Программа в сеансе одна.</summary>
    public ActionResult Launch(string showPath, IProgramLauncher launcher)
    {
        lock (gate)
        {
            if (finishing)
            {
                return ActionResult.Failed(ObserverErrors.SessionFinished);
            }
            if (launched)
            {
                return ActionResult.Failed(ObserverErrors.ProgramRunning);
            }
            launched = true;
        }

        if (launcher.IsProgramRunning())
        {
            lock (gate)
            {
                launched = false;
            }
            return ActionResult.Failed(ObserverErrors.ProgramRunning);
        }

        IProgramRun run;
        try
        {
            run = launcher.Launch(showPath, Log, this);
        }
        catch (ProgramLaunchException)
        {
            lock (gate)
            {
                launched = false;
            }
            return ActionResult.Failed(ProgramFactKinds.LaunchFailed);
        }

        bool late;
        lock (gate)
        {
            late = finishing;
            program = run;
        }
        // Программа успела выйти целиком, пока запуск возвращался: сеанс уже закрывается без неё.
        if (late)
        {
            run.Dispose();
        }
        return ActionResult.Done;
    }

    /// <summary>Открытые диалоги программы; без программы — пусто.</summary>
    public IReadOnlyList<DialogInfo> Dialogs() => Program()?.Dialogs() ?? [];

    /// <summary>Действие <c>press</c>.</summary>
    public Task<ActionResult> PressAsync(string button, CancellationToken cancellationToken) =>
        Program() is { } run
            ? run.PressAsync(button, cancellationToken)
            : Task.FromResult(ActionResult.Failed(WindowActionFailures.NoProgram));

    /// <summary>Действие <c>close</c>.</summary>
    public ActionResult Close() => Program()?.Close() ?? ActionResult.Failed(WindowActionFailures.NoProgram);

    void IProgramEvents.TitleChanged(string? text)
    {
        lock (gate)
        {
            title = text;
        }
        Send(new TitleChanged(clock.Elapsed, text));
    }

    public void DialogAppeared(DialogInfo dialog) => Send(new DialogOpened(clock.Elapsed, dialog));

    public void DialogDisappeared(long handle) => Send(new DialogClosed(clock.Elapsed, handle));

    public void Activity(bool quiet) => Send(new ActivitySampled(clock.Elapsed, quiet));

    public void MainExited(int? exitCode)
    {
        lock (gate)
        {
            mainExited = true;
        }
        Send(new ProgramExited(clock.Elapsed, exitCode));
    }

    public void AllExited() => _ = FinishAsync(SessionEndReasons.ProgramExited);

    /// <summary>
    /// Закрывает сеанс: сценарий дорабатывает на уже пришедших сигналах, наблюдение за программой
    /// прекращается (программа не закрывается), последний факт — причина закрытия.
    /// </summary>
    public async Task FinishAsync(string reason)
    {
        Task? running;
        Channel<ScenarioSignal>? channel;
        lock (gate)
        {
            if (finishing)
            {
                // Закрывает кто-то другой: дождаться его.
                running = finished.Task;
                channel = null;
            }
            else
            {
                finishing = true;
                running = null;
                channel = signals;
            }
        }
        if (running is not null)
        {
            await running.ConfigureAwait(false);
            return;
        }

        await ticker.DisposeAsync().ConfigureAwait(false);
        Task? scenarioTask;
        lock (gate)
        {
            scenarioTask = scenario;
        }
        // Конец сигналов, а не отмена: сигнал о выходе программы, пришедший раньше, сценарий ещё прочтёт.
        channel?.Writer.TryComplete();
        if (scenarioTask is not null)
        {
            try
            {
                await scenarioTask.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException)
            {
            }
        }

        IProgramRun? run;
        lock (gate)
        {
            run = program;
        }
        run?.Dispose();

        Log.Record(ProgramFactKinds.SessionFinished, new { reason });
        Log.Complete();
        await file.DisposeAsync().ConfigureAwait(false);
        finished.TrySetResult();
        onFinished(this);
    }

    private async Task<ScenarioOutcome> Execute(
        Scenario text,
        IScenarioActions actions,
        TimeSpan? actionTimeout,
        Channel<ScenarioSignal> channel,
        CancellationToken cancellationToken)
    {
        // Выход из-под замка вызывающего: исполнитель пишет факты сразу.
        await Task.Yield();
        try
        {
            return await new ScenarioExecutor(actions, Log, actionTimeout).RunAsync(text, channel.Reader, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            bool empty;
            lock (gate)
            {
                signals = null;
                scenario = null;
                empty = !launched && !finishing;
            }
            // Сценарий кончился, а программы так и нет: сеанс без программы не живёт. Не ждать —
            // закрытие само ждёт выполняемый сценарий, а это он и есть.
            if (empty)
            {
                _ = FinishAsync(SessionEndReasons.NoProgram);
            }
        }
    }

    private IProgramRun? Program()
    {
        lock (gate)
        {
            return finishing ? null : program;
        }
    }

    private void Send(ScenarioSignal signal)
    {
        lock (gate)
        {
            signals?.Writer.TryWrite(signal);
        }
    }
}
