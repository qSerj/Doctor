using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using PsDoctor.Core.Observation;
using PsDoctor.Core.Scenarios;
using PsDoctor.Observer.Client;
using Xunit;

namespace PsDoctor.Observer.Tests;

/// <summary>
/// API наблюдателя на настоящем Kestrel с подменённым запуском программы. Выполняется везде:
/// Win32 здесь нет, есть сеансы, сценарии, журнал на диске и поток.
/// </summary>
public sealed class ObservationApiTests : IAsyncLifetime
{
    private const string Ключ = "test-key-0123456789";
    private static readonly TimeSpan Терпение = TimeSpan.FromSeconds(10);

    private readonly string _каталог = Directory.CreateTempSubdirectory("psdoctor-сеансы-").FullName;
    private readonly FakeLauncher _запуск = new();
    private WebApplication _наблюдатель = null!;
    private ObserverClient _клиент = null!;

    public async Task InitializeAsync() => await Поднять();

    public async Task DisposeAsync()
    {
        _клиент.Dispose();
        // Остановка, а не только освобождение: она закрывает живой сеанс и его журнал, иначе Windows не отдаст каталог.
        await _наблюдатель.StopAsync();
        await _наблюдатель.DisposeAsync();
        Directory.Delete(_каталог, recursive: true);
    }

    private async Task Поднять()
    {
        _наблюдатель = ObserverHost.Build(new ObserverOptions(IPAddress.Loopback, 0, Ключ, _каталог), _запуск);
        await _наблюдатель.StartAsync();
        _клиент = new ObserverClient(new Uri(_наблюдатель.Urls.Single()), Ключ);
    }

    private async Task<List<Fact>> ДоКонцаСценария(RunScenarioAccepted принят)
    {
        var факты = new List<Fact>();
        using var время = new CancellationTokenSource(Терпение);
        await foreach (var факт in _клиент.StreamAsync(принят.Session, принят.After, null, время.Token))
        {
            факты.Add(факт);
            if (факт.Kind == ScenarioFactKinds.ScenarioFinished)
            {
                break;
            }
        }
        return факты;
    }

    private async Task<List<Fact>> ДоКонцаСеанса(string сеанс, long после = 0, IReadOnlyCollection<string>? виды = null)
    {
        var факты = new List<Fact>();
        using var время = new CancellationTokenSource(Терпение);
        await foreach (var факт in _клиент.StreamAsync(сеанс, после, виды, время.Token))
        {
            факты.Add(факт);
        }
        return факты;
    }

    private static string Статус(Fact завершение) => завершение.Data.GetProperty("status").GetString()!;

    [Fact]
    public async Task Сценарий_с_незнакомым_шагом_отвергается_с_номером_строки()
    {
        var отказ = await Assert.ThrowsAsync<ObserverException>(() => _клиент.RunAsync("launch \"C:\\p\\1.psh\"\nпрыжок"));

        Assert.Equal(HttpStatusCode.BadRequest, отказ.Status);
        Assert.Equal(ObserverErrors.BadScenario, отказ.Error!.Error);
        var ошибка = Assert.Single(отказ.Error.Errors!);
        Assert.Equal(2, ошибка.Line);
        Assert.Equal(ScenarioErrorKind.UnknownStep, ошибка.Kind);
    }

    [Fact]
    public async Task Запуск_при_программе_запущенной_мимо_наблюдателя_отказ_без_сеанса()
    {
        _запуск.Foreign = true;

        var отказ = await Assert.ThrowsAsync<ObserverException>(() => _клиент.RunAsync("launch \"C:\\p\\1.psh\""));

        Assert.Equal(HttpStatusCode.Conflict, отказ.Status);
        Assert.Equal(ObserverErrors.ProgramRunning, отказ.Error!.Error);
        Assert.Empty(_запуск.Runs);
        Assert.Empty(await _клиент.SessionsAsync());
    }

    [Fact]
    public async Task Второй_запуск_при_живом_сеансе_отказ()
    {
        var принят = await _клиент.RunAsync("launch \"C:\\p\\1.psh\"");
        await ДоКонцаСценария(принят);

        var отказ = await Assert.ThrowsAsync<ObserverException>(() => _клиент.RunAsync("launch \"C:\\p\\2.psh\""));

        Assert.Equal(HttpStatusCode.Conflict, отказ.Status);
        Assert.Equal(ObserverErrors.ProgramRunning, отказ.Error!.Error);
        Assert.Single(_запуск.Runs);
    }

