using System.Globalization;
using PsDoctor.Core.Observation;

namespace PsDoctor.Observer;

/// <summary>
/// Опрос журналов Windows при старте наблюдателя и раз в <see cref="Interval"/>: падения куста программы в Application
/// (Э6.2, часть Г) и сбои машины в System (Э6.5) — какие журналы, решает источник. Новые события дописываются в файл
/// событий журнала в каталоге сеансов; события журнала, помеченного <see cref="WindowsEventChannel.ToSession"/>, ложатся
/// ещё и фактом в живой сеанс. Закладка — номер записи журнала в файле закладки: так переживаются перезапуск
/// наблюдателя, перезагрузка и поздний отчёт 1001, а событие без сеанса находится при следующем опросе.
/// </summary>
/// <remarks>
/// У каждого журнала своя закладка и свой файл: номера записей у журналов свои, а поток исправленных аппаратных ошибок
/// не должен вытеснить из файла историю падений программы. У Application имена файлов прежние — на машинах они уже
/// лежат. Закладка сдвигается после записи в файл: оборвись наблюдатель между ними, те же события придут ещё раз, но не
/// пропадут. Опрос идёт всегда, не только при дежурстве: падение, случившееся без записи, — тоже улика.
/// </remarks>
public sealed class WindowsEventWatch : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    /// <summary>Насколько назад смотреть без закладки: первый запуск и очищенный журнал.</summary>
    public static readonly TimeSpan FirstLookBack = TimeSpan.FromDays(30);

    /// <summary>Предел файла событий: больше — файл уходит в <see cref="OldEventsFile"/>, прежний ротированный пропадает.</summary>
    public const long FileLimit = 10L * 1024 * 1024;

    /// <summary>Файл событий журнала Application; у других журналов имя журнала — в имени файла (<see cref="EventsFileOf"/>).</summary>
    public const string EventsFile = "windows-events.jsonl";

    public const string OldEventsFile = EventsFile + ".old";

    public const string BookmarkFile = "windows-events.bookmark";

    private const string Application = "Application";

    public static string EventsFileOf(string log) => log == Application ? EventsFile : $"windows-events.{log}.jsonl";

    public static string BookmarkFileOf(string log) => log == Application ? BookmarkFile : $"windows-events.{log}.bookmark";

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

    /// <summary>
    /// Один опрос всех журналов. Сбой одного не мешает другим и виден в статусе: у Application — именем исключения, у
    /// остальных — с именем журнала. Опрос, начатый, пока идёт прежний, ничего не делает.
    /// </summary>
    public void Tick()
    {
        if (Interlocked.Exchange(ref ticking, 1) == 1)
        {
            return;
        }
        var errors = new List<string>();
        var read = false;
        try
        {
            var since = new DateTimeOffset(utcNow() - FirstLookBack, TimeSpan.Zero);
            foreach (var channel in source.EventLogs)
            {
                string? error;
                try
                {
                    error = Poll(channel, since);
                }
                catch (Exception e)
                {
                    // Опрос идёт из таймера: необработанное исключение уронило бы наблюдатель. Закладка не сдвинута —
                    // следующий опрос прочитает то же.
                    error = e.GetType().Name;
                }
                if (error is null)
                {
                    read = true;
                }
                else
                {
                    errors.Add(channel.Log == Application ? error : $"{channel.Log}: {error}");
                }
            }
        }
        catch (Exception e)
        {
            errors.Add(e.GetType().Name);
        }
        finally
        {
            lock (gate)
            {
                lastError = errors.Count == 0 ? null : string.Join("; ", errors);
                if (read)
                {
                    lastReadUtc = utcNow();
                }
            }
            Volatile.Write(ref ticking, 0);
        }
    }

    /// <summary>Опрос одного журнала; сбой чтения — его имя, иначе <c>null</c>.</summary>
    private string? Poll(WindowsEventChannel channel, DateTimeOffset since)
    {
        var bookmark = ReadBookmark(channel.Log);
        var batch = source.ReadEvents(channel.Log, bookmark, since);
        if (batch.Error is null && batch.Newest < bookmark)
        {
            // Журнал очищен и нумерует записи заново: прежняя закладка пропустила бы всё новое.
            batch = source.ReadEvents(channel.Log, null, since);
        }
        if (batch.Error is not null)
        {
            return batch.Error;
        }
        if (batch.Events.Count > 0)
        {
            Append(channel.Log, batch.Events);
            if (channel.ToSession)
            {
                foreach (var found in batch.Events)
                {
                    service.RecordWindowsEvent(found);
                }
            }
        }
        if (batch.Newest is { } newest && newest != bookmark)
        {
            WriteBookmark(channel.Log, newest);
        }
        return null;
    }

    /// <summary>
    /// Все найденные события всех журналов по времени; у журнала сначала ротированный файл, потом нынешний.
    /// Нечитаемый файл — пусто.
    /// </summary>
    public IReadOnlyList<WindowsEvent> Events()
    {
        try
        {
            lock (files)
            {
                return
                [
                    .. source.EventLogs
                        .SelectMany(channel => new[] { EventsFileOf(channel.Log) + ".old", EventsFileOf(channel.Log) })
                        .SelectMany(file => JsonLinesFile.Read<WindowsEvent>(Path.Combine(directory, file), Valid))
                        .OrderBy(found => found.Time),
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

    private void Append(string log, IReadOnlyList<WindowsEvent> events)
    {
        lock (files)
        {
            var path = Path.Combine(directory, EventsFileOf(log));
            var file = new FileInfo(path);
            if (file.Exists && file.Length >= FileLimit)
            {
                File.Move(path, path + ".old", overwrite: true);
            }
            JsonLinesFile.Append(path, events);
        }
    }

    /// <summary>Номер записи из файла закладки; нет файла или он испорчен — <c>null</c>, и опрос смотрит на месяц назад.</summary>
    private long? ReadBookmark(string log)
    {
        var path = Path.Combine(directory, BookmarkFileOf(log));
        return File.Exists(path)
            && long.TryParse(File.ReadAllText(path).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var recordId)
            ? recordId
            : null;
    }

    /// <summary>Закладка подменяется целиком: оборванная запись не оставит полчисла.</summary>
    private void WriteBookmark(string log, long recordId)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, BookmarkFileOf(log));
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, recordId.ToString(CultureInfo.InvariantCulture));
        File.Move(temporary, path, overwrite: true);
    }
}
