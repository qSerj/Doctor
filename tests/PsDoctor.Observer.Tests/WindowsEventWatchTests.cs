using System.Net;
using PsDoctor.Core.Observation;
using PsDoctor.Observer.Client;
using Xunit;

namespace PsDoctor.Observer.Tests;

/// <summary>
/// Опрос журнала Windows (Э6.2, часть Г) на подменённом журнале: закладка, первый опрос за месяц, очищенный журнал,
/// сбой чтения, файл событий с ротацией и факт в живой сеанс. Опросы зовёт тест, а не таймер.
/// </summary>
public sealed class WindowsEventWatchTests : IAsyncLifetime
{
    private const string Ключ = "test-key-0123456789";
    private static readonly DateTime Сейчас = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _каталог = Directory.CreateTempSubdirectory("psdoctor-журнал-windows-").FullName;
    private readonly FakeLauncher _запуск = new();
    private readonly ObservationService _служба;
    private readonly WindowsEventWatch _опрос;

    public WindowsEventWatchTests()
    {
        _служба = new ObservationService(_каталог, _запуск, new ObserverHealth("test", null), utcNow: () => Сейчас);
        _опрос = new WindowsEventWatch(_служба, _запуск, () => Сейчас);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _опрос.Dispose();
        await _служба.DisposeAsync();
        Directory.Delete(_каталог, recursive: true);
    }

    private static WindowsEvent Событие(long номер, DateTime когда, string образ = "proshow.exe") =>
        new(new DateTimeOffset(когда, TimeSpan.Zero), "Application", "Application Error", 1000, номер, [образ, "9.0.3797.0", "c0000005"]);

    /// <summary>Загрузка после выключения без завершения работы: синего экрана не было.</summary>
    private static WindowsEvent Выключение(long номер, DateTime когда) =>
        new(new DateTimeOffset(когда, TimeSpan.Zero), "System", "Microsoft-Windows-Kernel-Power", 41, номер, ["0", "0x0", "0x0", "0x0", "0x0", "0", "0"]);

    private void ОбаЖурнала() => _запуск.EventLogs = [new("Application", ToSession: true), new("System", ToSession: false)];

    private string? Закладка(string журнал = "Application")
    {
        var путь = Path.Combine(_каталог, WindowsEventWatch.BookmarkFileOf(журнал));
        return File.Exists(путь) ? File.ReadAllText(путь) : null;
    }

    [Fact]
    public void Первый_опрос_смотрит_на_месяц_назад_и_ставит_закладку_на_новейшую_запись()
    {
        _запуск.QueueEvents(new WindowsEventsBatch([Событие(40, Сейчас.AddDays(-3))], 120));

        _опрос.Tick();

        var (журнал, после, с) = Assert.Single(_запуск.EventReads);
        Assert.Equal("Application", журнал);
        Assert.Null(после);
        Assert.Equal(new DateTimeOffset(Сейчас - WindowsEventWatch.FirstLookBack, TimeSpan.Zero), с);
        Assert.Equal(40, Assert.Single(_опрос.Events()).RecordId);
        Assert.Equal("120", Закладка());
        Assert.Equal(new WindowsEventsStatus(Сейчас), _опрос.Status);
    }

    [Fact]
    public void Следующий_опрос_идёт_с_закладки_и_после_перезапуска_наблюдателя()
    {
        _запуск.QueueEvents(new WindowsEventsBatch([Событие(40, Сейчас.AddDays(-3))], 120));
        _опрос.Tick();

        using var послеПерезапуска = new WindowsEventWatch(_служба, _запуск, () => Сейчас);
        _запуск.QueueEvents(new WindowsEventsBatch([Событие(121, Сейчас)], 125));
        послеПерезапуска.Tick();

        Assert.Equal(120, _запуск.EventReads[1].After);
        Assert.Equal([40L, 121L], послеПерезапуска.Events().Select(e => e.RecordId ?? 0));
        Assert.Equal("125", Закладка());
    }

