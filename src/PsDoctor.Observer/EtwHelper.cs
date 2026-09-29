using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using PsDoctor.Core.Observation;

namespace PsDoctor.Observer;

/// <summary>Команды повышенному помощнику лежат только в его каталоге; пути чужих файлов он из команды не принимает.</summary>
public sealed record EtwCommand(string Id, string Session, string Action, int RootPid, int ObserverPid, string ProgramImage, int[]? InitialPids = null);
/// <summary>Ответ помощника о записи сеанса.</summary>
/// <param name="Command">
/// Номер команды, на которую это ответ. Мост узнаёт свой ответ по нему, а не по времени записи файла: у Windows оно
/// грубое, часы ВМ прыгают, а в файле сеанса может лежать ответ прежнему помощнику. Нет номера — не ответ ни на что.
/// </param>
public sealed record EtwStatus(string State, string? Error = null, long LostEvents = 0, string? Command = null);
public sealed record EtwRawEvent(DateTime TimeUtc, int ProcessId, string Operation, string? File, long Bytes, int? Status);
/// <param name="CommandLine">Командная строка процесса из события старта: у воркера ProShow она называет входной файл.</param>
public sealed record EtwSummary(DateTime SecondUtc, int ProcessId, string? File, int Opens, int Reads, long ReadBytes,
    int Writes, long WriteBytes, int SharingViolations, string? ProcessStart = null, int? ParentProcessId = null,
    string? CommandLine = null);

/// <summary>Сердцебиение помощника: раз в секунду новое время. По его смене мост знает, что помощник жив.</summary>
public sealed record EtwHeartbeat(int Pid, DateTime TimeUtc);

public static class EtwFiles
{
    public const int SegmentCount = 4;
    public const int SegmentBytes = 16 * 1024 * 1024;

    public static string Directory(string sessions) => Path.Combine(sessions, "etw");
    public static string Command(string sessions) => Path.Combine(Directory(sessions), "command.json");
    public static string Heartbeat(string sessions) => Path.Combine(Directory(sessions), "helper.json");
    public static string Status(string sessions, string id) => Path.Combine(Directory(sessions), id + ".status.json");
    public static string Summary(string sessions, string id) => Path.Combine(Directory(sessions), id + ".summary.jsonl");
    public static string Raw(string sessions, string id, int segment = 0) =>
        Path.Combine(Directory(sessions), id + ".raw." + segment + ".jsonl");

    /// <summary>Сколько раз пробовать подменить файл, пока его держит читатель: до 200 мс.</summary>
    private const int ReplaceAttempts = 10;

    private static readonly TimeSpan ReplacePause = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// Пишет файл целиком и подменяет им прежний. На NTFS открытый файл не подменить, как бы его ни открыли, даже с
    /// <see cref="FileShare.Delete"/>; а другая сторона канала читает его каждые 100 мс–1 с. Читатель держит файл мгновение,
    /// поэтому несколько попыток. Не вышло — временный файл убирается, исключение уходит выше.
    /// </summary>
    public static void WriteAtomically<T>(string path, T value)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, ObservationJson.Options), new UTF8Encoding(false));
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temporary, path, true);
                return;
            }
            catch (Exception error) when ((error is IOException or UnauthorizedAccessException) && attempt < ReplaceAttempts)
            {
                Thread.Sleep(ReplacePause);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                try { File.Delete(temporary); }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
                throw;
            }
        }
    }

    /// <summary>
    /// Файл записи сеанса открывается дозаписью: помощник, поднятый посреди сеанса или после перезагрузки,
    /// не должен укоротить то, что записал прежний.
    /// </summary>
    public static StreamWriter OpenAppend(string path) => new(
        new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete),
        new UTF8Encoding(false));
}

