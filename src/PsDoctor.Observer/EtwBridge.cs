using System.Diagnostics;
using System.Text.Json;
using PsDoctor.Core.Observation;
using PsDoctor.Infrastructure.Observation;

namespace PsDoctor.Observer;

/// <summary>
/// Неповышенный конец файлового канала к отдельному ETW-помощнику. Помощник может не прийти вовсе или упасть
/// посреди сеанса — сеанс от этого не кончается: мост пишет, что запись файловой активности пропала, и
/// возобновляет её, когда помощник снова подаёт сердцебиение (Э6.2, часть А).
/// </summary>
public sealed class EtwBridge : IDisposable
{
    private readonly string sessions;
    private readonly string id;
    private readonly IFactRecorder facts;
    private readonly Func<int> rootPid;
    private readonly string programImage;
    private readonly Func<IReadOnlyCollection<int>?>? processes;
    private readonly TimeSpan helperTimeout;
    private readonly Func<string, EnvironmentFile> describeModule;
    // Загрузки модулей: одна на пару процесс и путь за сеанс, описание файла — одно на путь. Только поток чтения сводки.
    private readonly ModuleLoads modules = new();
    private readonly Dictionary<string, EnvironmentFile> moduleFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource cancellation = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Lock gate = new();
    private readonly Task reader;
    private readonly Task monitor;
    private long lostReported;
    private bool failedReported;
    // Помощник подтвердил запись этого сеанса и с тех пор подаёт сердцебиение; номер команды, на которую он ответил.
    private bool linked;
    private string? linkedCommand;
    // Когда послана команда start, на которую ещё нет ответа, по часам моста — для повтора, и её номер: ответом
    // считается только статус с этим номером, время записи файла для этого не годится.
    private TimeSpan? requested;
    private string? requestedCommand;
    // Команда stop, которой мост закрывается: ответ на неё несёт последний счёт потерь записи.
    private string? stopCommand;
    // Последнее увиденное сердцебиение и когда оно сменилось — по монотонным часам моста: часы Windows
    // на стенде прыгают после паузы ВМ, а смена значения от этого не зависит.
    private DateTime? lastBeat;
    private TimeSpan? beatChanged;
    private bool disposed;

    /// <param name="linkedCommand">Команда start, на которую помощник уже ответил; <c>null</c> — записи ещё нет.</param>
    private EtwBridge(string sessions, string id, IFactRecorder facts, Func<int> rootPid, string programImage,
        Func<IReadOnlyCollection<int>?>? processes, string? linkedCommand, TimeSpan monitorInterval, TimeSpan helperTimeout,
        Func<string, EnvironmentFile>? describeModule)
    {
        this.sessions = sessions;
        this.id = id;
        this.facts = facts;
        this.rootPid = rootPid;
        this.programImage = programImage;
        this.processes = processes;
        this.linkedCommand = linkedCommand;
        linked = linkedCommand is not null;
        this.helperTimeout = helperTimeout;
        this.describeModule = describeModule ?? FileDescription.Describe;
        // Сердцебиение, лежащее на диске до моста, — не признак жизни: после перезагрузки там старое значение.
        lastBeat = ReadHeartbeat(sessions)?.TimeUtc;
        beatChanged = linked ? TimeSpan.Zero : null;
        reader = Task.Run(ReadSummariesAsync);
        monitor = Task.Run(() => MonitorAsync(monitorInterval));
    }

    /// <summary>Сколько ждать ответа помощника на команду начать запись.</summary>
    public static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Сердцебиение не менялось дольше этого — помощник считается пропавшим.</summary>
    public static readonly TimeSpan HelperTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Как часто мост смотрит на сердцебиение помощника.</summary>
    public static readonly TimeSpan MonitorInterval = TimeSpan.FromSeconds(1);

