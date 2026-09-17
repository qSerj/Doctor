using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using PsDoctor.Core.Observation;
using PsDoctor.Core.Scenarios;
using PsDoctor.Observer.Client;
using Xunit;

namespace PsDoctor.Observer.Tests;

/// <summary>
/// Окна и действия в сеансе — на подменённой программе, везде. Настоящие окна и нажатие — в
/// <c>ProgramRunTests</c> на стенде, в сеансе пользователя.
/// </summary>
public sealed class WindowScenarioTests : IAsyncLifetime
{
    private const string Ключ = "test-key-0123456789";
    private const string Запуск = "launch \"C:\\lab\\p\\1.psh\"";
    private static readonly TimeSpan Терпение = TimeSpan.FromSeconds(10);

    private static readonly DialogInfo СтарыйФормат = new(0x10, "Old Show format detected.", ["This show was created with an older version of ProShow."], ["ОК"], "#32770", 1000);
    private static readonly DialogInfo Шрифт = new(0x20, "Message", [], ["Ok to All", "Ok"], "AGDSDocParent", 1000);

    private readonly string _каталог = Directory.CreateTempSubdirectory("psdoctor-окна-").FullName;
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

    [Fact]
    public async Task Сценарий_Л1_диалоги_заголовок_закрытие_и_выход_проходит_без_человека()
    {
        var принят = await _клиент.RunAsync(string.Join('\n',
            Запуск, "wait dialog 120", "press \"ОК\"", "wait dialog 30", "press \"Ok\"", "wait title \"1.psh\" 600", "close", "wait exit 60"));
        var программа = await Программа();
        // Программа ведёт себя как ProShow: диалог о шрифте встаёт после «ОК», имя файла в заголовке — после «Ok».
        программа.AfterPress = кнопка =>
        {
            if (кнопка == "ОК")
            {
                программа.OpenDialog(Шрифт);
            }
            else
            {
                программа.Title("ProShow Producer - Профиль - 1.psh");
            }
        };
        программа.Title("ProShow Producer - Профиль");
        программа.OpenDialog(СтарыйФормат);

        var факты = await ДоКонцаСеанса(принят.Session);

        Assert.Equal("completed", Итог(факты).GetProperty("status").GetString());
        Assert.Equal(["ОК", "Ok"], программа.Pressed);
        Assert.Equal(1, программа.CloseRequests);
        var засчитаны = факты.Where(f => f.Kind == ScenarioFactKinds.StepDone && f.Data.GetProperty("dialog").ValueKind == JsonValueKind.Object)
            .Select(f => f.Data.GetProperty("dialog").GetProperty("title").GetString());
        Assert.Equal(["Old Show format detected.", "Message"], засчитаны);
        Assert.Equal(SessionEndReasons.ProgramExited, факты[^1].Data.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Неожиданный_диалог_останавливает_сценарий_и_ничего_не_нажато()
    {
        var принят = await _клиент.RunAsync(string.Join('\n', Запуск, "wait title \"1.psh\" 600", "close", "wait exit 60"));
        var программа = await Программа();
        программа.Title("ProShow Producer - Профиль");
        программа.OpenDialog(СтарыйФормат);

        var факты = await ДоКонцаСценария(принят);

        var итог = Итог(факты);
        Assert.Equal("stopped", итог.GetProperty("status").GetString());
        Assert.Equal(2, итог.GetProperty("line").GetInt32());
        var диалог = факты.Single(f => f.Kind == ScenarioFactKinds.UnexpectedDialog).Data.GetProperty("dialog");
        Assert.Equal("Old Show format detected.", диалог.GetProperty("title").GetString());
        Assert.Equal("This show was created with an older version of ProShow.", диалог.GetProperty("texts")[0].GetString());
        Assert.Equal("ОК", диалог.GetProperty("buttons")[0].GetString());
        Assert.Empty(программа.Pressed);
        Assert.Equal(0, программа.CloseRequests);
        Assert.DoesNotContain(факты, f => f.Kind == ProgramFactKinds.DialogPressed);
    }

    [Fact]
    public async Task Текущие_диалоги_командой_и_нажатие_отдельным_сценарием()
    {
        var принят = await _клиент.RunAsync(Запуск);
        await ДоКонцаСценария(принят);
        var программа = _запуск.Runs[0];
        программа.OpenDialog(СтарыйФормат);
        программа.OpenDialog(Шрифт);

        var открыты = await _клиент.DialogsAsync(принят.Session);
        Assert.Equal([0x10L, 0x20L], открыты.Select(d => d.Handle));
        Assert.Equal(["Ok to All", "Ok"], открыты[1].Buttons);
        Assert.Equal("AGDSDocParent", открыты[1].Class);

        // Диалог, открытый до сценария, этому сценарию не неожиданный: нажатие из пульта — сценарий из одной строки.
        var нажать = await _клиент.RunAsync("press \"Ok to All\"");
        var факты = await ДоКонцаСценария(нажать);

        Assert.Equal("completed", Итог(факты).GetProperty("status").GetString());
        Assert.Equal(["Ok to All"], программа.Pressed);
        Assert.Equal([0x10L], (await _клиент.DialogsAsync(принят.Session)).Select(d => d.Handle));
    }

    [Fact]
    public async Task Нажатие_без_такой_кнопки_и_без_диалога_срывает_шаг()
    {
        var принят = await _клиент.RunAsync(Запуск + "\npress \"ОК\"");
        var факты = await ДоКонцаСценария(принят);
        Assert.Equal(WindowActionFailures.NoDialog, Срыв(факты));

        _запуск.Runs[0].OpenDialog(Шрифт);
        факты = await ДоКонцаСценария(await _клиент.RunAsync("press \"Cancel\""));
        Assert.Equal(WindowActionFailures.NoButton, Срыв(факты));
        Assert.Empty(_запуск.Runs[0].Pressed);
    }

    [Fact]
    public async Task Заголовок_известный_до_сценария_засчитывается_ожиданию()
    {
        var принят = await _клиент.RunAsync(Запуск);
        await ДоКонцаСценария(принят);
        _запуск.Runs[0].Title("ProShow Producer - Профиль - 1.psh");
        await Ждать(async () => (await Факты(принят.Session)).Any(f => f.Kind == ProgramFactKinds.MainWindow));

        var факты = await ДоКонцаСценария(await _клиент.RunAsync("wait title \"1.psh\" 1"));

        Assert.Equal("completed", Итог(факты).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Ожидание_покоя_по_замерам_активности()
    {
        var принят = await _клиент.RunAsync(Запуск + "\nwait idle 1 30");
        var программа = await Программа();

        // Покой отсчитывается от первого спокойного замера; время сценарию идёт тиками сеанса.
        программа.Activity(false);
        программа.Activity(true);
        var факты = await ДоКонцаСценария(принят);

        Assert.Equal("completed", Итог(факты).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Диалоги_закрытого_и_неизвестного_сеанса_отказ()
    {
        var принят = await _клиент.RunAsync(Запуск);
        await ДоКонцаСценария(принят);
        _запуск.Runs[0].Exit(0);
        await Ждать(async () => !(await _клиент.SessionsAsync()).Single().Active);

        var закрыт = await Assert.ThrowsAsync<ObserverException>(() => _клиент.DialogsAsync(принят.Session));
        var нет = await Assert.ThrowsAsync<ObserverException>(() => _клиент.DialogsAsync("20000101-000000-000"));

        Assert.Equal(ObserverErrors.SessionFinished, закрыт.Error!.Error);
        Assert.Equal(HttpStatusCode.NotFound, нет.Status);
        Assert.Equal(ObserverErrors.UnknownSession, нет.Error!.Error);
    }

    private async Task<FakeLauncher.FakeRun> Программа()
    {
        await Ждать(() => Task.FromResult(_запуск.Runs.Count == 1));
        return _запуск.Runs[0];
    }

    private static JsonElement Итог(List<Fact> факты) => факты.Last(f => f.Kind == ScenarioFactKinds.ScenarioFinished).Data;

    private static string? Срыв(List<Fact> факты) => факты.Last(f => f.Kind == ScenarioFactKinds.StepFailed).Data.GetProperty("reason").GetString();

    private async Task<List<Fact>> Факты(string сеанс)
    {
        var факты = new List<Fact>();
        await foreach (var факт in _клиент.ReadFactsAsync(сеанс))
        {
            факты.Add(факт);
        }
        return факты;
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

    private async Task<List<Fact>> ДоКонцаСеанса(string сеанс)
    {
        var факты = new List<Fact>();
        using var время = new CancellationTokenSource(Терпение);
        await foreach (var факт in _клиент.StreamAsync(сеанс, 0, null, время.Token))
        {
            факты.Add(факт);
        }
        return факты;
    }

    private static async Task Ждать(Func<Task<bool>> условие)
    {
        using var время = new CancellationTokenSource(Терпение);
        while (!await условие())
        {
            await Task.Delay(20, время.Token);
        }
    }
}
