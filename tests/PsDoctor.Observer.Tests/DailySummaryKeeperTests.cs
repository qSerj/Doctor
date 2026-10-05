using System.Text.Json;
using PsDoctor.Core.Observation;
using Xunit;

namespace PsDoctor.Observer.Tests;

/// <summary>
/// Дневные сводки в наблюдателе (Э6.6, часть Б): краткая запись сеанса, замеры, недостающие дни, хранение. Проходы зовёт
/// тест, а не таймер; часы и пояс — подменённые.
/// </summary>
public sealed class DailySummaryKeeperTests : IAsyncLifetime
{
    private static readonly TimeSpan Москва = TimeSpan.FromHours(3);

    private readonly string _каталог = Directory.CreateTempSubdirectory("psdoctor-сводки-").FullName;
    private readonly FakeLauncher _запуск = new();
    private readonly ObservationService _служба;
    private readonly List<WindowsEvent> _события = [];
    private DateTime _сейчас = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);

    public DailySummaryKeeperTests()
    {
        _служба = new ObservationService(_каталог, _запуск, new ObserverHealth("test", null), utcNow: () => _сейчас);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _служба.DisposeAsync();
        Directory.Delete(_каталог, recursive: true);
    }

    private string ЖурналСторожа => Path.Combine(_каталог, "watchdog.jsonl");

    private DailySummaryKeeper Хранитель() =>
        new(_служба, () => _события, ЖурналСторожа, () => _сейчас, _ => Москва);

    private static DateTimeOffset В(int месяц, int день, int час) => new(2026, месяц, день, час, 0, 0, Москва);

    private string Сводки => Path.Combine(_каталог, DailySummaryKeeper.DirectoryName);

    private static JsonElement Данные(object значение) => ObservationJson.ToElement(значение);

    /// <summary>Журнал сеанса на диске, как его оставил наблюдатель; без причины — оборванный.</summary>
    private string Журнал(DateTimeOffset начало, TimeSpan длина, string? причина = SessionEndReasons.ProgramExited, DateTime? изменён = null)
    {
        var id = SessionIds.New(начало.UtcDateTime, _ => false);
        var факты = new List<Fact>
        {
            new Fact(1, TimeSpan.Zero, id, null, ProgramFactKinds.SessionStarted, Данные(new { startedAt = начало, origin = SessionOrigins.Watch })),
            причина is null
                ? new Fact(2, длина, id, null, ProgramFactKinds.FileIo, ObservationJson.Empty)
                : new Fact(2, длина, id, null, ProgramFactKinds.SessionFinished, Данные(new { reason = причина })),
        };
        var путь = Path.Combine(_каталог, id + SessionIds.JournalExtension);
        File.WriteAllLines(путь, факты.Select(f => JsonSerializer.Serialize(f, ObservationJson.Options)));
        File.SetLastWriteTimeUtc(путь, изменён ?? _сейчас.AddDays(-1));
        return id;
    }

    private string Запись(string id) => Path.Combine(_каталог, id + ObservationService.BriefExtension);

    private static EnvironmentFacts Окружение(long свободноГб) =>
        new(null, null, @"C:\ProShow\proshow.exe", true, null, null, null, null, [], null, null,
            new DiskSpace(@"C:\", свободноГб << 30, 500L << 30), new DiskSpace(@"C:\", свободноГб << 30, 500L << 30));

    private static EnvironmentSnapshot Слепок(string merit) =>
        EnvironmentSnapshot.Create(DateTimeOffset.UnixEpoch, 0.1,
            [new EnvironmentEntry(EnvironmentSections.DirectShow, 32, "lav", new Dictionary<string, string?> { ["merit"] = merit }, null)]);

    [Fact]
    public async Task Краткая_запись_при_завершении_совпадает_с_собранной_из_журнала()
    {
        _запуск.Foreign = true;
        var сеанс = _служба.Attach(SessionOrigins.Watch).Accepted!.Session;
        await _служба.StopAsync(сеанс);

        Assert.True(File.Exists(Запись(сеанс)));
        using var журнал = new StreamReader(Path.Combine(_каталог, сеанс + SessionIds.JournalExtension));
        var изЖурнала = SessionBrief.FromFacts(сеанс, FactJournalReader.ReadAfter(журнал, 0).ToList());
        Assert.Equal(DailySummaryJson.Serialize(изЖурнала!), File.ReadAllText(Запись(сеанс)));
        Assert.Equal(SessionEndReasons.Stopped, изЖурнала!.EndReason);
    }

    [Fact]
    public void Оборванный_сеанс_записывается_из_журнала_один_раз_и_переживает_журнал()
    {
        var id = Журнал(В(10, 4, 10), TimeSpan.FromHours(1), причина: null);

        var запись = _служба.Brief(id);

        Assert.Equal((В(10, 4, 10), В(10, 4, 11), (string?)null), (запись!.Started, запись.Ended, запись.EndReason));
        Assert.True(File.Exists(Запись(id)));
        File.Delete(Path.Combine(_каталог, id + SessionIds.JournalExtension));
        Assert.Equal(В(10, 4, 11), Assert.Single(_служба.Briefs(DateTime.MinValue, DateTime.MaxValue)).Ended);
    }

    [Fact]
    public void Журнал_который_только_что_рос_без_конца_сеанса_не_записывается()
    {
        var id = Журнал(В(10, 5, 14), TimeSpan.FromMinutes(1), причина: null, изменён: _сейчас);

        Assert.NotNull(_служба.Brief(id));
        Assert.False(File.Exists(Запись(id)));
    }

    [Fact]
    public void Первый_проход_строит_тридцать_прошлых_дней_без_сегодняшнего_и_не_перестраивает_их()
    {
        var хранитель = Хранитель();

        хранитель.Tick();

        var дни = хранитель.StoredFrom(DateOnly.MinValue);
        Assert.Equal(30, дни.Count);
        Assert.Equal(new DateOnly(2026, 9, 5), дни[0].Day);
        Assert.Equal(new DateOnly(2026, 10, 4), дни[^1].Day);
        Assert.All(дни, д => Assert.True(д.Complete));
        Assert.Null(хранитель.LastError);

        // Событие задним числом в построенный день не попадает: сводка дня — какой она была.
        _события.Add(new WindowsEvent(В(10, 4, 9), "Application", "Application Error", 1000, 1, ["proshow.exe"]));
        хранитель.Tick();
        Assert.Empty(хранитель.Stored(new DateOnly(2026, 10, 4))!.Crashes);
    }

    [Fact]
    public void День_строится_только_через_четверть_часа_после_полуночи()
    {
        _сейчас = new DateTime(2026, 10, 4, 21, 10, 0, DateTimeKind.Utc); // 00:10 5 октября по Москве
        var хранитель = Хранитель();

        хранитель.Tick();
        Assert.Null(хранитель.Stored(new DateOnly(2026, 10, 4)));

        _сейчас = _сейчас.AddMinutes(10);
        хранитель.Tick();
        Assert.NotNull(хранитель.Stored(new DateOnly(2026, 10, 4)));
    }

    [Fact]
    public void Сводка_дня_собирает_сеансы_события_и_метки()
    {
        Журнал(В(10, 4, 10), TimeSpan.FromHours(2));
        _события.Add(new WindowsEvent(В(10, 4, 11), "Application", "Application Error", 1000, 1,
            ["proshow.exe", "9.0", "0", "all.dnt", "1.0", "0", "0xc0000005", "0x0051b3b5"]));
        _события.Add(new WindowsEvent(В(10, 4, 12), "System", "Microsoft-Windows-Kernel-Power", 41, 2, ["0", "0", "0", "0", "0", "0", "0"]));
        _служба.Incident(IncidentSources.Wizard, null);

        var хранитель = Хранитель();
        хранитель.Tick();

        var вчера = хранитель.Stored(new DateOnly(2026, 10, 4))!;
        Assert.Equal((1, 2.0), (вчера.ProShow.Sessions, вчера.ProShow.Hours));
        Assert.Equal(new NamedCount("all.dnt+0x0051b3b5", 1), Assert.Single(Assert.Single(вчера.Crashes).Signatures));
        Assert.Single(вчера.Machine.UnexpectedShutdowns);
        // Метка поставлена сегодня по часам службы — в сводке сегодняшнего дня.
        Assert.Empty(вчера.ProShow.Complaints);
        Assert.Single(хранитель.Build(new DateOnly(2026, 10, 5)).ProShow.Complaints);
    }

    [Fact]
    public void Замер_каждый_проход_и_разница_слепка_с_прошлым_днём()
    {
        var хранитель = Хранитель();
        _запуск.Environment = Окружение(200);
        _запуск.Snapshot = Слепок("0x800000");
        _сейчас = new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
        хранитель.Tick();

        _запуск.Environment = Окружение(5);
        _запуск.Snapshot = Слепок("0x600000");
        _сейчас = _сейчас.AddDays(1);
        хранитель.Tick();
        _сейчас = _сейчас.AddDays(1);
        хранитель.Tick();

        var третье = хранитель.Stored(new DateOnly(2026, 10, 3))!;
        Assert.False(третье.Environment.Changed);
        Assert.Equal(200L << 30, третье.Disks!.System!.FreeBytes);
        var четвёртое = хранитель.Stored(new DateOnly(2026, 10, 4))!;
        Assert.Equal(new SectionChanges(EnvironmentSections.DirectShow, 0, 0, 1), Assert.Single(четвёртое.Environment.Sections));
        Assert.Equal(
            new[] { SummaryHighlights.LowDisk, SummaryHighlights.EnvironmentChanged },
            четвёртое.Highlights.Select(h => h.Kind));
        Assert.Contains(SummaryGaps.Snapshot, хранитель.Stored(new DateOnly(2026, 10, 2))!.Gaps);
    }

    [Fact]
    public void Сегодняшняя_сводка_неполная_и_не_сохраняется()
    {
        _запуск.Environment = Окружение(200);
        var хранитель = Хранитель();
        хранитель.Tick();

        var сегодня = хранитель.Build(хранитель.Today());

        Assert.Equal(new DateOnly(2026, 10, 5), сегодня.Day);
        Assert.False(сегодня.Complete);
        Assert.NotNull(сегодня.Disks);
        Assert.Null(хранитель.Stored(сегодня.Day));
    }

    [Fact]
    public void Перезапуски_наблюдателя_из_журнала_сторожа()
    {
        File.WriteAllLines(ЖурналСторожа,
        [
            JsonSerializer.Serialize(new { time = В(10, 4, 9), kind = "observer-started", data = new { pid = 1 } }, ObservationJson.Options),
            JsonSerializer.Serialize(new { time = В(10, 4, 10), kind = "observer-restarted", data = new { reason = "no-health" } }, ObservationJson.Options),
            "{недописанная",
        ]);
        var хранитель = Хранитель();

        хранитель.Tick();

        var вчера = хранитель.Stored(new DateOnly(2026, 10, 4))!;
        Assert.Equal(1, вчера.Doctor.ObserverRestarts);
        Assert.Contains(new SummaryHighlight(SummaryHighlights.DoctorTrouble, 1), вчера.Highlights);
    }

    [Fact]
    public void Начало_полных_данных_ставится_первым_проходом()
    {
        var хранитель = Хранитель();

        хранитель.Tick();

        // События смотрят на 30 дней назад от первого прохода: самый ранний день полон только наполовину.
        Assert.Contains(SummaryGaps.Events, хранитель.Stored(new DateOnly(2026, 9, 5))!.Gaps);
        Assert.DoesNotContain(SummaryGaps.Events, хранитель.Stored(new DateOnly(2026, 9, 6))!.Gaps);
        // Журналов не было — сеансы известны только с первого прохода.
        Assert.Contains(SummaryGaps.Sessions, хранитель.Stored(new DateOnly(2026, 10, 4))!.Gaps);
    }

    [Fact]
    public void Без_журналов_Windows_сводка_говорит_что_событий_нет_в_данных()
    {
        var хранитель = new DailySummaryKeeper(_служба, null, null, () => _сейчас, _ => Москва);

        хранитель.Tick();

        Assert.Contains(SummaryGaps.Events, хранитель.Stored(new DateOnly(2026, 10, 4))!.Gaps);
    }

    [Fact]
    public void Сводки_и_краткие_записи_старше_срока_удаляются()
    {
        Directory.CreateDirectory(Сводки);
        var давняя = Path.Combine(Сводки, "2025-08-01.json");
        File.WriteAllText(давняя, "{}");
        var id = Журнал(В(8, 1, 10) - TimeSpan.FromDays(365), TimeSpan.FromHours(1));
        _служба.Brief(id);
        File.Delete(Path.Combine(_каталог, id + SessionIds.JournalExtension));
        var хранитель = Хранитель();

        хранитель.Tick();

        Assert.False(File.Exists(давняя));
        Assert.False(File.Exists(Запись(id)));
    }

    [Fact]
    public void Падение_чужой_программы_ложится_в_файл_но_не_в_сеанс()
    {
        _запуск.EventLogs = [new("Application", ToSession: true, SessionImages: ["proshow", "fvideo", "device-enc"])];
        using var опрос = new WindowsEventWatch(_служба, _запуск, () => _сейчас);
        _запуск.Foreign = true;
        var сеанс = _служба.Attach(SessionOrigins.Watch).Accepted!.Session;
        _запуск.QueueEvents(new WindowsEventsBatch(
        [
            new WindowsEvent(new DateTimeOffset(_сейчас.AddSeconds(5), TimeSpan.Zero), "Application", "Application Error", 1000, 10, ["AfterFX.exe"]),
            new WindowsEvent(new DateTimeOffset(_сейчас.AddSeconds(6), TimeSpan.Zero), "Application", "Application Error", 1000, 11, ["fvideo.exe"]),
        ], 11));

        опрос.Tick();

        var факт = Assert.Single(_служба.Live(сеанс)!.After(0), f => f.Kind == ProgramFactKinds.WindowsEvent);
        Assert.Equal(11, факт.Data.GetProperty("recordId").GetInt64());
        Assert.Equal([10L, 11L], опрос.Events().Select(e => e.RecordId ?? 0));
    }
}
