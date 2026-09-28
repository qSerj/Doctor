using System.Diagnostics;
using System.Text.Json;
using PsDoctor.Core.Observation;

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
    private readonly CancellationTokenSource cancellation = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Lock gate = new();
    private readonly Task reader;
    private readonly Task monitor;
    private long lostReported;
    private bool failedReported;
    // Помощник подтвердил запись этого сеанса и с тех пор подаёт сердцебиение.
    private bool linked;
    // Когда послана команда start, на которую ещё нет ответа: по часам моста — для повтора, по часам Windows —
    // для сравнения со временем записи статуса.
    private TimeSpan? requested;
    private DateTime requestedUtc;
    // Последнее увиденное сердцебиение и когда оно сменилось — по монотонным часам моста: часы Windows
    // на стенде прыгают после паузы ВМ, а смена значения от этого не зависит.
    private DateTime? lastBeat;
    private TimeSpan? beatChanged;
    private bool disposed;

    private EtwBridge(string sessions, string id, IFactRecorder facts, Func<int> rootPid, string programImage,
        Func<IReadOnlyCollection<int>?>? processes, bool linked, TimeSpan monitorInterval, TimeSpan helperTimeout)
    {
        this.sessions = sessions;
        this.id = id;
        this.facts = facts;
        this.rootPid = rootPid;
        this.programImage = programImage;
        this.processes = processes;
        this.linked = linked;
        this.helperTimeout = helperTimeout;
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
        Request(sessions, id, rootPid, programImage, initialPids, readyTimeout ?? ReadyTimeout);
        facts.Record(ProgramFactKinds.EtwState, new { state = "ready", lostEvents = 0 });
        return new EtwBridge(sessions, id, facts, () => rootPid, programImage, null, true, MonitorInterval, HelperTimeout);
    }

    /// <summary>
    /// Начинает запись, если помощник отвечает; не отвечает — сеанс идёт без неё с фактом <c>etw-state unavailable</c>,
    /// а мост начнёт запись, как только помощник подаст сердцебиение. Не бросает <see cref="EtwStartException"/>.
    /// </summary>
    /// <param name="rootPid">Основной процесс программы на момент (пере)подключения записи; 0 — ещё не запущен.</param>
    /// <param name="processes">Процессы куста на момент (пере)подключения записи.</param>
    public static EtwBridge Open(string sessions, string id, Func<int> rootPid, string programImage, IFactRecorder facts,
        Func<IReadOnlyCollection<int>?>? processes = null, TimeSpan? readyTimeout = null,
        TimeSpan? monitorInterval = null, TimeSpan? helperTimeout = null)
    {
        bool linked;
        try
        {
            Request(sessions, id, rootPid(), programImage, processes?.Invoke(), readyTimeout ?? ReadyTimeout);
            facts.Record(ProgramFactKinds.EtwState, new { state = "ready", lostEvents = 0 });
            linked = true;
        }
        catch (EtwStartException error)
        {
            facts.Record(ProgramFactKinds.EtwState, new { state = "unavailable", error = error.Message });
            linked = false;
        }
        return new EtwBridge(sessions, id, facts, rootPid, programImage, processes, linked,
            monitorInterval ?? MonitorInterval, helperTimeout ?? HelperTimeout);
    }

    private static void Request(string sessions, string id, int rootPid, string programImage,
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
            var status = ReadStatus(statusPath);
            if (status?.State == "ready")
                return;
            if (status?.State == "failed")
                throw new EtwStartException(status.Error ?? ObserverErrors.EtwUnavailable);
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
        catch (Exception error) when (error is IOException or JsonException) { return null; }
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

    private static void WriteStop(string sessions, string id)
    {
        try
        {
            EtwFiles.WriteAtomically(EtwFiles.Command(sessions),
                new EtwCommand(Guid.NewGuid().ToString("N"), id, "stop", 0, Environment.ProcessId, ""));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
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
                    requested = null;
                    Record(new { state = "failed", error = "helper-lost" });
                }
                return;
            }
            if (!alive) return;

            var now = clock.Elapsed;
            if (requested is { } asked)
            {
                var statusPath = EtwFiles.Status(sessions, id);
                var status = ReadStatus(statusPath);
                // Статус, записанный до команды, — от прежнего помощника: он не ответ.
                if (status?.State is "ready" or "degraded" && File.GetLastWriteTimeUtc(statusPath) >= requestedUtc)
                {
                    linked = true;
                    requested = null;
                    lostReported = 0;
                    failedReported = false;
                    Record(new { state = "ready", lostEvents = 0 });
                    return;
                }
                if (now - asked < RetryInterval) return;
            }

            var pids = processes?.Invoke();
            try
            {
                EtwFiles.WriteAtomically(EtwFiles.Command(sessions), new EtwCommand(Guid.NewGuid().ToString("N"), id, "start",
                    rootPid(), Environment.ProcessId, programImage, pids?.ToArray()));
                requested = now;
                requestedUtc = DateTime.UtcNow;
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
                    if (value.ProcessStart is not null)
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

    private void ReportStatus()
    {
        var status = ReadStatus(EtwFiles.Status(sessions, id));
        if (status is null) return;
        lock (gate)
        {
            if (!linked) return;
            if (status.LostEvents > lostReported)
            {
                lostReported = status.LostEvents;
                Record(new { state = "degraded", lostEvents = lostReported });
            }
            if (status.State == "failed" && !failedReported)
            {
                failedReported = true;
                // Помощник жив, но запись у него сорвалась: мост попробует снова через RetryInterval.
                linked = false;
                requested = clock.Elapsed;
                requestedUtc = DateTime.UtcNow;
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
            WriteStop(sessions, id);
            // Ждать остановки есть смысл, только если помощник вёл запись: иначе закрытие сеанса простояло бы зря.
            if (alive)
            {
                var wait = Stopwatch.StartNew();
                while (wait.Elapsed < TimeSpan.FromSeconds(5))
                {
                    var state = ReadStatus(EtwFiles.Status(sessions, id));
                    if (state?.State == "stopped") break;
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
