using System.Net;
using Microsoft.AspNetCore.Builder;
using PsDoctor.Core.Observation;
using PsDoctor.Core.Scenarios;
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
    public void Опыт_показывает_инструкцию_и_ждёт_кнопки_Сделано() => ОдинПоток.Выполнить(async () =>
    {
        using var пульт = await ПодключённыйAsync();
        пульт.ScenarioText = $"launch {Проект}\nwait dialog 10\npress \"Ok\"\n"
            + "say \"Дважды щёлкните по третьему слайду\"\nwait confirm 20\nsay \"Закрывайте ProShow\"\nwait exit 20";

        await пульт.RunAsync();
        await ОдинПоток.ЖдатьAsync(() => пульт.AwaitingConfirm, "ожидания подтверждения");
        Assert.Equal("Дважды щёлкните по третьему слайду", пульт.Instruction);
        Assert.True(пульт.CanConfirm);
        Assert.True(пульт.Steps[4].IsCurrent);

        await пульт.ConfirmAsync();
        await ОдинПоток.ЖдатьAsync(() => пульт.Instruction == "Закрывайте ProShow", "второй инструкции");
        Assert.False(пульт.CanConfirm);
        запуск.Последний!.Выйти();
        await ОдинПоток.ЖдатьAsync(() => !пульт.ScenarioRunning, "конца сценария");
        Assert.Equal("", пульт.Instruction);

        Assert.Contains("completed", пульт.Outcome, StringComparison.Ordinal);
        Assert.All(пульт.Steps, шаг => Assert.Equal(StepState.Done, шаг.State));
        Assert.Contains(пульт.Facts, факт => факт.Kind == ScenarioFactKinds.OperatorConfirmed);
    });

    [Fact]
    public void Инструкция_гаснет_когда_кончился_шаг_после_неё() => ОдинПоток.Выполнить(async () =>
    {
        using var пульт = await ПодключённыйAsync();
        пульт.ScenarioText = $"launch {Проект}\nwait dialog 10\npress \"Ok\"\n"
            + "say \"Дважды щёлкните по третьему слайду\"\nwait confirm 20\nwait 20";

        await пульт.RunAsync();
        await ОдинПоток.ЖдатьAsync(() => пульт.AwaitingConfirm, "ожидания подтверждения");
        Assert.Equal("Дважды щёлкните по третьему слайду", пульт.Instruction);

        await пульт.ConfirmAsync();
        await ОдинПоток.ЖдатьAsync(() => пульт.Steps[5].IsCurrent, "паузы после подтверждения");
        Assert.Equal("", пульт.Instruction);
        await пульт.CancelAsync();
        await ОдинПоток.ЖдатьAsync(() => !пульт.ScenarioRunning, "отмены сценария");
    });

    [Fact]
    public void Опыты_открываются_списком_из_каталога() => ОдинПоток.Выполнить(async () =>
    {
        var опыты = Directory.CreateDirectory(Path.Combine(каталог, "experiments")).FullName;
        await File.WriteAllTextAsync(Path.Combine(опыты, "b-второй.txt"), "close");
        await File.WriteAllTextAsync(Path.Combine(опыты, "a-первый.txt"), "say \"Закрывайте ProShow\"\nwait exit 60");
        await File.WriteAllTextAsync(Path.Combine(опыты, "заметки.md"), "не сценарий");
        using var пульт = new WorkbenchViewModel(environment: _ => null, experimentsDirectory: опыты);

        await пульт.OpenExperimentAsync(пульт.Experiments[0]);

        Assert.Equal(["a-первый", "b-второй"], пульт.Experiments.Select(опыт => опыт.Name));
        Assert.Equal("say \"Закрывайте ProShow\"\nwait exit 60", пульт.ScenarioText);
    });

    [Fact]
    public void Опыты_из_поставки_разбираются_и_пассивный_не_трогает_программу() => ОдинПоток.Выполнить(() =>
    {
        using var пульт = new WorkbenchViewModel(environment: _ => null);

        Assert.Contains(пульт.Experiments, опыт => опыт.Name == "e42-slide-001");
        foreach (var опыт in пульт.Experiments)
        {
            var разбор = ScenarioParser.Parse(File.ReadAllText(опыт.Path));
            Assert.True(разбор.Errors.Count == 0, $"{опыт.Name}: {string.Join("; ", разбор.Errors)}");
            if (опыт.Name.EndsWith("-attach", StringComparison.Ordinal))
            {
                Assert.False(разбор.Scenario!.DrivesProgram, опыт.Name);
            }
        }
        return Task.CompletedTask;
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
    public void Вехи_не_вытесняются_телеметрией() => ОдинПоток.Выполнить(async () =>
    {
        using var пульт = await ПодключённыйAsync();
        пульт.ScenarioText = $"launch {Проект}";
        await пульт.RunAsync();
        await ОдинПоток.ЖдатьAsync(() => !пульт.ScenarioRunning, "конца сценария");

        запуск.Последний!.Отсчёты(WorkbenchViewModel.TapeLimit + 100);
        await ОдинПоток.ЖдатьAsync(
            () => пульт.Facts.Count == WorkbenchViewModel.TapeLimit && пульт.Facts[^1].Data.Contains("3099", StringComparison.Ordinal),
            "последнего отсчёта");

        // Весь поток держит только последние отсчёты, начало сеанса из него ушло; вехи его хранят.
        Assert.DoesNotContain(пульт.Facts, факт => факт.Kind == ProgramFactKinds.SessionStarted);
        Assert.Equal(ProgramFactKinds.SessionStarted, пульт.Milestones[0].Kind);
        Assert.Contains(пульт.Milestones, факт => факт.Kind == ProgramFactKinds.DialogOpened);
        Assert.DoesNotContain(пульт.Milestones, факт => факт.Kind == ProgramFactKinds.ProcessSample);
        Assert.Same(пульт.Milestones, пульт.Tape);
        пульт.ShowAllFacts = true;
        Assert.Same(пульт.Facts, пульт.Tape);
    });

    [Fact]
    public void Длинный_журнал_догоняется_и_лента_показывает_его_хвост() => ОдинПоток.Выполнить(async () =>
    {
        using var пульт = await ПодключённыйAsync();
        пульт.ScenarioText = $"launch {Проект}";
        await пульт.RunAsync();
        await ОдинПоток.ЖдатьAsync(() => !пульт.ScenarioRunning, "конца сценария");
        запуск.Последний!.Отсчёты(3 * WorkbenchViewModel.TapeLimit);
        запуск.Последний.Выйти();
        await ОдинПоток.ЖдатьAsync(() => пульт.Sessions.Count == 1 && !пульт.Sessions[0].Active, "закрытия сеанса");

        // Закрытый сеанс заново: журнал больше трёх лент читается с начала, а в ленту уходит его хвост, номера подряд.
        await пульт.SelectAsync(пульт.Sessions[0]);
        await ОдинПоток.ЖдатьAsync(
            () => пульт.Facts.Count > 0 && пульт.Facts[^1].Kind == ProgramFactKinds.SessionFinished, "конца журнала в ленте");

        Assert.Equal(WorkbenchViewModel.TapeLimit, пульт.Facts.Count);
        var последний = пульт.Facts[^1].Number;
        Assert.Equal(Enumerable.Range(1, WorkbenchViewModel.TapeLimit).Select(n => последний - WorkbenchViewModel.TapeLimit + n),
            пульт.Facts.Select(факт => факт.Number));
        Assert.Equal(ProgramFactKinds.SessionStarted, пульт.Milestones[0].Kind);
        Assert.Equal(ProgramFactKinds.SessionFinished, пульт.Milestones[^1].Kind);
    });

    [Fact]
    public void Лампы_идут_за_программой_и_прогоном() => ОдинПоток.Выполнить(async () =>
    {
        using var пульт = await ПодключённыйAsync(частоОпрашивать: true);
        Assert.Equal(new Lamp("ProShow", "не запущен", LampTone.Off), пульт.ProgramLamp);
        Assert.Equal(new Lamp("Прогон", "нет", LampTone.Off), пульт.RunLamp);
        Assert.Equal(LampTone.On, пульт.LinkLamp.Tone);

        пульт.ScenarioText = $"launch {Проект}\nwait dialog 10\npress \"Ok\"\nwait confirm 20";
        await пульт.RunAsync();
        await ОдинПоток.ЖдатьAsync(() => пульт.AwaitingConfirm, "ожидания подтверждения");
        await ОдинПоток.ЖдатьAsync(() => пульт.ProgramLamp.IsOn, "лампы программы");

        Assert.Equal("под наблюдением · запуск · pid 1000", пульт.ProgramLamp.Text);
        Assert.Equal(new Lamp("Прогон", "идёт · шаг 4 из 4", LampTone.On), пульт.RunLamp);
        Assert.Equal(PrimaryAction.Confirm, пульт.Primary);
        Assert.Equal("wait confirm 20", пульт.CurrentStep);

        await пульт.ExecutePrimaryAsync();
        await ОдинПоток.ЖдатьAsync(() => !пульт.ScenarioRunning, "конца сценария");
        Assert.Equal(new Lamp("Прогон", "кончился · completed", LampTone.Off), пульт.RunLamp);
        Assert.Equal(PrimaryAction.Run, пульт.Primary);

        запуск.Последний!.Выйти();
        await ОдинПоток.ЖдатьAsync(() => пульт.ProgramLamp.Text == "не запущен", "выхода программы на лампе");
    });

    [Fact]
    public void Открытый_диалог_виден_в_панели_Сейчас() => ОдинПоток.Выполнить(async () =>
    {
        using var пульт = await ПодключённыйAsync();
        пульт.ScenarioText = $"launch {Проект}";
        await пульт.RunAsync();
        await ОдинПоток.ЖдатьAsync(() => пульт.Dialogs.Count == 1, "диалога в пульте");

        Assert.Equal("ProShow Producer", пульт.Dialogs[0].Title);
    });

    [Fact]
    public void Подключение_начинает_пассивный_сеанс_где_команды_программе_отвергаются() => ОдинПоток.Выполнить(async () =>
    {
        запуск.Чужой = true;
        using var пульт = await ПодключённыйAsync();

        await пульт.AttachAsync();
        Assert.Contains("подключён к ProShow, pid 1000", пульт.Status, StringComparison.Ordinal);
        await ОдинПоток.ЖдатьAsync(() => пульт.Facts.Any(факт => факт.Kind == ProgramFactKinds.ProgramAttached), "факта подключения");

        // Лента идёт: то, что программа делает после подключения, доходит до пульта.
        запуск.Последний!.Отсчёты(3);
        await ОдинПоток.ЖдатьAsync(() => пульт.Facts.Count(факт => факт.Kind == ProgramFactKinds.ProcessSample) == 3, "отсчётов в ленте");

        // Команды программе пассивный сеанс отвергает целиком, программа их не видит.
        await пульт.CloseProgramAsync();
        Assert.Contains(ObserverErrors.PassiveSession, пульт.Status, StringComparison.Ordinal);
        await пульт.PressAsync("Ok");
        Assert.Contains(ObserverErrors.PassiveSession, пульт.Status, StringComparison.Ordinal);
        Assert.Equal(0, запуск.Последний.Закрытий);
        Assert.Empty(запуск.Последний.Нажатия);
        Assert.True(запуск.Последний.Жив);

        // Шаги оператора — можно: так идёт опыт e42-slide-001-attach.
        пульт.ScenarioText = "say \"Дважды щёлкните по третьему слайду\"\nwait confirm 20";
        await пульт.RunAsync();
        await ОдинПоток.ЖдатьAsync(() => пульт.AwaitingConfirm, "ожидания подтверждения");
        await пульт.ConfirmAsync();
        await ОдинПоток.ЖдатьAsync(() => !пульт.ScenarioRunning, "конца сценария");
        Assert.Contains("completed", пульт.Outcome, StringComparison.Ordinal);
    });

    [Fact]
    public void Кнопки_диалога_без_имени_не_показываются()
    {
        var диалог = new DialogRow(new DialogInfo(1, "Slide Options", [], ["", " ", "OK", "Cancel"]));

        Assert.Equal(new[] { "OK", "Cancel" }, диалог.Buttons);
    }

    [Fact]
    public void Связь_гаснет_без_наблюдателя_и_возвращается_сама() => ОдинПоток.Выполнить(async () =>
    {
        using var пульт = await ПодключённыйAsync(частоОпрашивать: true);

        var порт = await ОстановитьНаблюдательAsync();
        await ОдинПоток.ЖдатьAsync(() => !пульт.Connected, "потери связи");
        Assert.Equal(LampTone.Alarm, пульт.LinkLamp.Tone);
        Assert.Equal(new Lamp("ProShow", "неизвестно", LampTone.Off), пульт.ProgramLamp);
        Assert.Equal(PrimaryAction.Connect, пульт.Primary);

        await ПоднятьНаблюдательAsync(порт);
        await ОдинПоток.ЖдатьAsync(() => пульт.Connected, "возвращения связи");
        Assert.Equal(LampTone.On, пульт.LinkLamp.Tone);
    });

    [Fact]
    public void Поток_фактов_переживает_больше_пяти_обрывов() => ОдинПоток.Выполнить(async () =>
    {
        using var пульт = await ПодключённыйAsync(частоОпрашивать: true);
        пульт.ScenarioText = $"launch {Проект}";
        await пульт.RunAsync();
        await ОдинПоток.ЖдатьAsync(() => !пульт.ScenarioRunning, "конца сценария");

        // Наблюдатель лежит секунду — при паузе 50 мс это два десятка неудачных переподключений.
        var порт = await ОстановитьНаблюдательAsync();
        await Task.Delay(TimeSpan.FromSeconds(1));
        await ПоднятьНаблюдательAsync(порт);

        // Остановка закрыла сеанс; его конец пульт дочитывает из журнала, продолжив с номера.
        await ОдинПоток.ЖдатьAsync(() => пульт.Facts.Any(факт => факт.Kind == ProgramFactKinds.SessionFinished), "конца сеанса");
        Assert.Contains(SessionEndReasons.ObserverShutdown, пульт.Facts[^1].Data, StringComparison.Ordinal);
        Assert.Equal(Enumerable.Range(1, пульт.Facts.Count).Select(n => (long)n), пульт.Facts.Select(факт => факт.Number));
    });

    [Fact]
    public void Сохранённые_настройки_подставляются_и_пульт_подключается_сам() => ОдинПоток.Выполнить(async () =>
    {
        var настройки = Path.Combine(каталог, "workbench.json");
        new WorkbenchSettings(наблюдатель.Urls.Single(), файлКлюча, @"C:\обмен", Проект, [Проект]).Save(настройки);
        using (var пульт = new WorkbenchViewModel(environment: _ => null, settingsPath: настройки))
        {
            Assert.Equal(Проект, пульт.ShowPath);
            Assert.Equal(@"C:\обмен", пульт.ExchangeDirectory);

            await пульт.StartAsync();

            Assert.True(пульт.Connected, пульт.Status);
            пульт.ShowPath = @"C:\lab\p2\2.psh";
            await пульт.LaunchAsync();
            await ОдинПоток.ЖдатьAsync(() => !пульт.ScenarioRunning, "конца сценария");
        }

        var сохранено = WorkbenchSettings.Load(настройки);
        Assert.Equal(наблюдатель.Urls.Single(), сохранено.Address);
        Assert.Equal(файлКлюча, сохранено.KeyFile);
        Assert.Equal(@"C:\lab\p2\2.psh", сохранено.ShowPath);
        Assert.Equal([@"C:\lab\p2\2.psh", Проект], сохранено.RecentShows);
    });

    [Fact]
    public void Пресет_переключает_машину_и_переподключает() => ОдинПоток.Выполнить(async () =>
    {
        var настройки = Path.Combine(каталог, "workbench.json");
        var пресеты = WorkbenchPreset.DirectoryFor(настройки);
        new WorkbenchPreset("http://127.0.0.1:1", файлКлюча, @"C:\обмен-дом", @"D:\дом\1.psh").Save(Path.Combine(пресеты, "дом.json"));
        new WorkbenchPreset(наблюдатель.Urls.Single(), файлКлюча, null, Проект).Save(Path.Combine(пресеты, "стенд.json"));
        using var пульт = new WorkbenchViewModel(environment: _ => null, settingsPath: настройки);
        Assert.Equal(["дом", "стенд"], пульт.Presets.Select(п => п.Name));

        await пульт.ApplyPresetAsync(пульт.Presets[0]);
        Assert.False(пульт.Connected);
        Assert.Equal(@"D:\дом\1.psh", пульт.ShowPath);
        Assert.StartsWith("пресет дом:", пульт.Status, StringComparison.Ordinal);
        Assert.StartsWith("дом · ", пульт.LinkLamp.Text, StringComparison.Ordinal);

        await пульт.ApplyPresetAsync(пульт.Presets[1]);
        Assert.True(пульт.Connected, пульт.Status);
        Assert.Equal(Проект, пульт.ShowPath);
        // Пустое поле пресета текущее значение не трогает.
        Assert.Equal(@"C:\обмен-дом", пульт.ExchangeDirectory);
        Assert.StartsWith("стенд · на связи", пульт.LinkLamp.Text, StringComparison.Ordinal);
        Assert.Equal("стенд", WorkbenchSettings.Load(настройки).Preset);

        // Ручная правка адреса — уже не та машина.
        пульт.Address = "http://127.0.0.1:2";
        Assert.Null(пульт.SelectedPreset);
    });

    [Fact]
    public void Пресет_сохраняется_из_текущих_настроек() => ОдинПоток.Выполнить(async () =>
    {
        var настройки = Path.Combine(каталог, "workbench.json");
        using (var пульт = new WorkbenchViewModel(environment: _ => null, settingsPath: настройки)
        {
            Address = наблюдатель.Urls.Single(),
            KeyFile = файлКлюча,
            ShowPath = Проект,
        })
        {
            пульт.SavePreset("стенд/");
            Assert.Contains("не годится", пульт.Status, StringComparison.Ordinal);
            Assert.Empty(пульт.Presets);

            пульт.SavePreset(" стенд ");
            Assert.Equal("стенд", пульт.SelectedPreset!.Name);
        }

        // Следующий запуск помнит пресет и показывает его имя, пока не подключился.
        using var снова = new WorkbenchViewModel(environment: _ => null, settingsPath: настройки);
        Assert.Equal("стенд", снова.SelectedPreset!.Name);
        Assert.Equal(new WorkbenchPreset(наблюдатель.Urls.Single(), файлКлюча, "", Проект),
            WorkbenchPreset.Load(снова.SelectedPreset.Path));
        await снова.StartAsync();
        Assert.StartsWith("стенд · на связи", снова.LinkLamp.Text, StringComparison.Ordinal);
    });

    [Fact]
    public void Файл_настроек_прежнего_пульта_читается() => ОдинПоток.Выполнить(async () =>
    {
        var настройки = Path.Combine(каталог, "old.json");
        await File.WriteAllTextAsync(настройки, "{\"ExchangeDirectory\":\"C:\\\\обмен\"}");

        using var пульт = new WorkbenchViewModel(environment: _ => null, settingsPath: настройки);

        Assert.Equal(@"C:\обмен", пульт.ExchangeDirectory);
        Assert.Equal("", пульт.Address);
        Assert.Equal(new Lamp("Связь", "адрес не задан", LampTone.Off), пульт.LinkLamp);
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
    public void Кнопки_до_подключения_не_роняют_пульт() => ОдинПоток.Выполнить(async () =>
    {
        using var пульт = new WorkbenchViewModel(environment: _ => null);

        await пульт.RefreshSessionsAsync();
        await пульт.RefreshDialogsAsync();
        await пульт.CancelAsync();
        await пульт.ConfirmAsync();
        await пульт.AttachAsync();

        Assert.Contains("нет связи", пульт.Status, StringComparison.Ordinal);
        Assert.False(пульт.Busy);
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

    /// <summary>Останавливает наблюдатель и отдаёт его порт — поднять снова на том же адресе.</summary>
    private async Task<int> ОстановитьНаблюдательAsync()
    {
        var порт = new Uri(наблюдатель.Urls.Single()).Port;
        await наблюдатель.StopAsync();
        await наблюдатель.DisposeAsync();
        return порт;
    }

    private async Task ПоднятьНаблюдательAsync(int порт)
    {
        наблюдатель = ObserverHost.Build(new ObserverOptions(IPAddress.Loopback, порт, Ключ, Path.Combine(каталог, "sessions")), запуск);
        await наблюдатель.StartAsync();
    }

    /// <param name="частоОпрашивать">Опрос и переподключение потока — десятки миллисекунд вместо секунд.</param>
    private async Task<WorkbenchViewModel> ПодключённыйAsync(bool частоОпрашивать = false)
    {
        var пульт = new WorkbenchViewModel(environment: _ => null,
            pollInterval: частоОпрашивать ? TimeSpan.FromMilliseconds(100) : null,
            streamRetryPause: частоОпрашивать ? TimeSpan.FromMilliseconds(50) : null)
        {
            Address = наблюдатель.Urls.Single(),
            KeyFile = файлКлюча,
        };
        await пульт.ConnectAsync();
        Assert.True(пульт.Connected, пульт.Status);
        return пульт;
    }
}
