using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using PsDoctor.Core.Observation;

namespace PsDoctor.Observer;

/// <summary>Команды повышенному помощнику лежат только в его каталоге; пути чужих файлов он из команды не принимает.</summary>
public sealed record EtwCommand(string Id, string Session, string Action, int RootPid, int ObserverPid, string ProgramImage, int[]? InitialPids = null);
public sealed record EtwStatus(string State, string? Error = null, long LostEvents = 0);
public sealed record EtwRawEvent(DateTime TimeUtc, int ProcessId, string Operation, string? File, long Bytes, int? Status);
public sealed record EtwSummary(DateTime SecondUtc, int ProcessId, string? File, int Opens, int Reads, long ReadBytes,
    int Writes, long WriteBytes, int SharingViolations, string? ProcessStart = null, int? ParentProcessId = null);

public static class EtwFiles
{
    public const int SegmentCount = 4;
    public const int SegmentBytes = 16 * 1024 * 1024;

    public static string Directory(string sessions) => Path.Combine(sessions, "etw");
    public static string Command(string sessions) => Path.Combine(Directory(sessions), "command.json");
    public static string Status(string sessions, string id) => Path.Combine(Directory(sessions), id + ".status.json");
    public static string Summary(string sessions, string id) => Path.Combine(Directory(sessions), id + ".summary.jsonl");
    public static string Raw(string sessions, string id, int segment = 0) =>
        Path.Combine(Directory(sessions), id + ".raw." + segment + ".jsonl");

    public static void WriteAtomically<T>(string path, T value)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, ObservationJson.Options), new UTF8Encoding(false));
        File.Move(temporary, path, true);
    }
}

/// <summary>Отдельный процесс с повышенными правами держит ETW, а обычный Observer читает его файлы.</summary>
public static class EtwHelper
{
    public static async Task<int> RunAsync(string sessions, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) return 2;
        System.IO.Directory.CreateDirectory(EtwFiles.Directory(sessions));
        string? seen = null;
        EtwCapture? capture = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                EtwCommand? command = null;
                try
                {
                    var path = EtwFiles.Command(sessions);
                    if (File.Exists(path))
                        command = JsonSerializer.Deserialize<EtwCommand>(File.ReadAllText(path), ObservationJson.Options);
                }
                catch (Exception error) when (error is IOException or JsonException)
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
                            EtwFiles.WriteAtomically(EtwFiles.Status(sessions, command.Session), new EtwStatus("ready"));
                        }
                        catch (Exception error)
                        {
                            capture?.Dispose();
                            capture = null;
                            EtwFiles.WriteAtomically(EtwFiles.Status(sessions, command.Session),
                                new EtwStatus("failed", error.ToString()));
                        }
                    }
                    else if (command.Action == "stop" && SessionIds.IsValid(command.Session))
                    {
                        EtwFiles.WriteAtomically(EtwFiles.Status(sessions, command.Session), new EtwStatus("stopped", LostEvents: ReadLost(sessions, command.Session)));
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

    private static long ReadLost(string sessions, string id)
    {
        try
        {
            var text = File.ReadAllText(EtwFiles.Status(sessions, id));
            return JsonSerializer.Deserialize<EtwStatus>(text, ObservationJson.Options)?.LostEvents ?? 0;
        }
        catch (Exception error) when (error is IOException or JsonException) { return 0; }
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
    private bool disposed;
    private long lastLost;

    public EtwCapture(string sessions, EtwCommand command)
    {
        this.sessions = sessions;
        this.command = command;
        ObserverPid = command.ObserverPid;
        if (command.RootPid > 0) processes.Add(command.RootPid);
        foreach (var pid in command.InitialPids ?? []) if (pid > 0) processes.Add(pid);
        summary = Open(EtwFiles.Summary(sessions, command.Session));
        raw = Open(EtwFiles.Raw(sessions, command.Session));
    }

    public int ObserverPid { get; }

    public void Start()
    {
        session = new TraceEventSession("PsDoctor-" + command.Session) { StopOnDispose = true };
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
                    data.ImageFileName, data.ParentID));
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
                    new EtwStatus("failed", error.ToString()));
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
                EtwFiles.WriteAtomically(EtwFiles.Status(sessions, command.Session), new EtwStatus("degraded", LostEvents: lost));
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
        raw = Open(EtwFiles.Raw(sessions, command.Session));
    }

    private void WriteSummary(EtwSummary value) =>
        summary.WriteLine(JsonSerializer.Serialize(value, ObservationJson.Options));

    private static StreamWriter Open(string path) => new(
        new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete),
        new UTF8Encoding(false));

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
        }
        lastLost = Math.Max(lastLost, session?.EventsLost ?? 0);
        session?.Dispose();
        if (reader is { IsAlive: true } && reader != Thread.CurrentThread) reader.Join(TimeSpan.FromSeconds(5));
        lock (gate)
        {
            foreach (var (key, value) in totals)
                WriteSummary(new EtwSummary(key.Second, key.Pid, key.File, value.Opens, value.Reads,
                    value.ReadBytes, value.Writes, value.WriteBytes, value.SharingViolations));
            totals.Clear();
            raw.Dispose();
            summary.Dispose();
        }
        EtwFiles.WriteAtomically(EtwFiles.Status(sessions, command.Session),
            new EtwStatus("stopped", LostEvents: lastLost));
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