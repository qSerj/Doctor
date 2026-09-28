using System.Diagnostics;
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
/// Метка инцидента (Э6.2, часть Б): живой, закрывающийся и отсутствующий сеанс, занятый замок подключения,
/// последний закончившийся сеанс и файл меток.
/// </summary>
public sealed class IncidentTests : IAsyncLifetime
{
    private const string Ключ = "test-key-0123456789";
    private static readonly DateTime Сейчас = new(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _каталог = Directory.CreateTempSubdirectory("psdoctor-метки-").FullName;
    private readonly FakeLauncher _запуск = new();
    private WebApplication _наблюдатель = null!;
    private ObserverClient _клиент = null!;

    public async Task InitializeAsync()
    {
        _наблюдатель = ObserverHost.Build(new ObserverOptions(IPAddress.Loopback, 0, Ключ, _каталог), _запуск);
        await _наблюдатель.StartAsync();
        _клиент = new ObserverClient(new Uri(_наблюдатель.Urls.Single()), Ключ);
    }

    public async Task DisposeAsync()
    {
        _клиент.Dispose();
        await _наблюдатель.StopAsync();
        await _наблюдатель.DisposeAsync();
        Directory.Delete(_каталог, recursive: true);
    }

    private async Task<List<Fact>> Факты(string сеанс)
    {
        var факты = new List<Fact>();
        await foreach (var факт in _клиент.ReadFactsAsync(сеанс))
        {
            факты.Add(факт);
        }
        return факты;
    }

    private async Task ДоКонцаСеанса(string сеанс)
    {
        using var время = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var _ in _клиент.StreamAsync(сеанс, 0, null, время.Token))
        {
        }
    }

    [Fact]
    public async Task Метка_при_живом_сеансе_ложится_фактом_в_него_и_строкой_в_файл()
    {
        _запуск.Foreign = true;
        var принят = await _клиент.AttachAsync();

        var метка = await _клиент.MarkIncidentAsync(IncidentSources.Wizard, "тормозит");

        Assert.Equal(принят.Session, метка.Session);
        Assert.Equal(ProgramStates.Attached, метка.Program);
        Assert.Equal(1000, метка.ProcessId);
        var факт = Assert.Single(await Факты(принят.Session), f => f.Kind == ProgramFactKinds.Incident);
        Assert.Equal(метка.Number, факт.Number);
        Assert.Equal(IncidentSources.Wizard, факт.Data.GetProperty("source").GetString());
        Assert.Equal("тормозит", факт.Data.GetProperty("note").GetString());
        Assert.Equal(метка, Assert.Single(await _клиент.IncidentsAsync()));
        Assert.Single(await _клиент.SessionsAsync());
    }

    [Fact]
    public async Task Метка_без_сеанса_только_строка_файла()
    {
        var безПрограммы = await _клиент.MarkIncidentAsync(IncidentSources.Wizard);
        _запуск.Foreign = true;
        var мимо = await _клиент.MarkIncidentAsync(IncidentSources.Cli);

        Assert.Null(безПрограммы.Session);
        Assert.Null(безПрограммы.Number);
        Assert.Null(безПрограммы.Recent);
        Assert.Equal(ProgramStates.None, безПрограммы.Program);
        Assert.Null(мимо.Session);
        Assert.Equal(ProgramStates.Unobserved, мимо.Program);
        Assert.Equal([безПрограммы, мимо], await _клиент.IncidentsAsync());
        Assert.Empty(await _клиент.SessionsAsync());
    }

    [Fact]
    public async Task Метка_после_остановленного_сеанса_называет_его_последним()
    {
        _запуск.Foreign = true;
        var принят = await _клиент.AttachAsync();
        await _клиент.StopAsync(принят.Session);
        await ДоКонцаСеанса(принят.Session);

        var метка = await _клиент.MarkIncidentAsync(IncidentSources.Wizard);

        Assert.Null(метка.Session);
        Assert.Equal(принят.Session, метка.Recent);
        var факты = await Факты(принят.Session);
        Assert.Equal(ProgramFactKinds.SessionFinished, факты[^1].Kind);
        Assert.DoesNotContain(факты, f => f.Kind == ProgramFactKinds.Incident);
    }

    [Fact]
    public async Task Метка_не_ждёт_замка_подключения()
    {
        _запуск.Foreign = true;
        _запуск.FindDelay = TimeSpan.FromSeconds(3);
        var подключение = Task.Run(() => _клиент.AttachAsync());
        await _запуск.FindStarted.Task;

        var часы = Stopwatch.StartNew();
        var метка = await _клиент.MarkIncidentAsync(IncidentSources.Wizard);
        часы.Stop();

        Assert.True(часы.Elapsed < TimeSpan.FromSeconds(1), $"метка ждала {часы.Elapsed}");
        Assert.Equal(ProgramStates.Unobserved, метка.Program);
        await подключение;
    }

