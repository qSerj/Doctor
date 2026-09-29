using System.Globalization;
using PsDoctor.Core.Observation;

namespace PsDoctor.Observer;

/// <summary>
/// Опрос журнала Windows (Э6.2, часть Г): при старте наблюдателя и раз в <see cref="Interval"/> — события падений куста
/// ProShow после закладки. Новые события дописываются в <see cref="EventsFile"/> каталога сеансов и ложатся фактом в
/// живой сеанс. Закладка — номер записи журнала в <see cref="BookmarkFile"/>: так переживаются перезапуск наблюдателя,
/// перезагрузка и поздний отчёт 1001, а падение без сеанса находится при следующем опросе.
/// </summary>
/// <remarks>
/// Закладка сдвигается после записи в файл: оборвись наблюдатель между ними, те же события придут ещё раз, но не
/// пропадут. Опрос идёт всегда, не только при дежурстве: падение, случившееся без записи, — тоже улика.
/// </remarks>
public sealed class WindowsEventWatch : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    /// <summary>Насколько назад смотреть без закладки: первый запуск и очищенный журнал.</summary>
    public static readonly TimeSpan FirstLookBack = TimeSpan.FromDays(30);

    /// <summary>Предел файла событий: больше — файл уходит в <see cref="OldEventsFile"/>, прежний ротированный пропадает.</summary>
    public const long FileLimit = 10L * 1024 * 1024;

    public const string EventsFile = "windows-events.jsonl";

    public const string OldEventsFile = EventsFile + ".old";

    public const string BookmarkFile = "windows-events.bookmark";

    /// <summary>Сколько остановка ждёт начатого опроса: чтение журнала за месяц — секунды.</summary>
    private static readonly TimeSpan StopWait = TimeSpan.FromSeconds(10);

    private readonly string directory;
    private readonly ObservationService service;
    private readonly IWindowsEventSource source;
    private readonly Func<DateTime> utcNow;
    private readonly Lock gate = new();
    private readonly Lock files = new();
    private Timer? timer;
    private int ticking;
    private DateTime? lastReadUtc;
    private string? lastError;

    public WindowsEventWatch(ObservationService service, IWindowsEventSource source, Func<DateTime>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(source);
        directory = service.DataDirectory;
        this.service = service;
        this.source = source;
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public WindowsEventsStatus Status
    {
        get
        {
            lock (gate)
            {
                return new WindowsEventsStatus(lastReadUtc, lastError);
            }
        }
    }

    /// <summary>Первый опрос — сразу: падение, случившееся, пока наблюдателя не было, находится при старте.</summary>
    public void Start() => timer ??= new Timer(_ => Tick(), null, TimeSpan.Zero, Interval);

    /// <summary>Один опрос. Опрос, начатый, пока идёт прежний, ничего не делает.</summary>
    public void Tick()
    {
        if (Interlocked.Exchange(ref ticking, 1) == 1)
        {
            return;
        }
        string? error = null;
        try
        {
            var since = new DateTimeOffset(utcNow() - FirstLookBack, TimeSpan.Zero);
            var bookmark = ReadBookmark();
            var batch = source.ReadEvents(bookmark, since);
            if (batch.Error is null && batch.Newest < bookmark)
            {
                // Журнал очищен и нумерует записи заново: прежняя закладка пропустила бы всё новое.
                batch = source.ReadEvents(null, since);
            }
            error = batch.Error;
            if (error is null)
            {
                if (batch.Events.Count > 0)
                {
                    Append(batch.Events);
                    foreach (var found in batch.Events)
                    {
                        service.RecordWindowsEvent(found);
                    }
                }
                if (batch.Newest is { } newest && newest != bookmark)
                {
                    WriteBookmark(newest);
                }
            }
        }
        catch (Exception e)
        {
            // Опрос идёт из таймера: необработанное исключение уронило бы наблюдатель. Закладка не сдвинута —
            // следующий опрос прочитает то же.
            error = e.GetType().Name;
        }
        finally
        {
            lock (gate)
            {
                lastError = error;
                if (error is null)
                {
                    lastReadUtc = utcNow();
                }
            }
            Volatile.Write(ref ticking, 0);
        }
    }

    /// <summary>Все найденные события по порядку: сначала ротированный файл, потом нынешний. Нечитаемый файл — пусто.</summary>
    public IReadOnlyList<WindowsEvent> Events()
    {
        try
        {
            lock (files)
            {
                return
                [
                    .. JsonLinesFile.Read<WindowsEvent>(Path.Combine(directory, OldEventsFile), Valid),
                    .. JsonLinesFile.Read<WindowsEvent>(Path.Combine(directory, EventsFile), Valid),
                ];
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Останавливает опрос и дожидается начатого: после остановки файлы в каталоге сеансов никто не пишет.</summary>
    public void Dispose()
    {
        var stopping = Interlocked.Exchange(ref timer, null);
        stopping?.DisposeAsync().AsTask().Wait(StopWait);
    }

    private static bool Valid(WindowsEvent value) => value is { Log: not null, Provider: not null, Properties: not null };

    private void Append(IReadOnlyList<WindowsEvent> events)
    {
        lock (files)
        {
            var path = Path.Combine(directory, EventsFile);
            var file = new FileInfo(path);
            if (file.Exists && file.Length >= FileLimit)
            {
                File.Move(path, Path.Combine(directory, OldEventsFile), overwrite: true);
            }
            JsonLinesFile.Append(path, events);
        }
    }

    /// <summary>Номер записи из файла закладки; нет файла или он испорчен — <c>null</c>, и опрос смотрит на месяц назад.</summary>
    private long? ReadBookmark()
    {
        var path = Path.Combine(directory, BookmarkFile);
        return File.Exists(path)
            && long.TryParse(File.ReadAllText(path).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var recordId)
            ? recordId
            : null;
    }

    /// <summary>Закладка подменяется целиком: оборванная запись не оставит полчисла.</summary>
    private void WriteBookmark(long recordId)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, BookmarkFile);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, recordId.ToString(CultureInfo.InvariantCulture));
        File.Move(temporary, path, overwrite: true);
    }
}
