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
        EtwFiles.WriteAtomically(EtwFiles.Status(_каталог, Сеанс), new EtwStatus("ready", Command: команда.Id));

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
            var первая = await Дождаться(() => Команда() is { Action: "start" } c ? c : null);
            EtwFiles.WriteAtomically(EtwFiles.Status(_каталог, Сеанс), new EtwStatus("ready", Command: первая.Id));
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
        var новая = await Дождаться(() => Команда() is { Action: "start" } c && c.Id != прежняя ? c : null);
        await Task.Delay(300);
        Assert.Equal(["unavailable", "ready", "failed"], Состояния());
        EtwFiles.WriteAtomically(EtwFiles.Status(_каталог, Сеанс), new EtwStatus("ready", Command: новая.Id));
        await Дождаться(() => Состояния().Count == 4 ? "да" : null);
        Assert.Equal(["unavailable", "ready", "failed", "ready"], Состояния());
        await снова.CancelAsync();
        await второе;
    }

    [Fact]
    public async Task Ответ_узнаётся_по_номеру_команды_а_не_по_времени_файла()
    {
        using var мост = Открыть();
        using var стоп = new CancellationTokenSource();
        var сердце = Биться(стоп.Token);
        var команда = await Дождаться(() => Команда() is { Action: "start" } c ? c : null);
        var путь = EtwFiles.Status(_каталог, Сеанс);

        // Чужой ответ со свежим временем файла — не ответ.
        EtwFiles.WriteAtomically(путь, new EtwStatus("ready", Command: "чужая"));
        File.SetLastWriteTimeUtc(путь, DateTime.UtcNow.AddHours(1));
        await Task.Delay(300);
        Assert.Equal(["unavailable"], Состояния());

        // Свой ответ со временем файла из прошлого — ответ: часы Windows грубые и прыгают, номер — нет.
        EtwFiles.WriteAtomically(путь, new EtwStatus("ready", Command: команда.Id));
        File.SetLastWriteTimeUtc(путь, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await Дождаться(() => Состояния().Contains("ready") ? "да" : null);
        Assert.Equal(["unavailable", "ready"], Состояния());
        await стоп.CancelAsync();
        await сердце;
    }

    [Fact]
    public async Task Последний_счёт_потерь_берётся_из_ответа_на_stop()
    {
        using var стоп = new CancellationTokenSource();
        var сердце = Биться(стоп.Token);
        // Сторона помощника: отвечает на каждую новую команду её номером, на stop — со счётом потерь.
        var помощник = Task.Run(async () =>
        {
            string? отвечено = null;
            while (!стоп.IsCancellationRequested)
            {
                if (Команда() is { } команда && команда.Id != отвечено)
                {
                    try
                    {
                        EtwFiles.WriteAtomically(EtwFiles.Status(_каталог, Сеанс), команда.Action == "start"
                            ? new EtwStatus("ready", Command: команда.Id)
                            : new EtwStatus("stopped", LostEvents: 5, Command: команда.Id));
                        отвечено = команда.Id;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                }
                try { await Task.Delay(20, стоп.Token); }
                catch (OperationCanceledException) { }
            }
        });

        using (Открыть())
        {
            Assert.Equal(["ready"], Состояния());
        }

        Assert.Equal(["ready", "degraded"], Состояния());
        var итог = _журнал.After(0).Last(f => f.Kind == ProgramFactKinds.EtwState);
        Assert.Equal(5, итог.Data.GetProperty("lostEvents").GetInt64());
        await стоп.CancelAsync();
        await сердце;
        await помощник;
    }

    [Fact]
    public void Прежний_ответ_на_диске_не_принимается_за_ответ_на_новый_старт()
    {
        // Помощник жив, а в файле сеанса лежит «ready» на прежнюю команду: старт сеанса не должен его принять.
        EtwFiles.WriteAtomically(EtwFiles.Status(_каталог, Сеанс), new EtwStatus("ready", Command: "прежняя"));
        EtwFiles.WriteAtomically(EtwFiles.Heartbeat(_каталог), new EtwHeartbeat(4242, DateTime.UtcNow));

        using var мост = Открыть();

        Assert.Equal(["unavailable"], Состояния());
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
    public async Task Подмена_файла_канала_дожидается_короткого_чтения()
    {
        var путь = EtwFiles.Heartbeat(_каталог);
        EtwFiles.WriteAtomically(путь, new EtwHeartbeat(1, DateTime.UnixEpoch));

        // Другая сторона канала читает файл мгновение; на Windows подмена в этот миг отказывает и повторяется.
        var читатель = new FileStream(путь, FileMode.Open, FileAccess.Read, FileShare.Read);
        var отпустить = Task.Run(async () =>
        {
            await Task.Delay(10);
            await читатель.DisposeAsync();
        });
        EtwFiles.WriteAtomically(путь, new EtwHeartbeat(2, DateTime.UnixEpoch));
        await отпустить;

        Assert.Equal(2, JsonSerializer.Deserialize<EtwHeartbeat>(File.ReadAllText(путь), ObservationJson.Options)!.Pid);
        Assert.Empty(Directory.EnumerateFiles(EtwFiles.Directory(_каталог), "*.tmp"));
    }

    [Fact]
    public void Подмена_под_долгим_читателем_отказывает_без_временного_файла()
    {
        // Открытый файл мешает подмене только на Windows.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var путь = EtwFiles.Heartbeat(_каталог);
        EtwFiles.WriteAtomically(путь, new EtwHeartbeat(1, DateTime.UnixEpoch));

        using (new FileStream(путь, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var отказ = Record.Exception(() => EtwFiles.WriteAtomically(путь, new EtwHeartbeat(2, DateTime.UnixEpoch)));
            Assert.True(отказ is IOException or UnauthorizedAccessException, $"подмена отказала так: {отказ}");
        }

        Assert.Equal(1, JsonSerializer.Deserialize<EtwHeartbeat>(File.ReadAllText(путь), ObservationJson.Options)!.Pid);
        Assert.Empty(Directory.EnumerateFiles(EtwFiles.Directory(_каталог), "*.tmp"));
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
            // Как у настоящего помощника: пропущенный удар мост переживёт.
            try { EtwFiles.WriteAtomically(EtwFiles.Heartbeat(_каталог), new EtwHeartbeat(4242, DateTime.UtcNow)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
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
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
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
