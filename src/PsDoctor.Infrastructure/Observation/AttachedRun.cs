using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PsDoctor.Core.Observation;
using PsDoctor.Core.Scenarios;
using static PsDoctor.Infrastructure.Observation.Win32Job;

namespace PsDoctor.Infrastructure.Observation;

/// <summary>Пассивный сеанс над чужим процессом: хэндлы и окна читаются, команд программе нет.</summary>
[SupportedOSPlatform("windows")]
public sealed class AttachedRun : IProgramRun
{
    private readonly ProgramTarget target;
    private readonly IFactRecorder facts;
    private readonly IProgramEvents events;
    private readonly Dictionary<int, TrackedProcess> live = [];
    private readonly Lock gate = new();
    private readonly Timer sampler;
    private readonly WindowWatcher windows;
    private bool disposed;
    private bool mainExited;

    private AttachedRun(ProgramTarget target, IFactRecorder facts, IProgramEvents events)
    {
        this.target = target;
        this.facts = facts;
        this.events = events;
        sampler = new Timer(_ => Refresh(), null, Timeout.Infinite, Timeout.Infinite);
        windows = new WindowWatcher(LiveProcessIds, target.ProcessId, facts, events);
    }

    public int ProcessId => target.ProcessId;

    public static AttachedRun Start(ProgramTarget target, IFactRecorder facts, IProgramEvents events)
    {
        var run = new AttachedRun(target, facts, events);
        try
        {
            run.RefreshCore();
            if (!run.live.ContainsKey(target.ProcessId))
                throw new ProgramAttachException(ObserverErrors.AttachFailed);
            run.windows.Start();
            run.sampler.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            return run;
        }
        catch
        {
            run.Dispose();
            throw;
        }
    }

    public IReadOnlyList<DialogInfo> Dialogs() => windows.Dialogs();

    public Task<ActionResult> PressAsync(string button, CancellationToken cancellationToken) =>
        Task.FromResult(ActionResult.Failed(ObserverErrors.PassiveSession));

    public ActionResult Close() => ActionResult.Failed(ObserverErrors.PassiveSession);

    public Task<ActionResult> RenderAsync(CancellationToken cancellationToken) =>
        Task.FromResult(ActionResult.Failed(ObserverErrors.PassiveSession));

    public void Conclude() { }

