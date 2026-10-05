using System.Globalization;
using System.Text;
using System.Text.Json;
using PsDoctor.Core.Observation;

namespace PsDoctor.Observer;

/// <summary>
/// Дневные сводки на машине монтажёра (Э6.6). При старте и раз в <see cref="Interval"/>: замер — слепок окружения и место
/// на дисках, даже если ProShow не открывали; затем недостающие сводки прошлых дней за <see cref="FirstDays"/> дней
/// назад; затем старое за <see cref="Keep"/> удаляется. Сводки копятся здесь, даже если машина инженера выключена месяц.
/// </summary>
/// <remarks>
/// Считает ядро (<see cref="DailySummaries.Build"/>); здесь только часы и файлы. Сводка дня строится один раз, когда день
/// кончился и прошёл <see cref="Grace"/>: опрос журналов Windows идёт раз в минуту, и событие в 23:59 должно успеть лечь в
/// файл. Сегодняшняя сводка строится по запросу и не сохраняется.
/// </remarks>
public sealed class DailySummaryKeeper : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>Сколько после конца дня ждать, прежде чем сводка станет окончательной.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(15);

    /// <summary>Насколько назад строятся недостающие сводки: столько лежат события Windows после первого опроса.</summary>
    public const int FirstDays = 30;

    /// <summary>Сколько живут сводки, замеры и краткие записи сеансов.</summary>
    public static readonly TimeSpan Keep = TimeSpan.FromDays(400);

    /// <summary>Сеанс, начатый раньше дня на столько, ещё может задевать день.</summary>
    private static readonly TimeSpan SessionReach = TimeSpan.FromDays(2);

    public const string DirectoryName = "summaries";
    public const string ReadingsFile = "readings.jsonl";
    public const string CoverageFile = "coverage.json";

    /// <summary>Предел файла замеров: больше — уходит в <c>.old</c>. Замер — пара сотен байтов раз в час.</summary>
    public const long ReadingsLimit = 1024 * 1024;

    /// <summary>Строка журнала сторожа о перезапуске наблюдателя.</summary>
    private const string ObserverRestarted = "observer-restarted";

    private readonly ObservationService service;
    private readonly Func<IReadOnlyList<WindowsEvent>>? events;
    private readonly string? watchdogLog;
    private readonly Func<DateTime> utcNow;
    private readonly Func<DateOnly, TimeSpan> offsetOf;
    private readonly string directory;
    private readonly Lock files = new();
    private Timer? timer;
    private int ticking;

    /// <param name="events">Найденные события журналов Windows; <c>null</c> — журналы не читаются (не Windows).</param>
    /// <param name="watchdogLog">Журнал сторожа; <c>null</c> или нет файла — перезапусков не было.</param>
    /// <param name="offsetOf">Смещение часового пояса машины в начале дня; по умолчанию — местный пояс.</param>
    public DailySummaryKeeper(ObservationService service, Func<IReadOnlyList<WindowsEvent>>? events, string? watchdogLog,
        Func<DateTime>? utcNow = null, Func<DateOnly, TimeSpan>? offsetOf = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        this.service = service;
        this.events = events;
        this.watchdogLog = watchdogLog;
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        this.offsetOf = offsetOf ?? (day => TimeZoneInfo.Local.GetUtcOffset(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local)));
        directory = Path.Combine(service.DataDirectory, DirectoryName);
    }

    /// <summary>Последний сбой прохода; <c>null</c> — прошёл без сбоя.</summary>
    public string? LastError { get; private set; }

    /// <summary>Первый проход — сразу: после месяца без наблюдателя недостающие дни достраиваются при старте.</summary>
    public void Start() => timer ??= new Timer(_ => Tick(), null, TimeSpan.Zero, Interval);

    public void Dispose()
    {
        var stopping = Interlocked.Exchange(ref timer, null);
        stopping?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(10));
    }

    /// <summary>Один проход: замер, недостающие дни, удаление старого. Проход, начатый, пока идёт прежний, ничего не делает.</summary>
    public void Tick()
    {
        if (Interlocked.Exchange(ref ticking, 1) == 1)
        {
            return;
        }
        try
        {
            TakeReading();
            BuildMissing();
            Prune();
            LastError = null;
        }
        catch (Exception e)
        {
            // Проход идёт из таймера: необработанное исключение уронило бы наблюдатель. Следующий проход повторит.
            LastError = e.GetType().Name;
        }
        finally
        {
            Volatile.Write(ref ticking, 0);
        }
    }

    /// <summary>Сегодня по местному времени машины.</summary>
    public DateOnly Today()
    {
        var now = utcNow();
        var guess = DateOnly.FromDateTime(now);
        return DateOnly.FromDateTime(now + offsetOf(guess));
    }

    /// <summary>Сводка дня сейчас, без сохранения: сегодняшняя — неполная.</summary>
    public DailySummary Build(DateOnly day) => Build(day, Sources.Load(this));

    /// <summary>Сохранённая сводка дня; <c>null</c> — её нет или она не читается.</summary>
    public DailySummary? Stored(DateOnly day)
    {
        try
        {
            var path = SummaryPath(day);
            return File.Exists(path) ? DailySummaryJson.Deserialize(File.ReadAllText(path)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Сохранённые сводки с дня <paramref name="from"/> по порядку.</summary>
    public IReadOnlyList<DailySummary> StoredFrom(DateOnly from)
    {
        var days = StoredDays().Where(day => day >= from).Order();
        return [.. days.Select(day => Stored(day)).OfType<DailySummary>()];
    }

    private string SummaryPath(DateOnly day) =>
        Path.Combine(directory, day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".json");

    private IEnumerable<DateOnly> StoredDays()
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }
        return Directory.EnumerateFiles(directory, "*.json")
            .Select(path => DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(path), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var day) ? day : (DateOnly?)null)
            .OfType<DateOnly>()
            .ToList();
    }

    private (DateTimeOffset Start, DateTimeOffset End) Bounds(DateOnly day)
    {
        var start = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), offsetOf(day));
        return (start, start.AddDays(1));
    }

    /// <summary>Замер сейчас: слепок и место. Окружение не читается — замера нет, а не пустой замер.</summary>
    private void TakeReading()
    {
        var facts = service.ReadEnvironmentNow();
        var snapshot = facts?.Snapshot ?? service.TakeEnvironment()?.Id;
        if (facts is null && snapshot is null)
        {
            return;
        }
        var reading = new MachineReading(new DateTimeOffset(utcNow(), TimeSpan.Zero), snapshot, facts?.SystemDisk, facts?.TempDisk);
        lock (files)
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, ReadingsFile);
            var file = new FileInfo(path);
            if (file.Exists && file.Length >= ReadingsLimit)
            {
                File.Move(path, path + ".old", overwrite: true);
            }
            JsonLinesFile.Append(path, new[] { reading });
        }
    }

    private IReadOnlyList<MachineReading> Readings()
    {
        lock (files)
        {
            var path = Path.Combine(directory, ReadingsFile);
            return
            [
                .. JsonLinesFile.Read<MachineReading>(path + ".old", ValidReading)
                    .Concat(JsonLinesFile.Read<MachineReading>(path, ValidReading))
                    .OrderBy(reading => reading.Measured),
            ];
        }
    }

    private static bool ValidReading(MachineReading reading) => reading.Measured != default;

    /// <summary>
    /// Недостающие сводки прошлых дней по порядку, чтобы у каждого дня была история прошлых. Сохранённая сводка не
    /// перестраивается: она уже прочитана или прочтётся такой, какой была в тот день.
    /// </summary>
    private void BuildMissing()
    {
        var now = utcNow();
        var today = Today();
        var stored = StoredDays().ToHashSet();
        Sources? sources = null;
        for (var day = today.AddDays(-FirstDays); day < today; day = day.AddDays(1))
        {
            if (stored.Contains(day) || Bounds(day).End.UtcDateTime + Grace > now)
            {
                continue;
            }
            sources ??= Sources.Load(this);
            Save(Build(day, sources));
        }
    }

    private DailySummary Build(DateOnly day, Sources sources)
    {
        var (start, end) = Bounds(day);
        var history = Enumerable.Range(1, DailySummaries.HistoryDays)
            .Select(back => Stored(day.AddDays(-back)))
            .OfType<DailySummary>()
            .ToList();
        var readings = sources.Readings;
        var inDay = readings.Where(r => r.Measured >= start && r.Measured < end).ToList();
        var snapshotId = inDay.LastOrDefault(r => r.Snapshot is not null)?.Snapshot;
        var previousId = readings.LastOrDefault(r => r.Measured < start && r.Snapshot is not null)?.Snapshot;
        var disks = inDay.LastOrDefault(r => r.System is not null || r.Temp is not null);
        var coverage = sources.Coverage;
        return DailySummaries.Build(new DailySummaryInput(
            day,
            start.Offset,
            Complete: end.UtcDateTime <= utcNow(),
            service.Briefs(start.UtcDateTime - SessionReach, end.UtcDateTime),
            sources.Events,
            sources.Incidents,
            sources.Restarts,
            snapshotId is null ? null : EnvironmentFiles.Load(service.DataDirectory, snapshotId),
            previousId is null ? null : EnvironmentFiles.Load(service.DataDirectory, previousId),
            disks is null ? null : new DiskReading(disks.Measured, disks.System, disks.Temp),
            sources.JournalBytes,
            history,
            EventsCovered: events is not null && start >= coverage.EventsSince,
            SessionsCovered: start >= coverage.SessionsSince));
    }

    private void Save(DailySummary summary)
    {
        lock (files)
        {
            Directory.CreateDirectory(directory);
            var path = SummaryPath(summary.Day);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, DailySummaryJson.Serialize(summary));
            File.Move(temporary, path, overwrite: true);
        }
    }

    /// <summary>Сводки и краткие записи старше <see cref="Keep"/>.</summary>
    private void Prune()
    {
        var oldest = Today().AddDays(-(int)Keep.TotalDays);
        lock (files)
        {
            foreach (var day in StoredDays().Where(day => day < oldest))
            {
                File.Delete(SummaryPath(day));
            }
        }
        service.DeleteBriefs(utcNow() - Keep);
    }

    /// <summary>
    /// С какого мига данные за день полны. Ставится один раз, при первом проходе: события Windows опрос берёт за
    /// <see cref="WindowsEventWatch.FirstLookBack"/> назад, журналы сеансов — сколько их оставило хранение; дальше краткие
    /// записи переживают журналы, и граница не сдвигается.
    /// </summary>
    private Coverage LoadCoverage()
    {
        var path = Path.Combine(directory, CoverageFile);
        lock (files)
        {
            try
            {
                if (File.Exists(path) && JsonSerializer.Deserialize<Coverage>(File.ReadAllText(path), ObservationJson.Options) is { } stored)
                {
                    return stored;
                }
            }
            catch (JsonException)
            {
                // Испорченный файл ставится заново: граница сдвинется к сегодняшнему старту, данные не пропадут.
            }
            var now = new DateTimeOffset(utcNow(), TimeSpan.Zero);
            var oldest = service.OldestJournalUtc() is { } journal ? new DateTimeOffset(journal, TimeSpan.Zero) : now;
            var coverage = new Coverage(now - WindowsEventWatch.FirstLookBack, oldest < now ? oldest : now);
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, JsonSerializer.Serialize(coverage, ObservationJson.Options));
            return coverage;
        }
    }

    /// <summary>Времена перезапусков наблюдателя сторожем по его журналу и ротированному хвосту.</summary>
    private IReadOnlyList<DateTimeOffset> Restarts()
    {
        if (watchdogLog is null)
        {
            return [];
        }
        var restarts = new List<DateTimeOffset>();
        foreach (var path in new[] { watchdogLog + ".old", watchdogLog })
        {
            if (!File.Exists(path))
            {
                continue;
            }
            string[] lines;
            try
            {
                lines = File.ReadAllLines(path, Encoding.UTF8);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var line in lines)
            {
                try
                {
                    using var row = JsonDocument.Parse(line);
                    if (row.RootElement.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String
                        && kind.GetString() == ObserverRestarted
                        && row.RootElement.TryGetProperty("time", out var time) && time.TryGetDateTimeOffset(out var at))
                    {
                        restarts.Add(at);
                    }
                }
                catch (JsonException)
                {
                }
            }
        }
        return restarts;
    }

    /// <summary>Всё, что читается с диска один раз на проход, а не на каждый из тридцати дней.</summary>
    private sealed record Sources(
        IReadOnlyList<WindowsEvent> Events,
        IReadOnlyList<IncidentRecord> Incidents,
        IReadOnlyList<DateTimeOffset> Restarts,
        IReadOnlyList<MachineReading> Readings,
        Coverage Coverage,
        long JournalBytes)
    {
        public static Sources Load(DailySummaryKeeper keeper) => new(
            keeper.events?.Invoke() ?? [],
            keeper.service.Incidents(),
            keeper.Restarts(),
            keeper.Readings(),
            keeper.LoadCoverage(),
            keeper.service.JournalBytes());
    }
}

/// <summary>Замер машины для дневной сводки: слепок и место. Строка файла замеров.</summary>
/// <param name="Snapshot">Идентификатор слепка, лёгшего рядом с журналами; <c>null</c> — не снялся.</param>
public sealed record MachineReading(DateTimeOffset Measured, string? Snapshot, DiskSpace? System, DiskSpace? Temp);

/// <summary>С какого мига данные полны: события журналов Windows и журналы сеансов.</summary>
public sealed record Coverage(DateTimeOffset EventsSince, DateTimeOffset SessionsSince);