    /// <summary>Через сколько повторить неотвеченную команду start живому помощнику.</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Начинает запись и ждёт подтверждения помощника; нет подтверждения — <see cref="EtwStartException"/>.
    /// Сеансы наблюдателя открывают запись через <see cref="Open"/>, который отказом не кончается.
    /// </summary>
    public static EtwBridge Start(string sessions, string id, int rootPid, string programImage, IFactRecorder facts,
        IReadOnlyCollection<int>? initialPids = null, TimeSpan? readyTimeout = null)
    {
        var command = Request(sessions, id, rootPid, programImage, initialPids, readyTimeout ?? ReadyTimeout);
        facts.Record(ProgramFactKinds.EtwState, new { state = "ready", lostEvents = 0 });
        return new EtwBridge(sessions, id, facts, () => rootPid, programImage, null, command, MonitorInterval, HelperTimeout, null);
    }

    /// <summary>
    /// Начинает запись, если помощник отвечает; не отвечает — сеанс идёт без неё с фактом <c>etw-state unavailable</c>,
    /// а мост начнёт запись, как только помощник подаст сердцебиение. Не бросает <see cref="EtwStartException"/>.
    /// </summary>
    /// <param name="rootPid">Основной процесс программы на момент (пере)подключения записи; 0 — ещё не запущен.</param>
    /// <param name="processes">Процессы куста на момент (пере)подключения записи.</param>
    /// <param name="describeModule">Описание файла загруженного модуля; по умолчанию <see cref="FileDescription.Describe"/>.</param>
    public static EtwBridge Open(string sessions, string id, Func<int> rootPid, string programImage, IFactRecorder facts,
        Func<IReadOnlyCollection<int>?>? processes = null, TimeSpan? readyTimeout = null,
        TimeSpan? monitorInterval = null, TimeSpan? helperTimeout = null, Func<string, EnvironmentFile>? describeModule = null)
    {
        string? command;
        try
        {
            command = Request(sessions, id, rootPid(), programImage, processes?.Invoke(), readyTimeout ?? ReadyTimeout);
            facts.Record(ProgramFactKinds.EtwState, new { state = "ready", lostEvents = 0 });
        }
        catch (EtwStartException error)
        {
            facts.Record(ProgramFactKinds.EtwState, new { state = "unavailable", error = error.Message });
            command = null;
        }
        return new EtwBridge(sessions, id, facts, rootPid, programImage, processes, command,
            monitorInterval ?? MonitorInterval, helperTimeout ?? HelperTimeout, describeModule);
    }

    /// <summary>Шлёт start и ждёт ответа на него; возвращает номер команды, на которую помощник ответил.</summary>
    private static string Request(string sessions, string id, int rootPid, string programImage,
        IReadOnlyCollection<int>? initialPids, TimeSpan readyTimeout)
    {
        // Сердцебиение есть, но давно не обновлялось — помощника нет, десять секунд под замком сервиса ждать нечего.
        if (ReadHeartbeat(sessions) is { } beat && DateTime.UtcNow - beat.TimeUtc > HelperTimeout)
            throw new EtwStartException("helper-not-running");
        var command = new EtwCommand(Guid.NewGuid().ToString("N"), id, "start", rootPid, Environment.ProcessId, programImage, initialPids?.ToArray());
        try { EtwFiles.WriteAtomically(EtwFiles.Command(sessions), command); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new EtwStartException(error.Message);
        }
        var statusPath = EtwFiles.Status(sessions, id);
        var wait = Stopwatch.StartNew();
        while (wait.Elapsed < readyTimeout)
        {
            // Ответ прежнему помощнику или на прежнюю команду может лежать в файле сеанса: он не ответ.
            if (ReadStatus(statusPath) is { } status && status.Command == command.Id)
            {
                if (status.State is "ready" or "degraded")
                    return command.Id;
                if (status.State == "failed")
                    throw new EtwStartException(status.Error ?? ObserverErrors.EtwUnavailable);
            }
            Thread.Sleep(100);
        }
        // Помощник не ответил — возможно, он не запущен. Команда «начать» осталась бы в файле, и помощник, запущенный
        // позже, начал бы писать сырьё сеанса, которого нет. Команда «остановить» её заменяет.
        WriteStop(sessions, id);
        throw new EtwStartException(ObserverErrors.EtwUnavailable);
    }

