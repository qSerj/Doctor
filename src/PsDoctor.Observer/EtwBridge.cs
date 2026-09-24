using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using PsDoctor.Core.Observation;

namespace PsDoctor.Observer;

/// <summary>Неповышенный конец файлового канала к отдельному ETW-помощнику.</summary>
public sealed class EtwBridge : IDisposable
{
    private readonly string sessions;
    private readonly string id;
    private readonly IFactRecorder facts;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task reader;
    private long lostReported;
    private bool failedReported;
    private bool disposed;

    private EtwBridge(string sessions, string id, IFactRecorder facts)
    {
        this.sessions = sessions;
        this.id = id;
        this.facts = facts;
        reader = Task.Run(ReadSummariesAsync);
    }

    /// <summary>Сколько ждать ответа помощника на команду начать запись.</summary>
    public static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(10);

    public static EtwBridge Start(string sessions, string id, int rootPid, string programImage, IFactRecorder facts,
        IReadOnlyCollection<int>? initialPids = null, TimeSpan? readyTimeout = null)
    {
        var command = new EtwCommand(Guid.NewGuid().ToString("N"), id, "start", rootPid, Environment.ProcessId, programImage, initialPids?.ToArray());
        try { EtwFiles.WriteAtomically(EtwFiles.Command(sessions), command); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new EtwStartException(error.Message);
        }
        var statusPath = EtwFiles.Status(sessions, id);
        var wait = Stopwatch.StartNew();
        while (wait.Elapsed < (readyTimeout ?? ReadyTimeout))
        {
            var status = ReadStatus(statusPath);
            if (status?.State == "ready")
            {
                facts.Record(ProgramFactKinds.EtwState, new { state = "ready", lostEvents = 0 });
                return new EtwBridge(sessions, id, facts);
            }
            if (status?.State == "failed")
                throw new EtwStartException(status.Error ?? ObserverErrors.EtwUnavailable);
            Thread.Sleep(100);
        }
        // Помощник не ответил — возможно, он не запущен. Команда «начать» осталась бы в файле, и помощник, запущенный
        // позже, начал бы писать сырьё сеанса, которого нет. Команда «остановить» её заменяет.
        try
        {
            EtwFiles.WriteAtomically(EtwFiles.Command(sessions),
                new EtwCommand(Guid.NewGuid().ToString("N"), id, "stop", 0, Environment.ProcessId, ""));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
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
                            new { image = value.ProcessStart, parentProcessId = value.ParentProcessId,
                                timeUtc = value.SecondUtc }, value.ProcessId);
                    else
                        facts.Record(ProgramFactKinds.FileIo, value, value.ProcessId);
                }
                catch (InvalidOperationException) { return; }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (IOException)
        {
            try { facts.Record(ProgramFactKinds.EtwState, new { state = "failed", error = "summary-io" }); }
            catch (InvalidOperationException) { }
        }
    }
    private void ReportStatus()
    {
        var status = ReadStatus(EtwFiles.Status(sessions, id));
        if (status is null) return;
        try
        {
            if (status.LostEvents > lostReported)
            {
                lostReported = status.LostEvents;
                facts.Record(ProgramFactKinds.EtwState, new { state = "degraded", lostEvents = lostReported });
            }
            if (status.State == "failed" && !failedReported)
            {
                failedReported = true;
                facts.Record(ProgramFactKinds.EtwState, new { state = "failed", error = status.Error });
            }
        }
        catch (InvalidOperationException) { }
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

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            EtwFiles.WriteAtomically(EtwFiles.Command(sessions),
                new EtwCommand(Guid.NewGuid().ToString("N"), id, "stop", 0, Environment.ProcessId, ""));
            var wait = Stopwatch.StartNew();
            while (wait.Elapsed < TimeSpan.FromSeconds(5))
            {
                var state = ReadStatus(EtwFiles.Status(sessions, id));
                if (state?.State == "stopped") break;
                Thread.Sleep(100);
            }
            if (!reader.Wait(TimeSpan.FromSeconds(2))) cancellation.Cancel();
            try { reader.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
            ReportStatus();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or AggregateException or ObjectDisposedException)
        {
            try { facts.Record(ProgramFactKinds.EtwState, new { state = "failed", error = "bridge-io" }); }
            catch (InvalidOperationException) { }
        }
        finally
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
    }
}

public sealed class EtwStartException(string reason) : Exception(reason);