using System.Net;
using Microsoft.AspNetCore.Builder;
using PsDoctor.Core.Observation;
using PsDoctor.Observer;
using PsDoctor.Observer.Client;
using PsDoctor.Workbench.ViewModels;
using Xunit;

namespace PsDoctor.Workbench.Tests;

/// <summary>
/// Пульт против настоящего наблюдателя на петле с подменённым запуском программы — тот же путь, каким
/// владелец с Linux-хоста ходит на стенд, только без Windows. Интерфейс не поднимается: всё, что делает
/// окно, — зовёт эти методы и показывает эти списки.
/// </summary>
public sealed class WorkbenchViewModelTests : IAsyncLifetime
{
    private const string Ключ = "workbench-test-key-0123456789";
    private const string Проект = @"C:\lab\p1\1.psh";

    private readonly string каталог = Directory.CreateTempSubdirectory("psdoctor-workbench-").FullName;
    private readonly Запуск запуск = new();
    private WebApplication наблюдатель = null!;
    private string файлКлюча = null!;

    public async Task InitializeAsync()
    {
        файлКлюча = Path.Combine(каталог, "observer.key");
        await File.WriteAllTextAsync(файлКлюча, Ключ + "\n");
        наблюдатель = ObserverHost.Build(new ObserverOptions(IPAddress.Loopback, 0, Ключ, Path.Combine(каталог, "sessions")), запуск);
        await наблюдатель.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await наблюдатель.StopAsync();
        await наблюдатель.DisposeAsync();
        Directory.Delete(каталог, recursive: true);
    }

    [Fact]
    public void Сценарий_выполняется_лента_наполняется_шаги_отмечаются() => ОдинПоток.Выполнить(async () =>
    {
        using var пульт = await ПодключённыйAsync();
        пульт.ScenarioText = $"launch {Проект}\nwait dialog 10\npress \"Ok\"\nclose\nwait exit 10";

        await пульт.RunAsync();
        await ОдинПоток.ЖдатьAsync(() => !пульт.ScenarioRunning, "конца сценария");

        Assert.Equal(5, пульт.Steps.Count);
        Assert.All(пульт.Steps, шаг => Assert.Equal(StepState.Done, шаг.State));
        Assert.Contains("completed", пульт.Outcome, StringComparison.Ordinal);
        var виды = пульт.Facts.Select(факт => факт.Kind).ToList();
        Assert.Equal(ProgramFactKinds.SessionStarted, виды[0]);
        Assert.Contains(ProgramFactKinds.ProgramLaunched, виды);
        Assert.Contains(ProgramFactKinds.DialogPressed, виды);
        // Номера в ленте идут подряд: пульт показывает журнал, а не то, что успел поймать.
        Assert.Equal(Enumerable.Range(1, пульт.Facts.Count).Select(n => (long)n), пульт.Facts.Select(факт => факт.Number));
    });

    [Fact]
    public void Кнопка_диалога_нажимается_из_пульта() => ОдинПоток.Выполнить(async () =>
    {
        using var пульт = await ПодключённыйAsync();
        пульт.ScenarioText = $"launch {Проект}";
        await пульт.RunAsync();
        await ОдинПоток.ЖдатьAsync(() => пульт.Dialogs.Count == 1, "диалога в пульте");

        var диалог = пульт.Dialogs[0];
        Assert.Equal(Запуск.Диалог.Buttons, диалог.Buttons);

        await пульт.PressAsync("Ok");
        await ОдинПоток.ЖдатьAsync(() => пульт.Dialogs.Count == 0, "закрытия диалога");

        Assert.Equal(["Ok"], запуск.Последний!.Нажатия);
    });

    [Fact]
    public void Отмена_прекращает_сценарий_и_не_трогает_программу() => ОдинПоток.Выполнить(async () =>
    {
        using var пульт = await ПодключённыйAsync();
        // Диалог встаёт при запуске, и его забирает `wait dialog`: иначе он остановил бы `wait exit`
        // как неожиданный, и отменять было бы уже нечего.
        пульт.ScenarioText = $"launch {Проект}\nwait dialog 10\nwait exit 600";
        await пульт.RunAsync();
        await ОдинПоток.ЖдатьAsync(() => пульт.Steps[2].State == StepState.Running, "начала ожидания выхода");

        await пульт.CancelAsync();
        await ОдинПоток.ЖдатьAsync(() => !пульт.ScenarioRunning, "конца сценария");

        Assert.Contains("cancelled", пульт.Outcome, StringComparison.Ordinal);
        Assert.Equal(StepState.Failed, пульт.Steps[2].State);
        Assert.Equal(0, запуск.Последний!.Закрытий);
        Assert.True(запуск.Последний.Жив);
    });

