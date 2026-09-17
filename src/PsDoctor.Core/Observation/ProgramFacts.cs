using PsDoctor.Core.Scenarios;

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

    /// <summary>Замер куста целиком за отрезок между замерами: прирост процессорного времени и ввода-вывода, покой.</summary>
    public const string JobActivity = "job-activity";

    /// <summary>Главное окно появилось, сменило заголовок или признак «не отвечает», пропало.</summary>
    public const string MainWindow = "main-window";

    /// <summary>Диалог — окно куста с владельцем и классом диалога — появился; данные — <see cref="DialogInfo"/>.</summary>
    public const string DialogOpened = "dialog-opened";

    public const string DialogClosed = "dialog-closed";

    /// <summary>Окно куста с владельцем, но не диалог: сигналом сценарию не становится, пишется ради разведки.</summary>
    public const string WindowOpened = "window-opened";

    public const string WindowClosed = "window-closed";

    /// <summary>Действие <c>press</c> нажало кнопку диалога.</summary>
    public const string DialogPressed = "dialog-pressed";

    /// <summary>Действие <c>close</c> отправило главному окну <c>WM_CLOSE</c>.</summary>
    public const string CloseRequested = "close-requested";

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

/// <param name="CpuSeconds">Прирост процессорного времени всего задания за отрезок, включая вышедшие процессы.</param>
/// <param name="IoBytes">Прирост прочитанного и записанного заданием за отрезок, байты; каналы между процессами входят.</param>
/// <param name="Quiet">Куст в покое за отрезок — по <see cref="JobActivity.IsQuiet"/>.</param>
public sealed record JobActivity(double Seconds, double CpuSeconds, long IoBytes, bool Quiet)
{
    /// <summary>
    /// Покой — меньше 2% одного ядра и меньше 64 КБ ввода-вывода за отрезок. Числа не сняты с программы,
    /// а назначены при Ш3 Э4.0: по ним работает только <c>wait idle</c>, и сырьё для пересмотра — в каждом факте.
    /// </summary>
    public static bool IsQuiet(double seconds, double cpuSeconds, long ioBytes) =>
        seconds > 0 && cpuSeconds < 0.02 * seconds && ioBytes < 64 * 1024;
}

/// <param name="Handle">Хэндл окна; <c>null</c> — главного окна нет.</param>
/// <param name="Hung">Windows считает окно не отвечающим (<c>IsHungAppWindow</c>).</param>
public sealed record MainWindowState(long? Handle, string? Title, bool Hung);

public sealed record WindowClosed(long Handle);

/// <param name="Class">Класс окна.</param>
public sealed record OwnedWindow(long Handle, string Class, string? Title);

/// <param name="Method">Чем нажато: <c>uia-invoke</c>.</param>
/// <param name="Result">Итог последнего вызова: <c>invoked</c> или имя сбоя с кодом.</param>
/// <param name="Attempts">
/// Сколько раз вызывали. Только что вставший диалог отвечает на Invoke сбоем COM (Ш3 Э4.0, «Message» через 0.02 с после
/// появления), поэтому сбой вызова при живом диалоге повторяется; сработавшее нажатие не повторяется никогда.
/// </param>
/// <param name="CursorMoved">Курсор сдвинулся между началом и концом нажатия — нажатие не должно его трогать.</param>
public sealed record DialogPressed(long Dialog, long Button, string Text, string Method, string Result, int Attempts, bool CursorMoved);

public sealed record CloseRequested(long Handle);

/// <summary>Причины срыва действий с окнами.</summary>
public static class WindowActionFailures
{
    /// <summary>Программа в сеансе не запущена.</summary>
    public const string NoProgram = "no-program";

    /// <summary>Открытого диалога нет.</summary>
    public const string NoDialog = "no-dialog";

    /// <summary>Ни в одном открытом диалоге нет кнопки с таким текстом.</summary>
    public const string NoButton = "no-button";

    /// <summary>Нажатие не выполнено: вызов вернул сбой или не вернулся.</summary>
    public const string PressFailed = "press-failed";

    /// <summary>Кнопка нажата, а диалог не закрылся.</summary>
    public const string NotClosed = "not-closed";

    /// <summary>Главного окна нет.</summary>
    public const string NoWindow = "no-window";
}

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

    /// <summary>Открытые диалоги куста — те, о которых уже записан факт.</summary>
    IReadOnlyList<DialogInfo> Dialogs();

    /// <summary>
    /// Нажимает кнопку с таким текстом в открытом диалоге (новейший первым) и ждёт, пока диалог закроется.
    /// Мышь, клавиатура и фокус не трогаются.
    /// </summary>
    Task<ActionResult> PressAsync(string button, CancellationToken cancellationToken);

    /// <summary>То же, что крестик главного окна. Не дожидается выхода и программу не убивает.</summary>
    ActionResult Close();
}

/// <summary>События запуска, из которых сеанс делает сигналы сценарию. Факты запуск пишет сам.</summary>
public interface IProgramEvents
{
    /// <summary>Основной процесс вышел. Потомки могут ещё жить.</summary>
    void MainExited(int? exitCode);

    /// <summary>В задании не осталось процессов. Сообщается один раз и последним.</summary>
    void AllExited();

    /// <summary>Заголовок главного окна сменился; <c>null</c> — главного окна нет.</summary>
    void TitleChanged(string? title);

    void DialogAppeared(DialogInfo dialog);

    void DialogDisappeared(long handle);

    /// <summary>Замер куста: был ли он в покое за отрезок.</summary>
    void Activity(bool quiet);
}

public sealed class ProgramLaunchException(string message) : Exception(message);