    [Fact]
    public async Task Сценарий_не_с_запуска_без_сеанса_отказ()
    {
        var отказ = await Assert.ThrowsAsync<ObserverException>(() => _клиент.RunAsync("wait exit 5"));

        Assert.Equal(ObserverErrors.NoSession, отказ.Error!.Error);
    }

    [Fact]
    public async Task Без_ключа_отказ_на_каждом_маршруте()
    {
        using var голый = new HttpClient { BaseAddress = new Uri(_наблюдатель.Urls.Single()) };
        foreach (var (метод, путь) in new[]
                 {
                     (HttpMethod.Post, ObserverRoutes.Scenarios),
                     (HttpMethod.Post, ObserverRoutes.CancelScenario),
                     (HttpMethod.Get, ObserverRoutes.Sessions),
                     (HttpMethod.Get, ObserverRoutes.Stream("20260917-000000-000")),
                     (HttpMethod.Get, ObserverRoutes.Facts("20260917-000000-000")),
                     (HttpMethod.Get, ObserverRoutes.Raw("20260917-000000-000")),
                     (HttpMethod.Get, ObserverRoutes.Dialogs("20260917-000000-000")),
                 })
        {
            using var запрос = new HttpRequestMessage(метод, путь) { Content = new StringContent("{\"text\":\"launch x\"}", Encoding.UTF8, "application/json") };
            using var ответ = await голый.SendAsync(запрос);
            Assert.Equal(HttpStatusCode.Unauthorized, ответ.StatusCode);
        }
        Assert.Empty(_запуск.Runs);
    }

    [Fact]
    public async Task Запуск_выход_и_ожидание_выхода_дают_полный_журнал_сеанса()
    {
        var принят = await _клиент.RunAsync("launch \"C:\\p\\1.psh\"\nwait exit 30");
        Assert.Equal(1, принят.After);
        var поток = Task.Run(() => ДоКонцаСеанса(принят.Session));

        await Ждать(() => _запуск.Runs.Count == 1);
        _запуск.Runs[0].Sample(3);
        _запуск.Runs[0].Exit(0);
        var факты = await поток;

        Assert.Equal(ProgramFactKinds.SessionStarted, факты[0].Kind);
        Assert.Equal(Enumerable.Range(1, факты.Count).Select(n => (long)n), факты.Select(f => f.Number));
        Assert.Equal("completed", Статус(факты.Single(f => f.Kind == ScenarioFactKinds.ScenarioFinished)));
        var конец = факты[^1];
        Assert.Equal(ProgramFactKinds.SessionFinished, конец.Kind);
        Assert.Equal(SessionEndReasons.ProgramExited, конец.Data.GetProperty("reason").GetString());
        // Факт завершения сценария пишется раньше факта закрытия сеанса: сценарий дорабатывает на пришедших сигналах.
        Assert.True(факты.FindIndex(f => f.Kind == ScenarioFactKinds.ScenarioFinished) < факты.Count - 1);
        // Итог сеанса — один раз, после прекращения наблюдения и перед последним фактом.
        Assert.Equal(1, _запуск.Runs[0].Conclusions);
        Assert.True(_запуск.Runs[0].ConcludedAfterDispose);
        Assert.Equal(ProgramFactKinds.ServiceFiles, факты[^2].Kind);

        // Сколько фактов получил клиент, столько строк в журнале на диске.
        var журнал = File.ReadAllLines(Path.Combine(_каталог, принят.Session + ".jsonl"));
        Assert.Equal(факты.Count, журнал.Length);
    }

    [Fact]
    public async Task Переподключение_с_номера_отдаёт_ровно_хвост()
    {
        var принят = await _клиент.RunAsync("launch \"C:\\p\\1.psh\"");
        await ДоКонцаСценария(принят);
        var запуск = _запуск.Runs[0];
        запуск.Sample(5);

        // Клиент получил первые N фактов и умер.
        const int получено = 7;
        var первые = new List<Fact>();
        using (var время = new CancellationTokenSource(Терпение))
        {
            await foreach (var факт in _клиент.StreamAsync(принят.Session, 0, null, время.Token))
            {
                первые.Add(факт);
                if (первые.Count == получено)
                {
                    break;
                }
            }
        }

        // Пока его нет, программа работает дальше.
        запуск.Sample(4);
        var хвост = Task.Run(() => ДоКонцаСеанса(принят.Session, первые[^1].Number));
        запуск.Sample(2);
        запуск.Exit(0);
        var остальные = await хвост;

        var все = первые.Concat(остальные).ToList();
        Assert.Equal(получено + 1, остальные[0].Number);
        Assert.Equal(Enumerable.Range(1, все.Count).Select(n => (long)n), все.Select(f => f.Number));
        Assert.Equal(File.ReadAllLines(Path.Combine(_каталог, принят.Session + ".jsonl")).Length, все.Count);
    }

