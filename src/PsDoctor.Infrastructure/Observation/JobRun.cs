using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PsDoctor.Core.Observation;
using static PsDoctor.Infrastructure.Observation.Win32Job;

namespace PsDoctor.Infrastructure.Observation;

/// <summary>
/// Программа под заданием (Job Object): весь куст процессов виден по уведомлениям задания, а не опросом,
/// который теряет короткие процессы (опыт 07). Раз в секунду — замеры живых процессов.
/// </summary>
/// <remarks>
/// <para>
/// Порядок запуска — решение о съёмке: процесс создаётся приостановленным, порт завершения привязан
/// к заданию до <c>AssignProcessToJobObject</c>, поток возобновляется последним. Иначе первые потомки
/// успевают родиться вне задания.
/// </para>
/// <para>
/// «Завершить процессы при закрытии задания» снимается явно: выход наблюдателя не убивает программу.
/// Процесс по возможности вынимается из задания, в котором живёт сам наблюдатель, — задача планировщика
/// держит свои процессы в задании, и её остановка иначе забрала бы программу с собой.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class JobRun : IProgramRun
{
    public static readonly TimeSpan DefaultSampleInterval = TimeSpan.FromSeconds(1);

    // Ключ пакета, которым Dispose будит цикл уведомлений. Задание шлёт свой ключ — хэндл задания, он не ноль.
    private static readonly UIntPtr StopKey = UIntPtr.Zero;

    private readonly IFactRecorder facts;
    private readonly IProgramEvents events;
    private readonly Lock gate = new();

    // Хэндлы живых процессов задания: открываются на уведомлении о новом процессе и держатся до выхода.
    private readonly Dictionary<int, IntPtr> processes = [];
    private readonly HashSet<int> exited = [];
    private readonly IntPtr job;
    private readonly IntPtr port;
    private readonly Thread loop;
    private readonly Timer sampler;
    private bool disposed;
    private bool allExited;

    private JobRun(IntPtr job, IntPtr port, int processId, IntPtr process, IFactRecorder facts, IProgramEvents events, TimeSpan sampleInterval)
    {
        this.job = job;
        this.port = port;
        this.facts = facts;
        this.events = events;
        ProcessId = processId;
        processes[processId] = process;
        loop = new Thread(Listen) { IsBackground = true, Name = "psdoctor-job" };
        sampler = new Timer(_ => Sample(), null, Timeout.InfiniteTimeSpan, sampleInterval);
    }

    public int ProcessId { get; }

    /// <param name="commandLine">Командная строка целиком, с кавычками, первым словом — программа.</param>
    public static JobRun Start(
        string application,
        string commandLine,
        string? workingDirectory,
        IFactRecorder facts,
        IProgramEvents events,
        TimeSpan? sampleInterval = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(application);
        ArgumentException.ThrowIfNullOrEmpty(commandLine);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(events);

        var job = IntPtr.Zero;
        var port = IntPtr.Zero;
        try
        {
            job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
            {
                throw Fail(facts, application, "create-job");
            }

            port = CreateIoCompletionPort(new IntPtr(-1), IntPtr.Zero, UIntPtr.Zero, 1);
            if (port == IntPtr.Zero)
            {
                throw Fail(facts, application, "create-port");
            }

            var association = new AssociateCompletionPort { CompletionKey = job, CompletionPort = port };
            if (!SetInformationJobObject(job, JobObjectAssociateCompletionPortInformation, ref association, Marshal.SizeOf<AssociateCompletionPort>()))
            {
                throw Fail(facts, application, "associate-port");
            }

            var size = Marshal.SizeOf<ExtendedLimitInformation>();
            if (!QueryInformationJobObject(job, JobObjectExtendedLimitInformation, out ExtendedLimitInformation limits, size, IntPtr.Zero))
            {
                throw Fail(facts, application, "query-limits");
            }
            limits.BasicLimitInformation.LimitFlags &= ~JobObjectLimitKillOnJobClose;
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limits, size))
            {
                throw Fail(facts, application, "set-limits");
            }

            var startup = new StartupInfo { Cb = Marshal.SizeOf<StartupInfo>() };
            const uint flags = CreateSuspended | CreateUnicodeEnvironment;
            var brokeAway = true;
            if (!CreateProcessW(application, (commandLine + '\0').ToCharArray(), IntPtr.Zero, IntPtr.Zero, false,
                    flags | CreateBreakawayFromJob, IntPtr.Zero, workingDirectory, ref startup, out var created))
            {
                // «Доступ запрещён» — задание, в котором живёт наблюдатель, не разрешает выход из себя.
                // Тогда программа остаётся и в нём, и в нашем: вложенные задания это допускают.
                if (Marshal.GetLastPInvokeError() != ErrorAccessDenied
                    || !CreateProcessW(application, (commandLine + '\0').ToCharArray(), IntPtr.Zero, IntPtr.Zero, false,
                        flags, IntPtr.Zero, workingDirectory, ref startup, out created))
                {
                    throw Fail(facts, application, "create-process");
                }
                brokeAway = false;
            }

            if (!AssignProcessToJobObject(job, created.Process))
            {
                var exception = Fail(facts, application, "assign-job");
                // Процесс приостановлен и не выполнил ни одной инструкции: убить его безопасно.
                TerminateProcess(created.Process, 1);
                CloseHandle(created.Thread);
                CloseHandle(created.Process);
                throw exception;
            }

            facts.Record(
                ProgramFactKinds.ProgramLaunched,
                new ProgramLaunched(application, commandLine, workingDirectory, brokeAway),
                created.ProcessId);

            var run = new JobRun(job, port, created.ProcessId, created.Process, facts, events, sampleInterval ?? DefaultSampleInterval);
            run.loop.Start();
            ResumeThread(created.Thread);
            CloseHandle(created.Thread);
            run.sampler.Change(TimeSpan.Zero, sampleInterval ?? DefaultSampleInterval);
            return run;
        }
        catch
        {
            if (port != IntPtr.Zero)
            {
                CloseHandle(port);
            }
            if (job != IntPtr.Zero)
            {
                CloseHandle(job);
            }
            throw;
        }
    }

    /// <summary>Прекращает наблюдение. Программа, если жива, продолжает работать.</summary>
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
        }

        sampler.Dispose();
        if (!allExited)
        {
            RecordAccounting();
        }

        PostQueuedCompletionStatus(port, 0, StopKey, IntPtr.Zero);
        if (Thread.CurrentThread != loop && loop.IsAlive)
        {
            loop.Join(TimeSpan.FromSeconds(5));
        }

        lock (gate)
        {
            foreach (var handle in processes.Values)
            {
                CloseHandle(handle);
            }
            processes.Clear();
        }
        CloseHandle(port);
        CloseHandle(job);
    }

    private static ProgramLaunchException Fail(IFactRecorder facts, string application, string stage)
    {
        var error = Marshal.GetLastPInvokeError();
        facts.Record(ProgramFactKinds.LaunchFailed, new LaunchFailed(application, stage, error));
        return new ProgramLaunchException($"{stage}: ошибка Win32 {error}");
    }

    private void Listen()
    {
        try
        {
            ListenCore();
        }
        catch (InvalidOperationException)
        {
            // Журнал сеанса закрыт раньше, чем дошли последние уведомления: писать их некуда.
        }
    }

    private void ListenCore()
    {
        while (GetQueuedCompletionStatus(port, out var message, out var key, out var overlapped, Infinite))
        {
            if (key == StopKey)
            {
                return;
            }

            var processId = (int)overlapped.ToInt64();
            switch (message)
            {
                case JobObjectMsgNewProcess:
                    OnStarted(processId);
                    break;
                case JobObjectMsgExitProcess:
                    OnExited(processId, abnormal: false);
                    break;
                case JobObjectMsgAbnormalExitProcess:
                    OnExited(processId, abnormal: true);
                    break;
                case JobObjectMsgActiveProcessZero:
                    RecordAccounting();
                    allExited = true;
                    events.AllExited();
                    return;
            }
        }
    }

    private void OnStarted(int processId)
    {
        ProcessStarted data;
        lock (gate)
        {
            exited.Remove(processId);
            if (!processes.TryGetValue(processId, out var handle))
            {
                handle = OpenProcess(ProcessQueryLimitedInformation | Synchronize, false, processId);
                if (handle != IntPtr.Zero)
                {
                    processes[processId] = handle;
                }
            }
            data = handle == IntPtr.Zero
                ? new ProcessStarted(null, null, null)
                : new ProcessStarted(ReadParent(handle), ReadImage(handle), ReadCommandLine(handle));
        }
        facts.Record(ProgramFactKinds.ProcessStarted, data, processId);
    }

    private void OnExited(int processId, bool abnormal)
    {
        ProcessExited data;
        lock (gate)
        {
            // Об аварийном выходе задание может сообщить вдобавок к обычному: факт один.
            if (!exited.Add(processId))
            {
                return;
            }
            processes.Remove(processId, out var handle);
            int? exitCode = null;
            if (handle != IntPtr.Zero && GetExitCodeProcess(handle, out var code))
            {
                exitCode = unchecked((int)code);
            }
            var counters = ReadVmCounters(handle);
            data = new ProcessExited(
                exitCode,
                abnormal,
                ReadCpuSeconds(handle),
                (long?)counters?.PeakPagefileUsage,
                (long?)counters?.PeakVirtualSize,
                (long?)counters?.PeakWorkingSetSize);
            if (handle != IntPtr.Zero)
            {
                CloseHandle(handle);
            }
        }
        facts.Record(ProgramFactKinds.ProcessExited, data, processId);
        if (processId == ProcessId)
        {
            events.MainExited(data.ExitCode);
        }
    }

    private void Sample()
    {
        try
        {
            SampleCore();
        }
        catch (InvalidOperationException)
        {
            // Журнал сеанса закрыт между замерами.
        }
    }

    private void SampleCore()
    {
        // Замер под замком: хэндл не закроется посреди опроса. Вызовы короткие, уведомления подождут.
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            foreach (var (processId, handle) in processes)
            {
                var counters = ReadVmCounters(handle);
                long? readBytes = null;
                long? writeBytes = null;
                if (GetProcessIoCounters(handle, out var io))
                {
                    readBytes = (long)io.ReadTransferCount;
                    writeBytes = (long)io.WriteTransferCount;
                }
                int? handles = GetProcessHandleCount(handle, out var count) ? (int)count : null;
                facts.Record(
                    ProgramFactKinds.ProcessSample,
                    new ProcessSample(
                        ReadCpuSeconds(handle),
                        readBytes,
                        writeBytes,
                        (long?)counters?.PagefileUsage,
                        (long?)counters?.PeakPagefileUsage,
                        (long?)counters?.VirtualSize,
                        (long?)counters?.PeakVirtualSize,
                        (long?)counters?.WorkingSetSize,
                        handles),
                    processId);
            }
        }
    }

    private void RecordAccounting()
    {
        if (!QueryInformationJobObject(job, JobObjectBasicAndIoAccountingInformation, out BasicAndIoAccountingInformation accounting,
                Marshal.SizeOf<BasicAndIoAccountingInformation>(), IntPtr.Zero)
            || !QueryInformationJobObject(job, JobObjectExtendedLimitInformation, out ExtendedLimitInformation limits,
                Marshal.SizeOf<ExtendedLimitInformation>(), IntPtr.Zero))
        {
            return;
        }
        facts.Record(ProgramFactKinds.JobAccounting, new JobAccounting(
            (int)accounting.TotalProcesses,
            (int)accounting.ActiveProcesses,
            (int)accounting.TotalTerminatedProcesses,
            (accounting.TotalUserTime + accounting.TotalKernelTime) / 1e7,
            (long)accounting.IoInfo.ReadTransferCount,
            (long)accounting.IoInfo.WriteTransferCount,
            (long)limits.PeakProcessMemoryUsed,
            (long)limits.PeakJobMemoryUsed));
    }

    private static double? ReadCpuSeconds(IntPtr handle) =>
        handle != IntPtr.Zero && GetProcessTimes(handle, out _, out _, out var kernel, out var user)
            ? (kernel + user) / 1e7
            : null;

    private static CountersView? ReadVmCounters(IntPtr handle)
    {
        if (handle == IntPtr.Zero
            || NtQueryInformationProcess(handle, ProcessVmCountersClass, out VmCountersEx counters, Marshal.SizeOf<VmCountersEx>(), out _) < 0)
        {
            return null;
        }
        return new CountersView(
            (ulong)counters.PeakVirtualSize,
            (ulong)counters.VirtualSize,
            (ulong)counters.PeakWorkingSetSize,
            (ulong)counters.WorkingSetSize,
            (ulong)counters.PagefileUsage,
            (ulong)counters.PeakPagefileUsage);
    }

    private static int? ReadParent(IntPtr handle) =>
        NtQueryInformationProcess(handle, ProcessBasicInformationClass, out ProcessBasicInformation basic, Marshal.SizeOf<ProcessBasicInformation>(), out _) >= 0
            ? (int)basic.InheritedFromUniqueProcessId.ToInt64()
            : null;

    private static string? ReadImage(IntPtr handle)
    {
        var buffer = new char[32768];
        var size = (uint)buffer.Length;
        return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
    }

    private static string? ReadCommandLine(IntPtr handle)
    {
        var size = 1024;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = NtQueryInformationProcess(handle, ProcessCommandLineInformationClass, buffer, size, out var needed);
                if (status >= 0)
                {
                    var text = Marshal.PtrToStructure<UnicodeString>(buffer);
                    return text.Buffer == IntPtr.Zero ? "" : Marshal.PtrToStringUni(text.Buffer, text.Length / 2);
                }
                if (status is not (StatusInfoLengthMismatch or StatusBufferTooSmall or StatusBufferOverflow) || needed <= size)
                {
                    return null;
                }
                size = needed;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return null;
    }

    private readonly record struct CountersView(
        ulong PeakVirtualSize,
        ulong VirtualSize,
        ulong PeakWorkingSetSize,
        ulong WorkingSetSize,
        ulong PagefileUsage,
        ulong PeakPagefileUsage);
}