/// <summary>Отдельный процесс с повышенными правами держит ETW, а обычный Observer читает его файлы.</summary>
public static class EtwHelper
{
    public static async Task<int> RunAsync(string sessions, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) return 2;
        System.IO.Directory.CreateDirectory(EtwFiles.Directory(sessions));
        StopOrphanSessions();
        // Команда, лежащая в файле до старта помощника, — от прежнего сеанса Windows или прежнего помощника. Исполнить её
        // значило бы заново открыть запись оборванного сеанса поверх его файлов. Живой сеанс возобновит запись сам:
        // его мост увидит сердцебиение и пришлёт новую команду.
        string? seen = CurrentCommandId(sessions);
        EtwCapture? capture = null;
        var beat = Stopwatch.StartNew();
        WriteHeartbeat(sessions);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (beat.Elapsed >= TimeSpan.FromSeconds(1))
                {
                    WriteHeartbeat(sessions);
                    beat.Restart();
                }
                EtwCommand? command = null;
                try
                {
                    var path = EtwFiles.Command(sessions);
                    if (File.Exists(path))
                        command = JsonSerializer.Deserialize<EtwCommand>(File.ReadAllText(path), ObservationJson.Options);
                }
                catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
                {
                    // Атомарная замена обычно исключает неполный файл; следующий опрос повторит чтение.
                }
                if (command is not null && command.Id != seen)
                {
                    seen = command.Id;
                    capture?.Dispose();
                    capture = null;
                    if (command.Action == "start" && SessionIds.IsValid(command.Session)
                        && command.ObserverPid > 0 && command.RootPid >= 0)
                    {
                        try
                        {
                            capture = new EtwCapture(sessions, command);
                            capture.Start();
                            EtwFiles.WriteAtomically(EtwFiles.Status(sessions, command.Session), new EtwStatus("ready", Command: command.Id));
                        }
                        catch (Exception error)
                        {
                            capture?.Dispose();
                            capture = null;
                            EtwFiles.WriteAtomically(EtwFiles.Status(sessions, command.Session),
                                new EtwStatus("failed", error.ToString(), Command: command.Id));
                        }
                    }
                    else if (command.Action == "stop" && SessionIds.IsValid(command.Session))
                    {
                        EtwFiles.WriteAtomically(EtwFiles.Status(sessions, command.Session),
                            new EtwStatus("stopped", LostEvents: ReadLost(sessions, command.Session), Command: command.Id));
                    }
                }
                capture?.Flush();
                if (capture is not null && !IsAlive(capture.ObserverPid))
                {
                    capture.Dispose();
                    capture = null;
                }
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally { capture?.Dispose(); }
        return 0;
    }

    /// <summary>Номер команды, лежащей в файле сейчас; нет файла или он не читается — <c>null</c>.</summary>
    public static string? CurrentCommandId(string sessions)
    {
        try
        {
            var path = EtwFiles.Command(sessions);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<EtwCommand>(File.ReadAllText(path), ObservationJson.Options)?.Id
                : null;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    private static void WriteHeartbeat(string sessions)
    {
        try { EtwFiles.WriteAtomically(EtwFiles.Heartbeat(sessions), new EtwHeartbeat(Environment.ProcessId, DateTime.UtcNow)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Пропущенный удар мост переживёт: помощник пропавшим считается только после десяти секунд без смены.
        }
    }

    /// <summary>
    /// Ядерные сессии прежнего помощника, снятого без остановки записи, живут до перезагрузки и держат буферы.
    /// Своих у нового помощника ещё нет, поэтому останавливаются все с нашим префиксом.
    /// </summary>
    private static void StopOrphanSessions()
    {
        try
        {
            foreach (var name in TraceEventSession.GetActiveSessionNames())
            {
                if (!name.StartsWith(EtwCapture.SessionPrefix, StringComparison.Ordinal)) continue;
                try { TraceEventSession.GetActiveSession(name)?.Stop(noThrow: true); }
                catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException) { }
    }

    private static long ReadLost(string sessions, string id)
    {
        try
        {
            var text = File.ReadAllText(EtwFiles.Status(sessions, id));
            return JsonSerializer.Deserialize<EtwStatus>(text, ObservationJson.Options)?.LostEvents ?? 0;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return 0; }
    }
    private static bool IsAlive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
}

/// <summary>Сырые файловые события и секундные агрегаты одного сеанса.</summary>
internal sealed class EtwCapture : IDisposable
{
    public const string SessionPrefix = "PsDoctor-";

    private readonly string sessions;
    private readonly EtwCommand command;
    private readonly HashSet<int> processes = [];
    private readonly Dictionary<string, (int Pid, string? File)> pending = [];
    private readonly Dictionary<string, string> fileObjects = [];
    private readonly Dictionary<(DateTime Second, int Pid, string? File), MutableSummary> totals = [];
    private readonly Lock gate = new();
    private readonly StreamWriter summary;
    private StreamWriter raw;
    private TraceEventSession? session;
    private Thread? reader;
    private bool stopping;
    private bool disposed;
    private long lastLost;

    public EtwCapture(string sessions, EtwCommand command)
    {
        this.sessions = sessions;
        this.command = command;
        ObserverPid = command.ObserverPid;
        if (command.RootPid > 0) processes.Add(command.RootPid);
        foreach (var pid in command.InitialPids ?? []) if (pid > 0) processes.Add(pid);
        summary = EtwFiles.OpenAppend(EtwFiles.Summary(sessions, command.Session));
        raw = EtwFiles.OpenAppend(EtwFiles.Raw(sessions, command.Session));
    }

    public int ObserverPid { get; }

    public void Start()
    {
        session = new TraceEventSession(SessionPrefix + command.Session) { StopOnDispose = true };
        session.EnableKernelProvider(KernelTraceEventParser.Keywords.Process |
            KernelTraceEventParser.Keywords.FileIO | KernelTraceEventParser.Keywords.FileIOInit);
        var kernel = session.Source.Kernel;
        kernel.ProcessStart += data =>
        {
            lock (gate)
            {
                if (command.RootPid == 0 && Path.GetFileName(data.ImageFileName)
                    .Equals(command.ProgramImage, StringComparison.OrdinalIgnoreCase))
                    processes.Add(data.ProcessID);
                else if (!processes.Contains(data.ParentID)) return;
                processes.Add(data.ProcessID);
                WriteSummary(new EtwSummary(data.TimeStamp.ToUniversalTime(), data.ProcessID, null, 0, 0, 0, 0, 0, 0,
                    data.ImageFileName, data.ParentID, data.CommandLine));
            }
        };
        kernel.ProcessStop += data => { lock (gate) processes.Remove(data.ProcessID); };
        kernel.FileIOName += data =>
        {
            lock (gate)
            {
                if (!string.IsNullOrEmpty(data.FileName))
                    fileObjects[data.FileKey.ToString()] = data.FileName;
            }
        };
        kernel.FileIOFileRundown += data =>
        {
            lock (gate)
            {
                if (!string.IsNullOrEmpty(data.FileName))
                    fileObjects[data.FileKey.ToString()] = data.FileName;
            }
        };
        kernel.FileIOClose += data =>
        {
            lock (gate)
            {
                fileObjects.Remove(data.FileObject.ToString());
                fileObjects.Remove(data.FileKey.ToString());
            }
        };
        kernel.FileIOCreate += data =>
        {
            lock (gate)
            {
                if (!processes.Contains(data.ProcessID)) return;
                pending[data.IrpPtr.ToString()] = (data.ProcessID, data.FileName);
                if (!string.IsNullOrEmpty(data.FileName)) fileObjects[data.FileObject.ToString()] = data.FileName;
                Add(data.TimeStamp.ToUniversalTime(), data.ProcessID, data.FileName, "open", 0, null);
            }
        };
        kernel.FileIORead += data =>
        {
            lock (gate)
            {
                if (!processes.Contains(data.ProcessID)) return;
                Add(data.TimeStamp.ToUniversalTime(), data.ProcessID, ResolveFile(data.FileName, data.FileObject.ToString(), data.FileKey.ToString()), "read", data.IoSize, null);
            }
        };
        kernel.FileIOWrite += data =>
        {
            lock (gate)
            {
                if (!processes.Contains(data.ProcessID)) return;
                Add(data.TimeStamp.ToUniversalTime(), data.ProcessID, ResolveFile(data.FileName, data.FileObject.ToString(), data.FileKey.ToString()), "write", data.IoSize, null);
            }
        };
        kernel.FileIOOperationEnd += data =>
        {
            lock (gate)
            {
                if (!pending.Remove(data.IrpPtr.ToString(), out var request)) return;
                if (data.NtStatus == unchecked((int)0xC0000043))
                    Add(data.TimeStamp.ToUniversalTime(), request.Pid, request.File, "sharing-violation", 0, data.NtStatus);
            }
        };
        reader = new Thread(() =>
        {
            try { session.Source.Process(); }
            catch (Exception error)
            {
                EtwFiles.WriteAtomically(EtwFiles.Status(sessions, command.Session),
                    new EtwStatus("failed", error.ToString(), Command: command.Id));
            }
        }) { IsBackground = true, Name = "psdoctor-etw" };
        reader.Start();
    }

    public void Flush()
    {
        lock (gate)
        {
            if (disposed) return;
            var now = DateTime.UtcNow;
            foreach (var key in totals.Keys.Where(x => x.Second < now.AddTicks(-(now.Ticks % TimeSpan.TicksPerSecond))).ToArray())
            {
                var value = totals[key];
                WriteSummary(new EtwSummary(key.Second, key.Pid, key.File, value.Opens, value.Reads,
                    value.ReadBytes, value.Writes, value.WriteBytes, value.SharingViolations));
                totals.Remove(key);
            }
            raw.Flush();
            summary.Flush();
            var lost = session?.EventsLost ?? 0;
            if (lost > lastLost)
            {
                lastLost = lost;
                EtwFiles.WriteAtomically(EtwFiles.Status(sessions, command.Session),
                    new EtwStatus("degraded", LostEvents: lost, Command: command.Id));
            }
        }
    }

    private string? ResolveFile(string? name, string fileObject, string fileKey) =>
        !string.IsNullOrEmpty(name) ? name : fileObjects.GetValueOrDefault(fileObject) ?? fileObjects.GetValueOrDefault(fileKey);
    private void Add(DateTime time, int pid, string? file, string operation, long bytes, int? status)
    {
        if (disposed) return;
        var second = time.AddTicks(-(time.Ticks % TimeSpan.TicksPerSecond));
        var key = (second, pid, file);
        if (!totals.TryGetValue(key, out var total)) totals[key] = total = new MutableSummary();
        switch (operation)
        {
            case "open": total.Opens++; break;
            case "read": total.Reads++; total.ReadBytes += bytes; break;
            case "write": total.Writes++; total.WriteBytes += bytes; break;
            case "sharing-violation": total.SharingViolations++; break;
        }
        raw.WriteLine(JsonSerializer.Serialize(new EtwRawEvent(time, pid, operation, file, bytes, status), ObservationJson.Options));
        if (raw.BaseStream.Position >= EtwFiles.SegmentBytes) Rotate();
    }

    private void Rotate()
    {
        raw.Dispose();
        for (var i = EtwFiles.SegmentCount - 1; i >= 1; i--)
        {
            var prior = EtwFiles.Raw(sessions, command.Session, i - 1);
            var next = EtwFiles.Raw(sessions, command.Session, i);
            if (File.Exists(prior)) File.Move(prior, next, true);
        }
        raw = EtwFiles.OpenAppend(EtwFiles.Raw(sessions, command.Session));
    }

    private void WriteSummary(EtwSummary value) =>
        summary.WriteLine(JsonSerializer.Serialize(value, ObservationJson.Options));

    public void Dispose()
    {
        lock (gate)
        {
            if (stopping) return;
            stopping = true;
        }
        // События доходят до потребителя с задержкой буфера ETW, до секунды. Закрытие программы — ровно
        // последняя секунда перед stop (e42-load-001: 260 записей .pxc при выходе). Поэтому буферы сначала
        // сливаются и дочитываются, и только потом Add перестаёт принимать события.
        lastLost = Math.Max(lastLost, session?.EventsLost ?? 0);
        try { session?.Flush(); }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
        session?.Stop(noThrow: true);
        if (reader is { IsAlive: true } && reader != Thread.CurrentThread) reader.Join(TimeSpan.FromSeconds(5));
        session?.Dispose();
        lock (gate)
        {
            disposed = true;
            foreach (var (key, value) in totals)
                WriteSummary(new EtwSummary(key.Second, key.Pid, key.File, value.Opens, value.Reads,
                    value.ReadBytes, value.Writes, value.WriteBytes, value.SharingViolations));
            totals.Clear();
            raw.Dispose();
            summary.Dispose();
        }
        EtwFiles.WriteAtomically(EtwFiles.Status(sessions, command.Session),
            new EtwStatus("stopped", LostEvents: lastLost, Command: command.Id));
    }

    private sealed class MutableSummary
    {
        public int Opens;
        public int Reads;
        public long ReadBytes;
        public int Writes;
        public long WriteBytes;
        public int SharingViolations;
    }
}