    [Fact]
    public async Task Кривой_запрос_метки_отвергается()
    {
        using var http = new HttpClient { BaseAddress = new Uri(_наблюдатель.Urls.Single()) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Ключ);
        foreach (var тело in new string?[]
                 {
                     null,
                     "не json",
                     """{"source":""}""",
                     $$"""{"source":"{{new string('x', IncidentRequest.MaxSource + 1)}}"}""",
                     $$"""{"source":"wizard","note":"{{new string('x', IncidentRequest.MaxNote + 1)}}"}""",
                 })
        {
            using var ответ = await http.PostAsync(ObserverRoutes.Incidents,
                тело is null ? null : new StringContent(тело, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, ответ.StatusCode);
        }
        Assert.Empty(await _клиент.IncidentsAsync());
    }

    [Fact]
    public async Task Оборванная_строка_файла_не_портит_следующую_метку()
    {
        var первая = await _клиент.MarkIncidentAsync(IncidentSources.Wizard);
        await File.AppendAllTextAsync(Path.Combine(_каталог, ObservationService.IncidentsFile), """{"atUtc":"2026-09-28T""");

        var вторая = await _клиент.MarkIncidentAsync(IncidentSources.Wizard);

        Assert.Equal([первая, вторая], await _клиент.IncidentsAsync());
    }

    [Fact]
    public async Task Закрывающийся_сеанс_метку_не_принимает()
    {
        var каталог = Path.Combine(_каталог, "сеанс");
        var сеанс = ObservationSession.Open(каталог, "20260928-120000-000", new ObserverHealth("test", null), _ => { }, journal: new StringWriter());
        var действие = new ДолгоеДействие();
        _ = сеанс.RunScenario(ScenarioParser.Parse("launch \"C:\\p\\1.psh\"").Scenario!, действие, null, CancellationToken.None);
        await действие.Начато.Task;

        var закрытие = сеанс.FinishAsync(SessionEndReasons.Stopped);
        var вовремя = сеанс.TryRecord(ProgramFactKinds.Incident, new { source = IncidentSources.Wizard });
        действие.Отпустить();
        await закрытие;
        var после = сеанс.TryRecord(ProgramFactKinds.Incident, new { source = IncidentSources.Wizard });

        Assert.Null(вовремя);
        Assert.Null(после);
        var факты = сеанс.Log.After(0);
        Assert.Equal(ProgramFactKinds.SessionFinished, факты[^1].Kind);
        Assert.DoesNotContain(факты, f => f.Kind == ProgramFactKinds.Incident);
    }

    [Fact]
    public async Task Последний_сеанс_в_пределах_получаса_а_отвергнутый_не_считается()
    {
        var каталог = Path.Combine(_каталог, "журналы");
        Directory.CreateDirectory(каталог);
        Журнал(каталог, Сейчас.AddHours(-3), TimeSpan.FromMinutes(10));
        var нужный = Журнал(каталог, Сейчас.AddHours(-1), TimeSpan.FromMinutes(45));
        Журнал(каталог, Сейчас.AddMinutes(-5), TimeSpan.FromSeconds(1), причина: SessionEndReasons.NoProgram);

        await using var служба = Служба(каталог);

        Assert.Equal(нужный, служба.Incident(IncidentSources.Wizard, null)!.Recent);
    }

    [Fact]
    public async Task Оборванный_сеанс_тоже_последний()
    {
        var каталог = Path.Combine(_каталог, "журналы");
        Directory.CreateDirectory(каталог);
        Журнал(каталог, Сейчас.AddHours(-2), TimeSpan.FromMinutes(10));
        var оборванный = Журнал(каталог, Сейчас.AddMinutes(-40), TimeSpan.FromMinutes(20), оборван: true);

        await using var служба = Служба(каталог);

        Assert.Equal(оборванный, служба.Incident(IncidentSources.Wizard, null)!.Recent);
    }

    [Fact]
    public async Task Сеанс_старше_получаса_не_последний()
    {
        var каталог = Path.Combine(_каталог, "журналы");
        Directory.CreateDirectory(каталог);
        Журнал(каталог, Сейчас.AddHours(-1), TimeSpan.FromMinutes(29));

        await using var служба = Служба(каталог);

        Assert.Null(служба.Incident(IncidentSources.Wizard, null)!.Recent);
    }

    private static ObservationService Служба(string каталог) =>
        new(каталог, new FakeLauncher(), new ObserverHealth("test", null), utcNow: () => Сейчас);

    /// <summary>Подложенный журнал: открыт, длился, закрыт с причиной или оборван с недописанной строкой.</summary>
    private static string Журнал(string каталог, DateTime открыт, TimeSpan длился, string причина = SessionEndReasons.Stopped, bool оборван = false)
    {
        var id = SessionIds.New(открыт, _ => false);
        var строки = new List<string>
        {
            $$$"""{"number":1,"elapsed":"00:00:00","session":"{{{id}}}","processId":null,"kind":"session-started","data":{"origin":null}}""",
        };
        строки.Add(оборван
            ? $$$"""{"number":2,"elapsed":"{{{длился:c}}}","session":"{{{id}}}","processId":1000,"kind":"process-sample","data":{}}"""
            : $$$"""{"number":2,"elapsed":"{{{длился:c}}}","session":"{{{id}}}","processId":null,"kind":"session-finished","data":{"reason":"{{{причина}}}"}}""");
        var текст = string.Join("\n", строки) + "\n" + (оборван ? """{"number":3,"elapsed":"01:""" : "");
        File.WriteAllText(Path.Combine(каталог, id + SessionIds.JournalExtension), текст);
        return id;
    }

    /// <summary>Действие сценария, которое не кончается, пока тест не отпустит: сеанс тем временем закрывается.</summary>
    private sealed class ДолгоеДействие : IScenarioActions
    {
        private readonly TaskCompletionSource<ActionResult> _итог = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Начато { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Отпустить() => _итог.TrySetResult(ActionResult.Done);

        public Task<ActionResult> RunAsync(ActionStep step, CancellationToken cancellationToken)
        {
            Начато.TrySetResult();
            return _итог.Task;
        }
    }
}