    [Fact]
    public async Task Заголовок_Last_Event_ID_работает_как_номер()
    {
        var принят = await _клиент.RunAsync("launch \"C:\\p\\1.psh\"");
        await ДоКонцаСценария(принят);
        _запуск.Runs[0].Exit(0);
        await Ждать(async () => !(await _клиент.SessionsAsync()).Single().Active);

        using var голый = new HttpClient { BaseAddress = new Uri(_наблюдатель.Urls.Single()) };
        using var запрос = new HttpRequestMessage(HttpMethod.Get, ObserverRoutes.Stream(принят.Session));
        запрос.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Ключ);
        запрос.Headers.Add("Last-Event-ID", "4");
        using var ответ = await голый.SendAsync(запрос);
        var текст = await ответ.Content.ReadAsStringAsync();

        Assert.StartsWith("id: 5\n", текст, StringComparison.Ordinal);
        Assert.EndsWith("event: end\ndata: {}\n\n", текст, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Фильтр_по_виду_факта()
    {
        var принят = await _клиент.RunAsync("launch \"C:\\p\\1.psh\"");
        await ДоКонцаСценария(принят);
        var поток = Task.Run(() => ДоКонцаСеанса(принят.Session, 0, [ProgramFactKinds.ProcessSample, ProgramFactKinds.SessionFinished]));
        _запуск.Runs[0].Sample(3);
        _запуск.Runs[0].Exit(0);

        var факты = await поток;

        Assert.Equal(
            [ProgramFactKinds.ProcessSample, ProgramFactKinds.ProcessSample, ProgramFactKinds.ProcessSample, ProgramFactKinds.SessionFinished],
            факты.Select(f => f.Kind));
        var изЖурнала = new List<Fact>();
        await foreach (var факт in _клиент.ReadFactsAsync(принят.Session, 0, [ProgramFactKinds.ProcessSample]))
        {
            изЖурнала.Add(факт);
        }
        Assert.Equal(факты.Take(3).Select(f => f.Number), изЖурнала.Select(f => f.Number));
    }

    [Fact]
    public async Task Закрытый_сеанс_читается_с_диска_после_перезапуска_наблюдателя()
    {
        var принят = await _клиент.RunAsync("launch \"C:\\p\\1.psh\"");
        await ДоКонцаСценария(принят);
        _запуск.Runs[0].Exit(0);
        var доПерезапуска = await ДоКонцаСеанса(принят.Session);

        _клиент.Dispose();
        await _наблюдатель.StopAsync();
        await _наблюдатель.DisposeAsync();
        await Поднять();

        var сеанс = Assert.Single(await _клиент.SessionsAsync());
        Assert.Equal(new SessionSummary(принят.Session, false, null), сеанс);
        var послеПерезапуска = await ДоКонцаСеанса(принят.Session, 3);
        Assert.Equal(доПерезапуска.Skip(3), послеПерезапуска, (a, b) => a.Number == b.Number && a.Kind == b.Kind);
    }

    [Fact]
    public async Task Прекращение_наблюдения_не_закрывает_программу()
    {
        var принят = await _клиент.RunAsync("launch \"C:\\p\\1.psh\"\nwait exit 600");
        var поток = Task.Run(() => ДоКонцаСеанса(принят.Session));
        await Ждать(() => _запуск.Runs.Count == 1);

        await _клиент.StopAsync(принят.Session);
        var факты = await поток;

        Assert.True(_запуск.Runs[0].Disposed);
        Assert.Equal(1, _запуск.Runs[0].Conclusions);
        Assert.DoesNotContain(факты, f => f.Kind == ProgramFactKinds.ProcessExited);
        Assert.Equal("cancelled", Статус(факты.Single(f => f.Kind == ScenarioFactKinds.ScenarioFinished)));
        Assert.Equal(SessionEndReasons.Stopped, факты[^1].Data.GetProperty("reason").GetString());
        var повторно = await Assert.ThrowsAsync<ObserverException>(() => _клиент.StopAsync(принят.Session));
        Assert.Equal(ObserverErrors.SessionFinished, повторно.Error!.Error);
    }

    [Fact]
    public async Task Остановка_наблюдателя_закрывает_сеанс_но_не_программу()
    {
        var принят = await _клиент.RunAsync("launch \"C:\\p\\1.psh\"");
        await ДоКонцаСценария(принят);

        _клиент.Dispose();
        await _наблюдатель.StopAsync();
        await _наблюдатель.DisposeAsync();
        await Поднять();

        Assert.True(_запуск.Runs[0].Disposed);
        var факты = await ДоКонцаСеанса(принят.Session);
        Assert.DoesNotContain(факты, f => f.Kind == ProgramFactKinds.ProcessExited);
        Assert.Equal(SessionEndReasons.ObserverShutdown, факты[^1].Data.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Отмена_сценария_оставляет_сеанс_живым()
    {
        var принят = await _клиент.RunAsync("launch \"C:\\p\\1.psh\"\nwait exit 600");
        await Ждать(() => _запуск.Runs.Count == 1);

        var отменён = await _клиент.CancelAsync();
        var факты = await ДоКонцаСценария(принят);

        Assert.Equal(принят.Session, отменён.Session);
        Assert.Equal("cancelled", Статус(факты[^1]));
        Assert.False(_запуск.Runs[0].Disposed);
        Assert.True((await _клиент.SessionsAsync()).Single().Active);
        var нечего = await Assert.ThrowsAsync<ObserverException>(() => _клиент.CancelAsync());
        Assert.Equal(ObserverErrors.NothingRunning, нечего.Error!.Error);
    }

    [Fact]
    public async Task Рендер_срывается_если_окно_вывода_не_встало()
    {
        var принят = await _клиент.RunAsync("launch \"C:\\p\\1.psh\"\nrender");

        var факты = await ДоКонцаСценария(принят);

        var срыв = факты.Single(f => f.Kind == ScenarioFactKinds.StepFailed);
        Assert.Equal(WindowActionFailures.NoOutputWindow, срыв.Data.GetProperty("reason").GetString());
        Assert.Equal("failed", Статус(факты[^1]));
    }

    [Fact]
    public async Task Несостоявшийся_запуск_срывает_шаг_и_закрывает_сеанс_без_программы()
    {
        _запуск.Fails = true;
        var принят = await _клиент.RunAsync("launch \"C:\\p\\1.psh\"\nwait exit 30");

        var факты = await ДоКонцаСеанса(принят.Session);

        Assert.Equal(
            [ProgramFactKinds.SessionStarted, ScenarioFactKinds.ScenarioStarted, ScenarioFactKinds.StepStarted, ProgramFactKinds.LaunchFailed,
             ScenarioFactKinds.StepFailed, ScenarioFactKinds.ScenarioFinished, ProgramFactKinds.SessionFinished],
            факты.Select(f => f.Kind));
        Assert.Equal(ProgramFactKinds.LaunchFailed, факты[4].Data.GetProperty("reason").GetString());
        Assert.Equal(SessionEndReasons.NoProgram, факты[^1].Data.GetProperty("reason").GetString());

        // Сеанс закрыт — следующий запуск открывает новый.
        _запуск.Fails = false;
        await Ждать(async () => !(await _клиент.SessionsAsync()).Single().Active);
        var второй = await _клиент.RunAsync("launch \"C:\\p\\1.psh\"");
        Assert.NotEqual(принят.Session, второй.Session);
    }

    [Fact]
    public async Task Сырья_нет()
    {
        var принят = await _клиент.RunAsync("launch \"C:\\p\\1.psh\"");
        using var голый = new HttpClient { BaseAddress = new Uri(_наблюдатель.Urls.Single()) };
        голый.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Ключ);

        using var есть = await голый.GetAsync(ObserverRoutes.Raw(принят.Session) + "?from=0&to=10");
        using var нет = await голый.GetAsync(ObserverRoutes.Raw("20000101-000000-000"));
        using var чужой = await голый.GetAsync(ObserverRoutes.Facts("..%2F..%2Fsecret"));

        Assert.Equal(HttpStatusCode.NotFound, есть.StatusCode);
        Assert.Contains(ObserverErrors.NoRaw, await есть.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains(ObserverErrors.UnknownSession, await нет.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, чужой.StatusCode);
    }

    private static async Task Ждать(Func<bool> условие) => await Ждать(() => Task.FromResult(условие()));

    private static async Task Ждать(Func<Task<bool>> условие)
    {
        using var время = new CancellationTokenSource(Терпение);
        while (!await условие())
        {
            await Task.Delay(20, время.Token);
        }
    }
}