    [Fact]
    public void Очищенный_журнал_читается_заново_за_месяц()
    {
        File.WriteAllText(Path.Combine(_каталог, WindowsEventWatch.BookmarkFile), "120");
        _запуск.QueueEvents(new WindowsEventsBatch([], 5));
        _запуск.QueueEvents(new WindowsEventsBatch([Событие(3, Сейчас.AddMinutes(-2))], 5));

        _опрос.Tick();

        Assert.Equal(new long?[] { 120, null }, _запуск.EventReads.Select(r => r.After));
        Assert.Equal(3, Assert.Single(_опрос.Events()).RecordId);
        Assert.Equal("5", Закладка());
    }

    [Fact]
    public void Сбой_чтения_не_двигает_закладку_и_виден_в_статусе_до_удачного_опроса()
    {
        _запуск.QueueEvents(new WindowsEventsBatch([], 120));
        _опрос.Tick();
        _запуск.QueueEvents(new WindowsEventsBatch([], null, "EventLogException"));

        _опрос.Tick();

        Assert.Equal("120", Закладка());
        Assert.Equal(new WindowsEventsStatus(Сейчас, "EventLogException"), _опрос.Status);

        _опрос.Tick();

        Assert.Equal(120, _запуск.EventReads[2].After);
        Assert.Equal(new WindowsEventsStatus(Сейчас), _опрос.Status);
    }

    [Fact]
    public void Событие_ложится_фактом_в_живой_сеанс_только_если_случилось_после_его_начала()
    {
        _запуск.Foreign = true;
        var сеанс = _служба.Attach(SessionOrigins.Watch).Accepted!.Session;
        _запуск.QueueEvents(new WindowsEventsBatch(
            [Событие(50, Сейчас.AddMinutes(-5)), Событие(51, Сейчас.AddSeconds(3), "device-encp.dll")], 51));

        _опрос.Tick();

        var факт = Assert.Single(_служба.Live(сеанс)!.After(0), f => f.Kind == ProgramFactKinds.WindowsEvent);
        Assert.Equal(51, факт.Data.GetProperty("recordId").GetInt64());
        Assert.Equal(2, _опрос.Events().Count);
    }

    [Fact]
    public async Task После_конца_сеанса_событие_только_в_файле()
    {
        _запуск.Foreign = true;
        var сеанс = _служба.Attach(SessionOrigins.Watch).Accepted!.Session;
        await _служба.StopAsync(сеанс);
        _запуск.QueueEvents(new WindowsEventsBatch([Событие(60, Сейчас.AddSeconds(10))], 60));

        _опрос.Tick();

        Assert.DoesNotContain(ProgramFactKinds.WindowsEvent, await File.ReadAllTextAsync(_служба.JournalPath(сеанс)!));
        Assert.Equal(60, Assert.Single(_опрос.Events()).RecordId);
    }

    [Fact]
    public void Файл_событий_от_10_МБ_уходит_в_old_и_читается_вместе_с_новым()
    {
        var путь = Path.Combine(_каталог, WindowsEventWatch.EventsFile);
        _запуск.QueueEvents(new WindowsEventsBatch([Событие(70, Сейчас.AddDays(-1))], 70));
        _опрос.Tick();
        using (var файл = new FileStream(путь, FileMode.Open))
        {
            // Хвост из нулей — битая строка: чтение её пропустит, а вес дойдёт до предела.
            файл.SetLength(WindowsEventWatch.FileLimit);
        }
        _запуск.QueueEvents(new WindowsEventsBatch([Событие(71, Сейчас)], 71));

        _опрос.Tick();

        Assert.Equal(WindowsEventWatch.FileLimit, new FileInfo(Path.Combine(_каталог, WindowsEventWatch.OldEventsFile)).Length);
        Assert.Single(File.ReadAllLines(путь));
        Assert.Equal([70L, 71L], _опрос.Events().Select(e => e.RecordId ?? 0));
    }