    public IReadOnlyCollection<int> LiveProcessIds()
    {
        lock (gate) return [.. live.Keys];
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
        }
        sampler.Dispose();
        windows.Dispose();
        lock (gate)
        {
            foreach (var process in live.Values) CloseHandle(process.Handle);
            live.Clear();
        }
    }

    private void Refresh()
    {
        try
        {
            bool ended;
            lock (gate)
            {
                if (disposed) return;
                RefreshCore();
                ended = mainExited && live.Count == 0;
            }
            if (ended) events.AllExited();
        }
        catch (InvalidOperationException)
        {
            // Журнал закрылся одновременно с замером.
        }
    }

    /// <summary>Снимок дополняет ETW-события и даёт начальный куст; каждый PID сверяется со временем создания.</summary>
    private void RefreshCore()
    {
        var candidates = new List<TrackedProcess>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                var handle = OpenProcess(ProcessQueryLimitedInformation | Synchronize, false, process.Id);
                if (handle == IntPtr.Zero) continue;
                if (!GetProcessTimes(handle, out var created, out _, out _, out _))
                {
                    CloseHandle(handle);
                    continue;
                }
                var parent = Parent(handle);
                candidates.Add(new TrackedProcess(process.Id, created, parent, handle));
            }
        }

        var known = live.Values.Where(old => candidates.Any(x => x.Id == old.Id && x.Created == old.Created))
            .Select(x => x.Id).ToHashSet();
        var root = candidates.Find(x => x.Id == target.ProcessId && x.Created == target.StartedUtc.ToFileTimeUtc());
        if (root is not null) known.Add(root.Id);
        // Потомок может родиться до первого снимка: повторяем проход, пока дерево растёт.
        bool grew;
        do
        {
            grew = false;
            foreach (var candidate in candidates)
            {
                if (known.Contains(candidate.Id) || candidate.Parent is not { } parent || !known.Contains(parent)) continue;
                var parentCreated = candidates.Find(x => x.Id == parent)?.Created ?? live.GetValueOrDefault(parent)?.Created;
                if (parentCreated is null || candidate.Created < parentCreated) continue;
                known.Add(candidate.Id);
                grew = true;
            }
        } while (grew);

        foreach (var candidate in candidates)
        {
            if (!known.Contains(candidate.Id))
            {
                CloseHandle(candidate.Handle);
                continue;
            }
            if (live.TryGetValue(candidate.Id, out var old) && old.Created == candidate.Created)
            {
                CloseHandle(candidate.Handle);
                continue;
            }
            if (old is not null) Exit(old);
            live[candidate.Id] = candidate;
            facts.Record(ProgramFactKinds.ProcessStarted, new ProcessStarted(candidate.Parent, Image(candidate.Handle), null), candidate.Id);
        }

        foreach (var previous in live.Values.ToArray())
        {
            if (candidates.Any(x => x.Id == previous.Id && x.Created == previous.Created)) continue;
            Exit(previous);
        }

        foreach (var process in live.Values)
        {
            var cpu = GetProcessTimes(process.Handle, out _, out _, out var kernel, out var user)
                ? (double?)(kernel + user) / 1e7 : null;
            var hasIo = GetProcessIoCounters(process.Handle, out var io);
            var hasVm = NtQueryInformationProcess(process.Handle, ProcessVmCountersClass, out VmCountersEx vm,
                Marshal.SizeOf<VmCountersEx>(), out _) >= 0;
            facts.Record(ProgramFactKinds.ProcessSample, new ProcessSample(cpu,
                hasIo ? (long)io.ReadTransferCount : null, hasIo ? (long)io.WriteTransferCount : null,
                hasVm ? (long)vm.PagefileUsage : null, hasVm ? (long)vm.PeakPagefileUsage : null,
                hasVm ? (long)vm.VirtualSize : null, hasVm ? (long)vm.PeakVirtualSize : null,
                hasVm ? (long)vm.WorkingSetSize : null,
                GetProcessHandleCount(process.Handle, out var count) ? (int)count : null), process.Id);
        }
    }

    private void Exit(TrackedProcess process)
    {
        if (!live.Remove(process.Id)) return;
        var code = GetExitCodeProcess(process.Handle, out var value) ? unchecked((int)value) : (int?)null;
        var cpu = GetProcessTimes(process.Handle, out _, out _, out var kernel, out var user)
            ? (double?)(kernel + user) / 1e7 : null;
        var hasVm = NtQueryInformationProcess(process.Handle, ProcessVmCountersClass, out VmCountersEx vm,
            Marshal.SizeOf<VmCountersEx>(), out _) >= 0;
        facts.Record(ProgramFactKinds.ProcessExited, new ProcessExited(code, code is not null and not 0,
            cpu, hasVm ? (long)vm.PeakPagefileUsage : null, hasVm ? (long)vm.PeakVirtualSize : null,
            hasVm ? (long)vm.PeakWorkingSetSize : null), process.Id);
        CloseHandle(process.Handle);
        if (process.Id == target.ProcessId)
        {
            mainExited = true;
            events.MainExited(code);
        }
    }

    private static int? Parent(IntPtr handle) =>
        NtQueryInformationProcess(handle, ProcessBasicInformationClass, out ProcessBasicInformation basic,
            Marshal.SizeOf<ProcessBasicInformation>(), out _) >= 0
            ? (int)basic.InheritedFromUniqueProcessId.ToInt64() : null;

    private static string? Image(IntPtr handle)
    {
        var buffer = new char[32768];
        var length = (uint)buffer.Length;
        return QueryFullProcessImageNameW(handle, 0, buffer, ref length) ? new string(buffer, 0, (int)length) : null;
    }

    private sealed record TrackedProcess(int Id, long Created, int? Parent, IntPtr Handle);
}