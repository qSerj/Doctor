using System.Text.Json;
using PsDoctor.Core.Observation;
using Xunit;

namespace PsDoctor.Observer.Tests;

/// <summary>
/// Мост к помощнику ETW без самого помощника: его сторона канала — файлы, их пишет тест. Э6.2, часть А: сеанс не
/// кончается отказом без помощника, пропажа помощника видна фактом, а его возвращение возобновляет запись.
/// </summary>
public sealed class EtwBridgeTests : IDisposable
{
    private const string Сеанс = "20260928-120000-000";
    private static readonly TimeSpan Терпение = TimeSpan.FromSeconds(10);

    private readonly string _каталог = Directory.CreateTempSubdirectory("psdoctor-etw-").FullName;
    private readonly FactLog _журнал = new(new FactJournalWriter(new StringWriter(), "тест", () => TimeSpan.Zero));

    public void Dispose() => Directory.Delete(_каталог, recursive: true);

    [Fact]
    public void Без_помощника_сеанс_идёт_с_записью_недоступна()
    {
        using var мост = Открыть();

        var состояние = Assert.Single(_журнал.After(0), f => f.Kind == ProgramFactKinds.EtwState);
        Assert.Equal("unavailable", состояние.Data.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Мост_начинает_запись_когда_помощник_подал_сердцебиение()
    {
        using var мост = Открыть();
        using var стоп = new CancellationTokenSource();
        var сердце = Биться(стоп.Token);

        var команда = await Дождаться(() => Команда() is { Action: "start" } c && c.Session == Сеанс ? c : null);
        Assert.Equal(1000, команда.RootPid);
        EtwFiles.WriteAtomically(EtwFiles.Status(_каталог, Сеанс), new EtwStatus("ready"));

        await Дождаться(() => Состояния().Contains("ready") ? "да" : null);
        Assert.Equal(["unavailable", "ready"], Состояния());
        await стоп.CancelAsync();
        await сердце;
    }

    [Fact]
    public async Task Пропавший_помощник_виден_фактом_и_запись_возобновляется()
    {
        using var мост = Открыть();
        using (var стоп = new CancellationTokenSource())
        {
            var сердце = Биться(стоп.Token);
            await Дождаться(() => Команда() is { Action: "start" } ? "да" : null);
            EtwFiles.WriteAtomically(EtwFiles.Status(_каталог, Сеанс), new EtwStatus("ready"));
            await Дождаться(() => Состояния().Contains("ready") ? "да" : null);
            await стоп.CancelAsync();
            await сердце;
        }

        var пропажа = await Дождаться(() => _журнал.After(0).LastOrDefault(f => f.Kind == ProgramFactKinds.EtwState
            && f.Data.GetProperty("state").GetString() == "failed"));
        Assert.Equal("helper-lost", пропажа.Data.GetProperty("error").GetString());

        // Новый помощник: прежний статус «ready» лежит на диске, но ответом на новую команду не считается.
        var прежняя = Команда()!.Id;
        using var снова = new CancellationTokenSource();
        var второе = Биться(снова.Token);
        await Дождаться(() => Команда() is { Action: "start" } c && c.Id != прежняя ? c : null);
        await Task.Delay(300);
        Assert.Equal(["unavailable", "ready", "failed"], Состояния());
        EtwFiles.WriteAtomically(EtwFiles.Status(_каталог, Сеанс), new EtwStatus("ready"));
        await Дождаться(() => Состояния().Count == 4 ? "да" : null);
        Assert.Equal(["unavailable", "ready", "failed", "ready"], Состояния());
        await снова.CancelAsync();
        await второе;
    }

    [Fact]
    public void Сердцебиение_из_прошлого_не_оживляет_помощника()
    {
        EtwFiles.WriteAtomically(EtwFiles.Heartbeat(_каталог), new EtwHeartbeat(4242, DateTime.UtcNow.AddMinutes(-5)));

        var отказ = Assert.Throws<EtwStartException>(() =>
            EtwBridge.Start(_каталог, Сеанс, 1000, "proshow.exe", _журнал, readyTimeout: TimeSpan.FromSeconds(5)));

        Assert.Equal("helper-not-running", отказ.Message);
    }

    [Fact]
    public async Task Командная_строка_воркера_попадает_в_факт()
    {
        using var мост = Открыть();
        var строка = JsonSerializer.Serialize(new EtwSummary(DateTime.UtcNow, 2000, null, 0, 0, 0, 0, 0, 0,
            "device-enc.dll", 1000, "device-enc.dll -i \"C:\\p\\clip.mp4\" -f rawvideo -"), ObservationJson.Options);
        await File.WriteAllTextAsync(EtwFiles.Summary(_каталог, Сеанс), строка + "\n");

        var факт = await Дождаться(() => _журнал.After(0).FirstOrDefault(f => f.Kind == ProgramFactKinds.EtwProcessStarted));
        Assert.Contains("clip.mp4", факт.Data.GetProperty("commandLine").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Помощник_дописывает_файлы_сеанса_а_не_укорачивает()
    {
        var путь = EtwFiles.Raw(_каталог, Сеанс);
        Directory.CreateDirectory(EtwFiles.Directory(_каталог));
        File.WriteAllText(путь, "прежний\n");

        using (var запись = EtwFiles.OpenAppend(путь))
        {
            запись.WriteLine("новый");
        }

        Assert.Equal("прежний\nновый\n", File.ReadAllText(путь).ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Помощник_при_старте_видит_прежнюю_команду_и_не_исполняет_её()
    {
        EtwFiles.WriteAtomically(EtwFiles.Command(_каталог), new EtwCommand("прежняя", Сеанс, "start", 1000, 4242, "proshow.exe"));

        Assert.Equal("прежняя", EtwHelper.CurrentCommandId(_каталог));
        Assert.Null(EtwHelper.CurrentCommandId(Path.Combine(_каталог, "пусто")));
    }

    private EtwBridge Открыть() => EtwBridge.Open(_каталог, Сеанс, () => 1000, "proshow.exe", _журнал,
        readyTimeout: TimeSpan.FromMilliseconds(200), monitorInterval: TimeSpan.FromMilliseconds(50),
        helperTimeout: TimeSpan.FromMilliseconds(500));

    /// <summary>Сторона помощника: новое сердцебиение раз в 50 мс, пока не отменят.</summary>
    private Task Биться(CancellationToken стоп) => Task.Run(async () =>
    {
        while (!стоп.IsCancellationRequested)
        {
            EtwFiles.WriteAtomically(EtwFiles.Heartbeat(_каталог), new EtwHeartbeat(4242, DateTime.UtcNow));
            try { await Task.Delay(50, стоп); }
            catch (OperationCanceledException) { }
        }
    });

    private EtwCommand? Команда()
    {
        try
        {
            var путь = EtwFiles.Command(_каталог);
            return File.Exists(путь) ? JsonSerializer.Deserialize<EtwCommand>(File.ReadAllText(путь), ObservationJson.Options) : null;
        }
        catch (Exception e) when (e is IOException or JsonException) { return null; }
    }

    private List<string?> Состояния() => [.. _журнал.After(0)
        .Where(f => f.Kind == ProgramFactKinds.EtwState)
        .Select(f => f.Data.GetProperty("state").GetString())];

    private static async Task<T> Дождаться<T>(Func<T?> условие) where T : class
    {
        using var предел = new CancellationTokenSource(Терпение);
        while (true)
        {
            if (условие() is { } значение) return значение;
            await Task.Delay(20, предел.Token);
        }
    }
}