    [Fact]
    public void Сбои_машины_со_своей_закладкой_и_файлом_и_не_фактом_в_сеанс()
    {
        ОбаЖурнала();
        _запуск.Foreign = true;
        var сеанс = _служба.Attach(SessionOrigins.Watch).Accepted!.Session;
        _запуск.QueueEvents(new WindowsEventsBatch([Событие(51, Сейчас.AddSeconds(3))], 51));
        _запуск.QueueEvents(new WindowsEventsBatch([Выключение(900, Сейчас.AddSeconds(5))], 905), "System");

        _опрос.Tick();

        Assert.Equal([("Application", (long?)null), ("System", (long?)null)], _запуск.EventReads.Select(r => (r.Log, r.After)));
        Assert.Equal("51", Закладка());
        Assert.Equal("905", Закладка("System"));
        // Номер записи у каждого журнала свой, файл тоже: поток аппаратных ошибок не вытеснит падения программы.
        Assert.DoesNotContain("Kernel-Power", File.ReadAllText(Path.Combine(_каталог, WindowsEventWatch.EventsFile)));
        Assert.Contains("Kernel-Power", File.ReadAllText(Path.Combine(_каталог, WindowsEventWatch.EventsFileOf("System"))));
        var факт = Assert.Single(_служба.Live(сеанс)!.After(0), f => f.Kind == ProgramFactKinds.WindowsEvent);
        Assert.Equal(51, факт.Data.GetProperty("recordId").GetInt64());
        Assert.Equal([51L, 900L], _опрос.Events().Select(e => e.RecordId ?? 0));
    }

    [Fact]
    public void Сбой_журнала_System_не_мешает_Application_и_виден_в_статусе()
    {
        ОбаЖурнала();
        _запуск.QueueEvents(new WindowsEventsBatch([Событие(9, Сейчас.AddDays(-1))], 10));
        _запуск.QueueEvents(new WindowsEventsBatch([], null, "UnauthorizedAccessException"), "System");

        _опрос.Tick();

        Assert.Equal("10", Закладка());
        Assert.Null(Закладка("System"));
        Assert.Equal(new WindowsEventsStatus(Сейчас, "System: UnauthorizedAccessException"), _опрос.Status);

        _запуск.QueueEvents(new WindowsEventsBatch([Выключение(6, Сейчас.AddDays(-2))], 7), "System");
        _опрос.Tick();

        Assert.Equal(new WindowsEventsStatus(Сейчас), _опрос.Status);
        Assert.Equal("7", Закладка("System"));
        Assert.Equal([6L, 9L], _опрос.Events().Select(e => e.RecordId ?? 0));
    }

    [Fact]
    public async Task Наблюдатель_опрашивает_журнал_при_старте_и_отдаёт_события_маршрутом()
    {
        var запуск = new FakeLauncher();
        запуск.QueueEvents(new WindowsEventsBatch([Событие(80, Сейчас)], 80));
        await using var наблюдатель = ObserverHost.Build(new ObserverOptions(IPAddress.Loopback, 0, Ключ, Path.Combine(_каталог, "host")), запуск);
        await наблюдатель.StartAsync();
        using var клиент = new ObserverClient(new Uri(наблюдатель.Urls.Single()), Ключ);

        // Первый опрос идёт в фоне сразу после старта; статус ставится после записи в файл.
        WindowsEventsStatus? опрос = null;
        for (var попытка = 0; попытка < 100 && опрос?.LastReadUtc is null; попытка++)
        {
            опрос = (await клиент.HealthAsync()).WindowsEvents;
            if (опрос?.LastReadUtc is null)
            {
                await Task.Delay(50);
            }
        }

        Assert.NotNull(опрос?.LastReadUtc);
        Assert.Null(опрос!.LastError);
        Assert.Equal(80, Assert.Single(await клиент.WindowsEventsAsync()).RecordId);
        await наблюдатель.StopAsync();
    }
}