    [Fact]
    public void Прекращение_наблюдения_закрывает_сеанс_и_оставляет_программу() => ОдинПоток.Выполнить(async () =>
    {
        using var пульт = await ПодключённыйAsync();
        пульт.ScenarioText = $"launch {Проект}";
        await пульт.RunAsync();
        await ОдинПоток.ЖдатьAsync(() => !пульт.ScenarioRunning, "конца сценария");

        await пульт.StopAsync();
        await ОдинПоток.ЖдатьAsync(() => пульт.Sessions.All(сеанс => !сеанс.Active), "закрытия сеанса");

        Assert.Equal(0, запуск.Последний!.Закрытий);
        Assert.True(запуск.Последний.Жив);
        Assert.True(запуск.Последний.Отпущен);
    });

    [Fact]
    public void Фильтр_по_виду_оставляет_в_ленте_только_его() => ОдинПоток.Выполнить(async () =>
    {
        using var пульт = await ПодключённыйAsync();
        пульт.ScenarioText = $"launch {Проект}";
        await пульт.RunAsync();
        await ОдинПоток.ЖдатьAsync(() => !пульт.ScenarioRunning, "конца сценария");

        пульт.KindFilter = ProgramFactKinds.DialogOpened;
        await пульт.SelectAsync(пульт.SelectedSession);
        await ОдинПоток.ЖдатьAsync(() => пульт.Facts.Count > 0, "фактов после фильтра");

        Assert.All(пульт.Facts, факт => Assert.Equal(ProgramFactKinds.DialogOpened, факт.Kind));
    });

    [Fact]
    public void Незнакомый_шаг_виден_до_сети() => ОдинПоток.Выполнить(async () =>
    {
        using var пульт = await ПодключённыйAsync();
        пульт.ScenarioText = "прыжок";

        await пульт.RunAsync();

        Assert.Contains("строка 1", пульт.Status, StringComparison.Ordinal);
        Assert.Empty(пульт.Steps);
        Assert.Empty(пульт.Sessions);
        Assert.False(пульт.ScenarioRunning);
    });

    [Fact]
    public void Отказ_наблюдателя_показывается_устойчивым_именем() => ОдинПоток.Выполнить(async () =>
    {
        using var пульт = await ПодключённыйAsync();
        пульт.ScenarioText = "close";

        await пульт.RunAsync();

        Assert.Contains(ObserverErrors.NoSession, пульт.Status, StringComparison.Ordinal);
    });

    [Fact]
    public void Чужой_ключ_виден_в_состоянии() => ОдинПоток.Выполнить(async () =>
    {
        var чужой = Path.Combine(каталог, "wrong.key");
        await File.WriteAllTextAsync(чужой, "wrong-key");
        using var пульт = new WorkbenchViewModel(environment: _ => null)
        {
            Address = наблюдатель.Urls.Single(),
            KeyFile = чужой,
        };

        await пульт.ConnectAsync();

        Assert.False(пульт.Connected);
        Assert.Contains("401", пульт.Status, StringComparison.Ordinal);
    });

    [Fact]
    public void Без_адреса_пульт_не_ходит_в_сеть() => ОдинПоток.Выполнить(async () =>
    {
        using var пульт = new WorkbenchViewModel(environment: _ => null) { Address = "не адрес", KeyFile = файлКлюча };

        await пульт.ConnectAsync();

        Assert.False(пульт.Connected);
        Assert.Contains("адрес не разобран", пульт.Status, StringComparison.Ordinal);
    });

    [Fact]
    public void Адрес_и_ключ_берутся_из_переменных_окружения() => ОдинПоток.Выполнить(() =>
    {
        using var пульт = new WorkbenchViewModel(environment: имя => имя switch
        {
            ObserverConnection.UrlVariable => "http://192.168.56.5:8100",
            ObserverConnection.KeyFileVariable => файлКлюча,
            _ => null,
        });

        Assert.Equal("http://192.168.56.5:8100", пульт.Address);
        Assert.Equal(файлКлюча, пульт.KeyFile);
        return Task.CompletedTask;
    });

    private async Task<WorkbenchViewModel> ПодключённыйAsync()
    {
        var пульт = new WorkbenchViewModel(environment: _ => null)
        {
            Address = наблюдатель.Urls.Single(),
            KeyFile = файлКлюча,
        };
        await пульт.ConnectAsync();
        Assert.True(пульт.Connected, пульт.Status);
        return пульт;
    }
}
