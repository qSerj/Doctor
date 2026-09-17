namespace PsDoctor.Core.Observation;

/// <summary>Виды фактов сеанса и программы. Новый вид факта — новая строка здесь, а не новая версия протокола.</summary>
public static class ProgramFactKinds
{
    /// <summary>Сеанс открыт: журнал создан, программы ещё нет.</summary>
    public const string SessionStarted = "session-started";

    /// <summary>Основной процесс создан и посажен в задание. Процесс факта — основной.</summary>
    public const string ProgramLaunched = "program-launched";

    /// <summary>Процесс программы не создан или не посажен в задание.</summary>
    public const string LaunchFailed = "launch-failed";

    /// <summary>Новый процесс в задании, включая основной. Процесс факта — новый.</summary>
    public const string ProcessStarted = "process-started";

    public const string ProcessExited = "process-exited";

    /// <summary>Ежесекундный замер процесса, пока он жив.</summary>
    public const string ProcessSample = "process-sample";

    /// <summary>Учёт задания целиком: пишется, когда в задании не осталось процессов, и при прекращении наблюдения.</summary>
    public const string JobAccounting = "job-accounting";

    public const string SessionFinished = "session-finished";
}

/// <summary>Почему закрылся сеанс.</summary>
public static class SessionEndReasons
{
    /// <summary>В задании не осталось ни одного процесса.</summary>
    public const string ProgramExited = "program-exited";

    /// <summary>Прекращено наблюдение; программа, если жива, продолжает работать.</summary>
    public const string Stopped = "stopped";

    /// <summary>Сценарий кончился, а программа так и не была запущена.</summary>
    public const string NoProgram = "no-program";

    /// <summary>Наблюдатель останавливается.</summary>
    public const string ObserverShutdown = "observer-shutdown";
}

/// <param name="WorkingDirectory">Текущий каталог процесса — каталог файла шоу, как при открытии двойным щелчком.</param>
/// <param name="BrokeAwayFromJob">
/// Процесс вынут из задания, в котором живёт сам наблюдатель (планировщик держит свои задачи в задании).
/// <c>false</c> — вынуть не дали, и остановка задачи наблюдателя планировщиком может убить программу.
/// </param>
public sealed record ProgramLaunched(string Application, string CommandLine, string? WorkingDirectory, bool BrokeAwayFromJob);

/// <param name="Error">Код ошибки Win32.</param>
public sealed record LaunchFailed(string Application, string Stage, int Error);

/// <param name="CommandLine">
/// <c>null</c> — не прочиталась: процесс, проживший миллисекунды, мог выйти раньше, чем его открыли.
/// </param>
public sealed record ProcessStarted(int? ParentProcessId, string? Image, string? CommandLine);

/// <summary>
/// Замер процесса. Любое поле — <c>null</c>, если его не удалось снять: процесс мог выйти между замерами.
/// </summary>
/// <param name="PagefileUsage">Выделенная память (commit), байты — то, что упирается в потолок 32-битного процесса.</param>
/// <param name="VirtualSize">Занятое адресное пространство, байты.</param>
public sealed record ProcessSample(
    double? CpuSeconds,
    long? ReadBytes,
    long? WriteBytes,
    long? PagefileUsage,
    long? PeakPagefileUsage,
    long? VirtualSize,
    long? PeakVirtualSize,
    long? WorkingSetSize,
    int? Handles);

/// <param name="Abnormal">Задание сообщило об аварийном выходе.</param>
/// <param name="PeakPagefileUsage">Пик выделенной памяти за жизнь процесса, прочитанный при выходе.</param>
public sealed record ProcessExited(
    int? ExitCode,
    bool Abnormal,
    double? CpuSeconds,
    long? PeakPagefileUsage,
    long? PeakVirtualSize,
    long? PeakWorkingSetSize);

/// <param name="TotalProcesses">Сколько процессов побывало в задании.</param>
/// <param name="PeakProcessMemoryUsed">Пик выделенной памяти одного процесса задания, байты.</param>
public sealed record JobAccounting(
    int TotalProcesses,
    int ActiveProcesses,
    int TerminatedProcesses,
    double CpuSeconds,
    long ReadBytes,
    long WriteBytes,
    long PeakProcessMemoryUsed,
    long PeakJobMemoryUsed);

/// <summary>
/// Запуск программы под наблюдением. Реализация под Windows — в <c>Infrastructure</c>;
/// в тестах API — подмена.
/// </summary>
public interface IProgramLauncher
{
    /// <summary>Программа уже живёт в системе — кем бы она ни была запущена.</summary>
    bool IsProgramRunning();

    /// <summary>
    /// Запускает программу с файлом шоу. Факты о процессах пишутся в <paramref name="facts"/>,
    /// выходы сообщаются в <paramref name="events"/>. Не запустилась — <see cref="ProgramLaunchException"/>,
    /// факт об этом уже записан.
    /// </summary>
    IProgramRun Launch(string showPath, IFactRecorder facts, IProgramEvents events);
}

/// <summary>Живой запуск. <see cref="IDisposable.Dispose"/> прекращает наблюдение, программу не закрывает.</summary>
public interface IProgramRun : IDisposable
{
    int ProcessId { get; }
}

public interface IProgramEvents
{
    /// <summary>Основной процесс вышел. Потомки могут ещё жить.</summary>
    void MainExited(int? exitCode);

    /// <summary>В задании не осталось процессов. Сообщается один раз и последним.</summary>
    void AllExited();
}

public sealed class ProgramLaunchException(string message) : Exception(message);