    public static EtwStatus? ReadStatus(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<EtwStatus>(File.ReadAllText(path), ObservationJson.Options)
                : null;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Последнее сердцебиение помощника; нет файла или он не читается — <c>null</c>.</summary>
    public static EtwHeartbeat? ReadHeartbeat(string sessions)
    {
        try
        {
            var path = EtwFiles.Heartbeat(sessions);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<EtwHeartbeat>(File.ReadAllText(path), ObservationJson.Options)
                : null;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Шлёт stop; номер команды или <c>null</c>, если она не записалась.</summary>
    private static string? WriteStop(string sessions, string id)
    {
        var command = new EtwCommand(Guid.NewGuid().ToString("N"), id, "stop", 0, Environment.ProcessId, "");
        try
        {
            EtwFiles.WriteAtomically(EtwFiles.Command(sessions), command);
            return command.Id;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task MonitorAsync(TimeSpan interval)
    {
        try
        {
            while (true)
            {
                await Task.Delay(interval, cancellation.Token).ConfigureAwait(false);
                Check();
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }

    /// <summary>Жив ли помощник: его сердцебиение сменилось не раньше чем <see cref="helperTimeout"/> назад.</summary>
    private bool HelperAlive()
    {
        var beat = ReadHeartbeat(sessions)?.TimeUtc;
        if (beat is not null && beat != lastBeat)
        {
            lastBeat = beat;
            beatChanged = clock.Elapsed;
        }
        return beatChanged is { } changed && clock.Elapsed - changed <= helperTimeout;
    }

    private void Check()
    {
        lock (gate)
        {
            if (disposed) return;
            var alive = HelperAlive();
            if (linked)
            {
                if (!alive)
                {
                    linked = false;
                    linkedCommand = null;
                    requested = null;
                    requestedCommand = null;
                    Record(new { state = "failed", error = "helper-lost" });
                }
                return;
            }
            if (!alive) return;

            var now = clock.Elapsed;
            if (requested is { } asked)
            {
                // Ответ узнаётся по номеру команды. Время записи файла для этого не годится: у Windows оно грубее
                // часов моста, часы ВМ прыгают, а в файле сеанса может лежать ответ прежнему помощнику.
                if (requestedCommand is not null && ReadStatus(EtwFiles.Status(sessions, id)) is { State: "ready" or "degraded" } status
                    && status.Command == requestedCommand)
                {
                    linked = true;
                    linkedCommand = requestedCommand;
                    requested = null;
                    requestedCommand = null;
                    lostReported = 0;
                    failedReported = false;
                    Record(new { state = "ready", lostEvents = 0 });
                    return;
                }
                if (now - asked < RetryInterval) return;
            }

            var pids = processes?.Invoke();
            var command = new EtwCommand(Guid.NewGuid().ToString("N"), id, "start",
                rootPid(), Environment.ProcessId, programImage, pids?.ToArray());
            try
            {
                EtwFiles.WriteAtomically(EtwFiles.Command(sessions), command);
                requested = now;
                requestedCommand = command.Id;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Следующий опрос повторит.
            }
        }
    }

    private void Record(object state)
    {
        try { facts.Record(ProgramFactKinds.EtwState, state); }
        catch (InvalidOperationException) { }
    }

    private async Task ReadSummariesAsync()
    {
        var path = EtwFiles.Summary(sessions, id);
        try
        {
            while (!File.Exists(path))
                await Task.Delay(100, cancellation.Token).ConfigureAwait(false);
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var lines = new StreamReader(file);
            while (true)
            {
                var line = await lines.ReadLineAsync(cancellation.Token).ConfigureAwait(false);
                if (line is null)
                {
                    ReportStatus();
                    if (disposed && ReadStatus(EtwFiles.Status(sessions, id))?.State == "stopped") return;
                    await Task.Delay(200, cancellation.Token).ConfigureAwait(false);
                    continue;
                }
                EtwSummary? value;
                try { value = JsonSerializer.Deserialize<EtwSummary>(line, ObservationJson.Options); }
                catch (JsonException) { continue; }
                if (value is null) continue;
                try
                {
                    if (value.Module is not null)
                        RecordModule(value);
                    else if (value.ProcessStart is not null)
                        facts.Record(ProgramFactKinds.EtwProcessStarted,
                            new { image = value.ProcessStart, commandLine = value.CommandLine,
                                parentProcessId = value.ParentProcessId, timeUtc = value.SecondUtc }, value.ProcessId);
                    else
                        facts.Record(ProgramFactKinds.FileIo, value, value.ProcessId);
                }
                catch (InvalidOperationException) { return; }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (IOException)
        {
            Record(new { state = "failed", error = "summary-io" });
        }
    }

    /// <summary>
    /// Факт <c>etw-image-loaded</c>: файл описан так же, как в слепке окружения, — по нему видно, какой фильтр или
    /// библиотека из слепка пошли в дело. Один на образ процесса и путь за сеанс, процесс факта — первый загрузивший;
    /// помощник, поднятый заново, присылает загрузки повторно, факт остаётся один.
    /// </summary>
    private void RecordModule(EtwSummary value)
    {
        if (!modules.Add(value.Image, value.ProcessId, value.Module!)) return;
        if (!moduleFiles.TryGetValue(value.Module!, out var file))
            moduleFiles[value.Module!] = file = describeModule(value.Module!);
        facts.Record(ProgramFactKinds.EtwImageLoaded, new { image = value.Image, timeUtc = value.SecondUtc, file }, value.ProcessId);
    }

    private void ReportStatus()
    {
        var status = ReadStatus(EtwFiles.Status(sessions, id));
        if (status is null) return;
        lock (gate)
        {
            // Статус другой команды — от прежней записи или прежнего помощника: о нынешней записи он не говорит.
            // Ответ на stop этого моста — о ней: в нём последний счёт потерь.
            if (!linked || status.Command is null || (status.Command != linkedCommand && status.Command != stopCommand)) return;
            if (status.LostEvents > lostReported)
            {
                lostReported = status.LostEvents;
                Record(new { state = "degraded", lostEvents = lostReported });
            }
            if (status.State == "failed" && !failedReported)
            {
                failedReported = true;
                // Помощник жив, но запись у него сорвалась: мост пошлёт новую команду через RetryInterval.
                linked = false;
                linkedCommand = null;
                requested = clock.Elapsed;
                requestedCommand = null;
                Record(new { state = "failed", error = status.Error });
            }
        }
    }

    public void Dispose()
    {
        bool alive;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            alive = linked || requested is not null;
        }
        try
        {
            var stop = WriteStop(sessions, id);
            lock (gate) stopCommand = stop;
            // Ждать остановки есть смысл, только если помощник вёл запись: иначе закрытие сеанса простояло бы зря.
            if (alive && stop is not null)
            {
                var wait = Stopwatch.StartNew();
                while (wait.Elapsed < TimeSpan.FromSeconds(5))
                {
                    if (ReadStatus(EtwFiles.Status(sessions, id)) is { State: "stopped" } state && state.Command == stop) break;
                    Thread.Sleep(100);
                }
            }
            // Без помощника новых сводок не будет: дочитать уже лежащее хватает доли секунды.
            if (!reader.Wait(alive ? TimeSpan.FromSeconds(2) : TimeSpan.FromMilliseconds(300))) cancellation.Cancel();
            try { reader.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
            cancellation.Cancel();
            try { monitor.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
            ReportStatus();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or AggregateException or ObjectDisposedException)
        {
            Record(new { state = "failed", error = "bridge-io" });
        }
        finally
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
    }

    public static IEnumerable<EtwRawEvent> RawBetween(string sessions, string id, DateTime fromUtc, DateTime toUtc)
    {
        for (var segment = EtwFiles.SegmentCount - 1; segment >= 0; segment--)
        {
            var path = EtwFiles.Raw(sessions, id, segment);
            if (!File.Exists(path)) continue;
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(file);
            while (reader.ReadLine() is { } line)
            {
                EtwRawEvent? value;
                try { value = JsonSerializer.Deserialize<EtwRawEvent>(line, ObservationJson.Options); }
                catch (JsonException) { continue; }
                if (value is not null && value.TimeUtc >= fromUtc && value.TimeUtc <= toUtc)
                    yield return value;
            }
        }
    }
}

public sealed class EtwStartException(string reason) : Exception(reason);
