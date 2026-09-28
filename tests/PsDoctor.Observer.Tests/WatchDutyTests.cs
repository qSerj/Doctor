using System.Net;
using PsDoctor.Core.Observation;
using PsDoctor.Infrastructure.Installation;
using PsDoctor.Observer.Client;
using Xunit;

namespace PsDoctor.Observer.Tests;

/// <summary>Дежурство (Э6.2, часть В) на подменённом запуске и ручных часах: шаги зовёт тест, а не таймер.</summary>
public sealed class WatchDutyTests : IAsyncLifetime
{
    private static readonly DateTime Сейчас = new(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);

    private static readonly EnvironmentFacts Окружение = new("10.0.19045.4894", "22H2", @"C:\ProShow\proshow.exe", true, "9.0.3797",
        false, 1, [new AntivirusProduct("Windows Defender", 397568)], [], 16L << 30, 8L << 30,
        new DiskSpace(@"C:\", 50L << 30, 200L << 30), new DiskSpace(@"C:\", 50L << 30, 200L << 30));

    private readonly string _каталог = Directory.CreateTempSubdirectory("psdoctor-дежурство-").FullName;
    private readonly FakeLauncher _запуск = new() { Environment = Окружение };
    private readonly ObservationService _служба;
    private readonly WatchDuty _дежурство;
    private TimeSpan _часы = TimeSpan.FromHours(1);

    public WatchDutyTests()
    {
        _служба = new ObservationService(_каталог, _запуск, new ObserverHealth("test", null), utcNow: () => Сейчас);
        _дежурство = new WatchDuty(_служба, () => _часы, () => Сейчас);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _дежурство.Dispose();
        await _служба.DisposeAsync();
        Directory.Delete(_каталог, recursive: true);
    }

    private string? Живой() => _служба.Activity().Session;

    [Fact]
    public void Без_ProShow_дежурство_молчит()
    {
        _дежурство.Tick();

        Assert.Equal(0, _запуск.Finds);
        Assert.Empty(_служба.Sessions());
        Assert.Null(_дежурство.Status.LastRefusal);
    }

    [Fact]
    public void Увидело_ProShow_подключилось_с_origin_watch_и_окружением_вторым_фактом()
    {
        _запуск.Foreign = true;

        _дежурство.Tick();

        var сеанс = Assert.IsType<string>(Живой());
        var факты = _служба.Live(сеанс)!.After(0);
        Assert.Equal(ProgramFactKinds.SessionStarted, факты[0].Kind);
        Assert.Equal(SessionOrigins.Watch, факты[0].Data.GetProperty("origin").GetString());
        Assert.Equal(ProgramFactKinds.Environment, факты[1].Kind);
        Assert.Equal("10.0.19045.4894", факты[1].Data.GetProperty("windowsBuild").GetString());
        Assert.Equal(ProgramFactKinds.ProgramAttached, факты[2].Kind);
        Assert.Equal(ProgramStates.Attached, _служба.Activity().Program);
    }

    [Fact]
    public void При_живом_сеансе_второго_не_начинает()
    {
        _запуск.Foreign = true;
        _дежурство.Tick();
        var первый = Живой();

        _часы += TimeSpan.FromMinutes(5);
        _дежурство.Tick();

        Assert.Equal(1, _запуск.Finds);
        Assert.Equal(первый, Живой());
        Assert.Single(_служба.Sessions());
    }

    [Fact]
    public void Отказ_повторяется_через_30_секунд_без_фактов()
    {
        _запуск.Foreign = true;
        _запуск.FindRefusal = ObserverErrors.AmbiguousProgram;

        _дежурство.Tick();
        _часы += TimeSpan.FromSeconds(29);
        _дежурство.Tick();

        Assert.Equal(1, _запуск.Finds);
        Assert.Equal(new WatchStatus(true, ObserverErrors.AmbiguousProgram, Сейчас), _дежурство.Status);
        Assert.Empty(Directory.EnumerateFiles(_каталог, "*" + SessionIds.JournalExtension));

        _запуск.FindRefusal = null;
        _часы += TimeSpan.FromSeconds(1);
        _дежурство.Tick();

        Assert.Equal(2, _запуск.Finds);
        Assert.NotNull(Живой());
    }

    [Fact]
    public async Task После_явного_stop_к_тому_же_процессу_не_подключается()
    {
        _запуск.Foreign = true;
        _дежурство.Tick();
        var сеанс = Живой()!;
        await _служба.StopAsync(сеанс);

        _часы += TimeSpan.FromMinutes(5);
        _дежурство.Tick();

        Assert.Null(Живой());
        Assert.Equal(ObserverErrors.StoppedProgram, _дежурство.Status.LastRefusal);
        Assert.Single(_служба.Sessions());

        // ProShow перезапущен, номер процесса тот же, время создания новое.
        _запуск.Started = Сейчас.AddMinutes(1);
        _часы += WatchDuty.RetryAfter;
        _дежурство.Tick();

        Assert.NotNull(Живой());
        Assert.NotEqual(сеанс, Живой());
    }

    [Fact]
    public async Task Мастер_подключается_к_остановленной_программе_как_раньше()
    {
        _запуск.Foreign = true;
        _дежурство.Tick();
        await _служба.StopAsync(Живой()!);

        var (принят, _, отказ) = _служба.Attach(SessionOrigins.Wizard);

        Assert.Null(отказ);
        Assert.NotNull(принят);
    }

    [Fact]
    public void Настройка_watch_и_ключ_наблюдателя()
    {
        var ключ = Path.Combine(_каталог, "observer.key");
        File.WriteAllText(ключ, "секрет");
        var (включено, _) = InstalledSettings.Parse("""{"watch": true}""");
        var раскладка = new InstalledLayout(Path.Combine(_каталог, "settings.json"), ключ, Path.Combine(_каталог, "sessions"), Path.Combine(_каталог, "w.jsonl"));

        Assert.False(InstalledSettings.Default.Watch);
        Assert.False(InstalledSettings.Parse("{}").Settings!.Watch);
        Assert.True(включено!.Watch);
        Assert.DoesNotContain("--watch", Watchdog.ObserverArguments(InstalledSettings.Default, раскладка));
        var ключи = Watchdog.ObserverArguments(включено, раскладка);
        Assert.Contains("--watch", ключи);
        Assert.True(ObserverOptions.Parse(ключи).Options!.Watch);
        Assert.False(ObserverOptions.Parse(["--key-file", ключ]).Options!.Watch);
    }

    [Fact]
    public async Task Health_показывает_дежурство()
    {
        await using var включено = ObserverHost.Build(new ObserverOptions(IPAddress.Loopback, 0, "test-key-0123456789", Path.Combine(_каталог, "a"), Watch: true), new FakeLauncher());
        await using var выключено = ObserverHost.Build(new ObserverOptions(IPAddress.Loopback, 0, "test-key-0123456789", Path.Combine(_каталог, "b")), new FakeLauncher());
        await включено.StartAsync();
        await выключено.StartAsync();
        using var первый = new ObserverClient(new Uri(включено.Urls.Single()), "test-key-0123456789");
        using var второй = new ObserverClient(new Uri(выключено.Urls.Single()), "test-key-0123456789");

        Assert.Equal(new WatchStatus(true), (await первый.HealthAsync()).Watch);
        Assert.Equal(new WatchStatus(false), (await второй.HealthAsync()).Watch);

        await включено.StopAsync();
        await выключено.StopAsync();
    }
}
