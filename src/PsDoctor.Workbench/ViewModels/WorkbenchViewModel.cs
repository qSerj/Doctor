using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using PsDoctor.Core.Observation;
using PsDoctor.Core.Scenarios;
using PsDoctor.Observer.Client;
using PsDoctor.Workbench;

namespace PsDoctor.Workbench.ViewModels;

/// <summary>
/// Пульт целиком: связь с наблюдателем, лампы состояния, сеансы, лента фактов, шаги сценария и диалоги. Avalonia
/// сюда не заходит — окно только показывает это и зовёт методы, поэтому пульт проверяется тестами без интерфейса.
/// </summary>
/// <remarks>
/// <para><b>Один поток.</b> Все методы вызываются с потока интерфейса и <c>ConfigureAwait(false)</c> не
/// делают нарочно: продолжения возвращаются туда же, и списки меняются только там. Иначе пришлось бы
/// заводить диспетчер, а с ним — вторую истину о том, где живёт состояние.</para>
/// <para><b>Пульт ничего не толкует.</b> Вид факта, имя отказа, причина срыва показываются как пришли:
/// вердикты — не его дело, а слова для Ольги живут в окне оператора, а не здесь. Лампы называют состояние,
/// которое сообщил наблюдатель, а не выводят его.</para>
/// <para><b>Связь живая.</b> Пульт опрашивает <c>/health</c> раз в <see cref="DefaultPollInterval"/>; два пропуска
/// подряд — «нет связи», первый ответ после них — связь восстановлена, без кнопки.</para>
/// </remarks>
public sealed class WorkbenchViewModel : ObservableObject, IDisposable
{
    /// <summary>Сколько последних фактов держит лента. За рендер их тысячи, и держать всё пульту незачем.</summary>
    public const int TapeLimit = 2000;

    /// <summary>Каталог сценариев опытов рядом с пультом: файл <c>*.txt</c> на опыт.</summary>
    public const string ExperimentsFolder = "Experiments";

    /// <summary>Сколько неотвеченных опросов подряд гасят лампу связи: один пропуск — ещё не обрыв.</summary>
    public const int MissesToLose = 2;

    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(3);

    /// <summary>Сколько ждать ответа на опрос: дольше — пропуск. Общий предел клиента в 30 с для лампы велик.</summary>
    public static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan DefaultStreamRetryPause = TimeSpan.FromSeconds(2);

    private readonly Func<Uri, string, ObserverClient> connect;
    private readonly Func<string, string?> environment;
    private readonly string experimentsDirectory;
    private readonly string? settingsPath;
    private readonly TimeSpan pollInterval;
    private readonly TimeSpan streamRetryPause;

    private ObserverClient? client;
    private CancellationTokenSource? pumpCancel;
    private Task pump = Task.CompletedTask;
    private CancellationTokenSource? beatCancel;
    private Task beat = Task.CompletedTask;
    private int misses;
    // Живой сеанс, о котором наблюдатель сообщил последним: новый сеанс пульт выбирает сам, старый — не трогает.
    private string? seenSession;
    private string? exportedSession;
    private string? lastScenarioStatus;

    private string address;
    private string keyFile;
    private string status = "";
    private string outcome = "";
    private string version = "";
    private string scenarioText = "";
    private string showPath;
    private SessionRow? selectedSession;
    private bool connected;
    private bool keyRejected;
    private ObserverActivity? activity;
    private bool scenarioRunning;
    private bool busy;
    private string exchangeDirectory;
    private bool includeRaw;
    private SessionArtifact? selectedArtifact;
    private string instruction = "";
    private bool awaitingConfirm;
    private bool showAllFacts;
    private FactRow? selectedFact;
    private ExperimentRow? selectedExperiment;
    private Lamp linkLamp = new("Связь", "", LampTone.Off);
    private Lamp programLamp = new("ProShow", "", LampTone.Off);
    private Lamp runLamp = new("Прогон", "", LampTone.Off);
    private Lamp operatorLamp = new("Оператор", "", LampTone.Off);

    /// <param name="connect">Как создаётся клиент; тест подставляет свой.</param>
    /// <param name="environment">Откуда берутся значения по умолчанию для адреса и ключа; они главнее сохранённых.</param>
    /// <param name="experimentsDirectory">Каталог сценариев опытов; по умолчанию — <see cref="ExperimentsFolder"/> рядом с пультом.</param>
    /// <param name="settingsPath">Файл настроек пульта; <c>null</c> — пульт ничего не помнит, как в тестах.</param>
    /// <param name="pollInterval">Как часто спрашивать <c>/health</c>.</param>
    /// <param name="streamRetryPause">Пауза перед переподключением оборванного потока фактов.</param>
    public WorkbenchViewModel(Func<Uri, string, ObserverClient>? connect = null, Func<string, string?>? environment = null,
        string? experimentsDirectory = null, string? settingsPath = null, TimeSpan? pollInterval = null,
        TimeSpan? streamRetryPause = null)
    {
        this.connect = connect ?? ((uri, key) => new ObserverClient(uri, key));
        this.environment = environment ?? Environment.GetEnvironmentVariable;
        this.experimentsDirectory = experimentsDirectory ?? Path.Combine(AppContext.BaseDirectory, ExperimentsFolder);
        this.settingsPath = settingsPath;
        this.pollInterval = pollInterval ?? DefaultPollInterval;
        this.streamRetryPause = streamRetryPause ?? DefaultStreamRetryPause;

        var saved = settingsPath is null ? new WorkbenchSettings() : WorkbenchSettings.Load(settingsPath);
        address = this.environment(ObserverConnection.UrlVariable) ?? saved.Address ?? "";
        keyFile = this.environment(ObserverConnection.KeyFileVariable) ?? saved.KeyFile ?? "";
        exchangeDirectory = this.environment("PSDOCTOR_EXCHANGE_DIR") ?? saved.ExchangeDirectory ?? "";
        showPath = saved.ShowPath ?? "";
        foreach (var show in saved.RecentShows ?? [])
        {
            RecentShows.Add(show);
        }
        LoadExperiments();
        if (Experiments.FirstOrDefault(e => e.Name == saved.Experiment) is { } last)
        {
            selectedExperiment = last;
            try
            {
                scenarioText = File.ReadAllText(last.Path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                status = $"опыт {last.Name} не прочитан: {e.Message}";
            }
        }
        Dialogs.CollectionChanged += (_, _) => Refresh();
        Refresh();
    }

    public string Address
    {
        get => address;
        set
        {
            if (SetProperty(ref address, value)) Refresh();
        }
    }

    public string KeyFile
    {
        get => keyFile;
        set => SetProperty(ref keyFile, value);
    }

    public string ExchangeDirectory
    {
        get => exchangeDirectory;
        set
        {
            if (SetProperty(ref exchangeDirectory, value)) Refresh();
        }
    }

    public bool IncludeRaw
    {
        get => includeRaw;
        set => SetProperty(ref includeRaw, value);
    }

    /// <summary>Последнее, что случилось: отказ наблюдателя устойчивым именем, обрыв, принятый сценарий.</summary>
    public string Status
    {
        get => status;
        private set => SetProperty(ref status, value);
    }

    /// <summary>
    /// Чем кончился последний сценарий: устойчивое имя исхода, причина и строка шага. Отдельно от
    /// <see cref="Status"/>, потому что закрытие сеанса идёт следом и затёрло бы исход одной строкой.
    /// </summary>
    public string Outcome
    {
        get => outcome;
        private set => SetProperty(ref outcome, value);
    }

    /// <summary>Версия и коммит наблюдателя из <c>/health</c>: с чем именно разговариваем.</summary>
    public string Version
    {
        get => version;
        private set => SetProperty(ref version, value);
    }

    /// <summary>Наблюдатель ответил на последний опрос или пропустил меньше <see cref="MissesToLose"/> подряд.</summary>
    public bool Connected
    {
        get => connected;
        private set
        {
            if (SetProperty(ref connected, value)) Refresh();
        }
    }

    /// <summary>Что наблюдатель сообщил о себе последним опросом; у наблюдателя до Э4.6 — <c>null</c>.</summary>
    public ObserverActivity? Activity
    {
        get => activity;
        private set
        {
            if (SetProperty(ref activity, value)) Refresh();
        }
    }

    /// <summary>Последняя инструкция оператору из факта <c>operator-instruction</c> выбранного сеанса.</summary>
    public string Instruction
    {
        get => instruction;
        private set => SetProperty(ref instruction, value);
    }

    /// <summary>Сценарий стоит на <c>wait confirm</c>: начат и ещё не кончился.</summary>
    public bool AwaitingConfirm
    {
        get => awaitingConfirm;
        private set
        {
            if (SetProperty(ref awaitingConfirm, value)) Refresh();
        }
    }

    /// <summary>
    /// «Сделано» предлагается, только пока сценарий ждёт подтверждения: в другое время наблюдатель его примет и
    /// выбросит, и оператор решит, что подтвердил.
    /// </summary>
    public bool CanConfirm => Connected && AwaitingConfirm;

    /// <summary>Сценарий принят и ещё не кончился фактом <c>scenario-finished</c>.</summary>
    public bool ScenarioRunning
    {
        get => scenarioRunning;
        private set
        {
            if (SetProperty(ref scenarioRunning, value)) Refresh();
        }
    }

    /// <summary>Второй сценарий одновременно наблюдатель не принимает — кнопка не предлагает этого и пультом.</summary>
    public bool CanRun => Connected && !ScenarioRunning;

    public bool CanExport => Connected && !Busy && SelectedSession is { Finished: true } && ExchangeDirectory.Length > 0;

    public string ScenarioText
    {
        get => scenarioText;
        set
        {
            if (SetProperty(ref scenarioText, value)) Refresh();
        }
    }

    /// <summary>Путь к файлу шоу <b>на машине наблюдателя</b>: пульт его не проверяет и не открывает.</summary>
    public string ShowPath
    {
        get => showPath;
        set => SetProperty(ref showPath, value);
    }

    /// <summary>Последние файлы шоу, которыми запускали, — свежий первым.</summary>
    public ObservableCollection<string> RecentShows { get; } = [];

    /// <summary>Лента показывает все факты, а не только вехи. Переключение не ходит к наблюдателю: оба списка уже здесь.</summary>
    public bool ShowAllFacts
    {
        get => showAllFacts;
        set
        {
            if (SetProperty(ref showAllFacts, value)) OnPropertyChanged(nameof(Tape));
        }
    }

    /// <summary>То, что показывает вкладка «Журнал»: вехи или весь поток.</summary>
    public ObservableCollection<FactRow> Tape => ShowAllFacts ? Facts : Milestones;

    /// <summary>Факт, выбранный в ленте: его данные целиком видны в панели рядом.</summary>
    public FactRow? SelectedFact
    {
        get => selectedFact;
        set => SetProperty(ref selectedFact, value);
    }

    public ObservableCollection<SessionRow> Sessions { get; } = [];

    /// <summary>Весь поток фактов выбранного сеанса, последние <see cref="TapeLimit"/>.</summary>
    public ObservableCollection<FactRow> Facts { get; } = [];

    /// <summary>
    /// Вехи выбранного сеанса, <see cref="FactRow.IsMilestone"/>, со своим пределом: тысячи отсчётов за рендер не
    /// вытесняют их, как вытеснили бы из общего списка.
    /// </summary>
    public ObservableCollection<FactRow> Milestones { get; } = [];

    public ObservableCollection<StepRow> Steps { get; } = [];

    public ObservableCollection<DialogRow> Dialogs { get; } = [];

    public ObservableCollection<SessionArtifact> Artifacts { get; } = [];

    /// <summary>Сценарии опытов из каталога рядом с пультом, по имени файла.</summary>
    public ObservableCollection<ExperimentRow> Experiments { get; } = [];

    /// <summary>Опыт, чей сценарий лежит в поле; запоминается между запусками.</summary>
    public ExperimentRow? SelectedExperiment
    {
        get => selectedExperiment;
        private set => SetProperty(ref selectedExperiment, value);
    }

    /// <summary>Выбранный сеанс. Выбор переключает ленту: для этого есть <see cref="SelectAsync"/>.</summary>
    public SessionRow? SelectedSession
    {
        get => selectedSession;
        private set
        {
            if (SetProperty(ref selectedSession, value)) Refresh();
        }
    }

    public SessionArtifact? SelectedArtifact
    {
        get => selectedArtifact;
        set => SetProperty(ref selectedArtifact, value);
    }

    /// <summary>Идёт запрос к наблюдателю. Ленты не касается: она живёт своим потоком.</summary>
    public bool Busy
    {
        get => busy;
        private set
        {
            if (SetProperty(ref busy, value)) Refresh();
        }
    }

    /// <summary>Связь с наблюдателем.</summary>
    public Lamp LinkLamp
    {
        get => linkLamp;
        private set => SetProperty(ref linkLamp, value);
    }

    /// <summary>ProShow: не запущен, под наблюдением или запущен мимо наблюдателя.</summary>
    public Lamp ProgramLamp
    {
        get => programLamp;
        private set => SetProperty(ref programLamp, value);
    }

    /// <summary>Прогон сценария: нет, идёт с шагом, кончился с исходом.</summary>
    public Lamp RunLamp
    {
        get => runLamp;
        private set => SetProperty(ref runLamp, value);
    }

    /// <summary>Чего сценарий или программа ждут от человека.</summary>
    public Lamp OperatorLamp
    {
        get => operatorLamp;
        private set => SetProperty(ref operatorLamp, value);
    }

    /// <summary>Шаг, на котором стоит сценарий, — крупно в панели «Сейчас».</summary>
    public string CurrentStep => Steps.FirstOrDefault(s => s.IsCurrent)?.Text ?? "";

    /// <summary>Действие, которого обстановка ждёт первым: одна главная кнопка вместо поиска по окну.</summary>
    public PrimaryAction Primary =>
        !Connected ? PrimaryAction.Connect
        : CanConfirm ? PrimaryAction.Confirm
        : ScenarioRunning ? PrimaryAction.Cancel
        : CanExport && exportedSession != SelectedSession?.Id ? PrimaryAction.Export
        : ScenarioText.Trim().Length > 0 ? PrimaryAction.Run
        : PrimaryAction.None;

    public string PrimaryText => Primary switch
    {
        PrimaryAction.Connect => "Подключиться",
        PrimaryAction.Confirm => "Сделано",
        PrimaryAction.Cancel => "Отменить прогон",
        PrimaryAction.Export => "Собрать пакет",
        PrimaryAction.Run => SelectedExperiment is { } опыт ? $"Выполнить {опыт.Name}" : "Выполнить",
        _ => "Выберите опыт",
    };

    public bool CanPrimary => !Busy && Primary != PrimaryAction.None;

    /// <summary>Открывает окно: если адрес и ключ известны, подключается сам.</summary>
    public async Task StartAsync()
    {
        if (Address.Length > 0 && KeyFile.Length > 0)
        {
            await ConnectAsync();
        }
    }

    /// <summary>Главная кнопка панели «Сейчас».</summary>
    public Task ExecutePrimaryAsync() => Primary switch
    {
        PrimaryAction.Connect => ConnectAsync(),
        PrimaryAction.Confirm => ConfirmAsync(),
        PrimaryAction.Cancel => CancelAsync(),
        PrimaryAction.Export => ExportAsync(),
        PrimaryAction.Run => RunAsync(),
        _ => Task.CompletedTask,
    };

    /// <summary>
    /// Связывается с наблюдателем: читает ключ из файла, спрашивает <c>/health</c>, берёт сеансы и начинает опрос.
    /// Опрос идёт и тогда, когда наблюдатель пока не отвечает: поднимется — пульт увидит сам.
    /// </summary>
    public async Task ConnectAsync()
    {
        await StopBeatAsync();
        await StopPumpAsync();
        client?.Dispose();
        client = null;
        Connected = false;
        KeyRejected = false;
        Activity = null;
        Version = "";
        misses = 0;
        seenSession = null;
        Sessions.Clear();
        Facts.Clear();
        Milestones.Clear();
        Dialogs.Clear();
        Artifacts.Clear();
        SelectedArtifact = null;
        SelectedSession = null;

        if (!Uri.TryCreate(Address, UriKind.Absolute, out var uri))
        {
            Status = $"адрес не разобран: «{Address}»";
            return;
        }
        string key;
        try
        {
            key = (await File.ReadAllTextAsync(KeyFile)).Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Status = $"ключ {KeyFile} не прочитан: {e.Message}";
            return;
        }
        if (key.Length == 0)
        {
            Status = $"ключ {KeyFile} пуст";
            return;
        }

        client = connect(uri, key);
        Refresh();
        beatCancel = new CancellationTokenSource();
        await PollAsync(beatCancel.Token);
        beat = BeatAsync(beatCancel.Token);
    }

    /// <summary>Перечитывает список сеансов, сохраняя выбор.</summary>
    public Task RefreshSessionsAsync() => GuardAsync(LoadSessionsAsync);

    /// <summary>Переключает ленту на сеанс: журнал с начала, дальше поток новых фактов.</summary>
    public async Task SelectAsync(SessionRow? session)
    {
        await StopPumpAsync();
        SelectedSession = session;
        Facts.Clear();
        Milestones.Clear();
        SelectedFact = null;
        Dialogs.Clear();
        // Лента пойдёт с начала журнала и снова назначит инструкцию и ожидание подтверждения.
        Instruction = "";
        AwaitingConfirm = false;
        if (session is null || client is null)
        {
            return;
        }
        await RefreshDialogsAsync();
        await RefreshArtifactsAsync();
        pumpCancel = new CancellationTokenSource();
        pump = PumpAsync(session.Id, pumpCancel.Token);
    }

    /// <summary>Отдаёт наблюдателю текст из поля сценария.</summary>
    public Task RunAsync() => RunTextAsync(ScenarioText);

    public Task RunDiagnosticAsync(bool withStartupDialog)
    {
        RememberShow();
        ScenarioText = DiagnosticScenario(withStartupDialog);
        SelectedExperiment = null;
        return RunAsync();
    }

    public async Task ExportAsync()
    {
        if (client is null || SelectedSession is not { Finished: true } session)
        {
            Status = "для экспорта нужен закрытый сеанс";
            return;
        }
        if (ExchangeDirectory.Length == 0)
        {
            Status = "не указан каталог обмена";
            return;
        }
        Busy = true;
        try
        {
            SaveSettings();
            var path = await new LabPackageExporter().ExportAsync(client, session.Id, ScenarioText, ShowPath,
                ExchangeDirectory, SelectedArtifact, IncludeRaw);
            exportedSession = session.Id;
            Status = $"пакет сохранён: {path}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ObserverException)
        {
            Status = $"пакет не собран: {e.Message}";
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>Кнопка «Сделано»: оператор выполнил сказанное.</summary>
    public Task ConfirmAsync() => GuardAsync(async () =>
    {
        var accepted = await client!.ConfirmAsync();
        Status = $"подтверждение передано, сеанс {accepted.Session}";
    });

    /// <summary>Перечитывает каталог опытов. Нет каталога — пустой список, а не ошибка.</summary>
    public void LoadExperiments()
    {
        Experiments.Clear();
        if (!Directory.Exists(experimentsDirectory))
        {
            return;
        }
        foreach (var path in Directory.EnumerateFiles(experimentsDirectory, "*.txt").Order(StringComparer.Ordinal))
        {
            Experiments.Add(new ExperimentRow(Path.GetFileNameWithoutExtension(path), path));
        }
        // Список пересоздан: выбранный опыт — строка нового списка с тем же именем.
        SelectedExperiment = Experiments.FirstOrDefault(e => e.Name == SelectedExperiment?.Name);
    }

    /// <summary>Кладёт сценарий опыта в поле сценария; выполняет его, как любой другой текст, кнопка «Выполнить».</summary>
    public async Task OpenExperimentAsync(ExperimentRow? experiment)
    {
        if (experiment is null)
        {
            return;
        }
        try
        {
            ScenarioText = await File.ReadAllTextAsync(experiment.Path);
            SelectedExperiment = experiment;
            Status = $"опыт {experiment.Name} загружен";
            SaveSettings();
            Refresh();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Status = $"опыт {experiment.Name} не прочитан: {e.Message}";
        }
    }

    /// <summary>
    /// Пассивное подключение к ProShow, запущенному мимо наблюдателя. Программе потом даются только сценарии из
    /// <c>say</c> и <c>wait</c> — так опыт ведётся над программой, которую открыл оператор.
    /// </summary>
    public Task AttachAsync() => GuardAsync(async () =>
    {
        var accepted = await client!.AttachAsync();
        seenSession = accepted.Session;
        Status = $"подключён к ProShow, pid {accepted.ProcessId.ToString(CultureInfo.InvariantCulture)}, сеанс {accepted.Session}";
        await LoadSessionsAsync();
        await SelectAsync(Sessions.FirstOrDefault(s => s.Id == accepted.Session));
    });

    /// <summary>Быстрая кнопка «Запустить»: сценарий из одной строки.</summary>
    public Task LaunchAsync()
    {
        RememberShow();
        return RunTextAsync($"launch {Quote(ShowPath)}");
    }

    /// <summary>Быстрая кнопка «Закрыть программу»: то же, что крестик главного окна.</summary>
    public Task CloseProgramAsync() => RunTextAsync("close");

    /// <summary>Нажимает кнопку открытого диалога — тоже сценарием из одной строки, другого пути в API нет.</summary>
    public Task PressAsync(string button) => RunTextAsync($"press {Quote(button)}");

    /// <summary>Отменяет выполняемый сценарий. Программу не трогает.</summary>
    public Task CancelAsync() => GuardAsync(async () =>
    {
        var accepted = await client!.CancelAsync();
        Status = $"сценарий отменён, сеанс {accepted.Session}";
    });

    /// <summary>Прекращает наблюдение за выбранным сеансом. Программа остаётся жить.</summary>
    public Task StopAsync() => GuardAsync(async () =>
    {
        if (SelectedSession is not { } session)
        {
            Status = "сеанс не выбран";
            return;
        }
        await client!.StopAsync(session.Id);
        Status = $"наблюдение за {session.Id} прекращено";
        await LoadSessionsAsync();
    });

    public Task RefreshDialogsAsync() => GuardAsync(async () =>
    {
        if (SelectedSession is not { Active: true } session)
        {
            Dialogs.Clear();
            return;
        }
        var dialogs = await client!.DialogsAsync(session.Id);
        Dialogs.Clear();
        foreach (var dialog in dialogs)
        {
            Dialogs.Add(new DialogRow(dialog));
        }
    });

    public Task RefreshArtifactsAsync() => GuardAsync(async () =>
    {
        Artifacts.Clear();
        SelectedArtifact = null;
        if (SelectedSession is not { Finished: true } session) return;
        foreach (var artifact in await client!.ArtifactsAsync(session.Id)) Artifacts.Add(artifact);
        if (Artifacts.Count == 1) SelectedArtifact = Artifacts[0];
    });

    public void Dispose()
    {
        beatCancel?.Cancel();
        beatCancel?.Dispose();
        beatCancel = null;
        pumpCancel?.Cancel();
        pumpCancel?.Dispose();
        pumpCancel = null;
        client?.Dispose();
        client = null;
    }

    private bool KeyRejected
    {
        get => keyRejected;
        set
        {
            if (keyRejected != value)
            {
                keyRejected = value;
                Refresh();
            }
        }
    }

    /// <summary>Аргумент с пробелами сценарий берёт в кавычки — те же правила, что у разбора.</summary>
    private static string Quote(string value) => value.Contains(' ', StringComparison.Ordinal) ? $"\"{value}\"" : value;

    private string DiagnosticScenario(bool withStartupDialog)
    {
        var normalized = ShowPath.Replace('\\', '/');
        var name = Path.GetFileName(normalized);
        var lines = new List<string> { $"launch {Quote(ShowPath)}" };
        if (withStartupDialog)
        {
            lines.Add("wait dialog 180");
            lines.Add("press \"Ok to All\"");
        }
        lines.Add($"wait title {Quote(name)} 1800");
        lines.Add("render");
        lines.Add("wait render-done 7200");
        lines.Add("press \"Ok\"");
        lines.Add("close");
        lines.Add("wait exit 180");
        return string.Join(Environment.NewLine, lines);
    }

    private async Task RunTextAsync(string text)
    {
        if (client is null)
        {
            Status = "нет связи с наблюдателем";
            return;
        }

        // Разбор у пульта свой — чтобы показать шаги и не гонять заведомо негодный текст по сети.
        // Право отказать остаётся за наблюдателем: разбирают оба одним и тем же кодом ядра.
        var parsed = ScenarioParser.Parse(text);
        if (parsed.Scenario is null)
        {
            Steps.Clear();
            Status = "сценарий не разобран: " + string.Join("; ", parsed.Errors.Select(Describe));
            Refresh();
            return;
        }

        Steps.Clear();
        foreach (var step in parsed.Scenario.Steps)
        {
            Steps.Add(new StepRow(step.Line, step.Text));
        }
        Refresh();

        await GuardAsync(async () =>
        {
            var accepted = await client.RunAsync(text);
            // Сеанс известен раньше опроса: опрос не станет выбирать его второй раз.
            seenSession = accepted.Session;
            ScenarioRunning = true;
            Outcome = "";
            lastScenarioStatus = null;
            Status = $"сценарий принят, сеанс {accepted.Session}, факты после {accepted.After.ToString(CultureInfo.InvariantCulture)}";
            if (SelectedSession?.Id != accepted.Session)
            {
                await LoadSessionsAsync();
                await SelectAsync(Sessions.FirstOrDefault(s => s.Id == accepted.Session));
            }
        });
    }

    private static string Describe(ScenarioError error) =>
        $"строка {error.Line.ToString(CultureInfo.InvariantCulture)}: {error.Kind}" + (error.Token is null ? "" : $" «{error.Token}»");

    private async Task LoadSessionsAsync()
    {
        var sessions = await client!.SessionsAsync();
        var chosen = SelectedSession?.Id;
        Sessions.Clear();
        foreach (var session in sessions)
        {
            Sessions.Add(new SessionRow(session));
        }
        // Выбор — строка списка, и после перечитывания она другая: тот же сеанс, новая строка.
        SelectedSession = Sessions.FirstOrDefault(s => s.Id == chosen);
    }

    /// <summary>Опрос <c>/health</c>, пока пульт подключён: лампы и возвращение связи без кнопки.</summary>
    private async Task BeatAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(pollInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            await PollAsync(cancellationToken);
        }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        if (client is not { } current)
        {
            return;
        }
        ObserverHealth health;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(HealthTimeout);
            health = await current.HealthAsync(limit.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (ObserverException e) when (e.Status == HttpStatusCode.Unauthorized)
        {
            KeyRejected = true;
            Lose("наблюдатель не принял ключ (401)");
            return;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException or OperationCanceledException or ObserverException)
        {
            // До первого ответа ждать второго пропуска незачем: связи ещё не было.
            if (++misses >= MissesToLose || !Connected)
            {
                Lose(e is OperationCanceledException
                    ? "нет связи с наблюдателем: не ответил за " + HealthTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " с"
                    : $"нет связи с наблюдателем: {e.Message}");
            }
            return;
        }
        if (client != current)
        {
            return;
        }

        misses = 0;
        KeyRejected = false;
        Version = health.Commit is null ? health.Version : $"{health.Version} ({health.Commit})";
        Activity = health.Activity;
        var returned = !Connected;
        Connected = true;
        var live = health.Activity?.Session;
        if (returned)
        {
            Status = "наблюдатель отвечает";
            SaveSettings();
        }
        if (returned || live != seenSession)
        {
            var appeared = live is not null && live != seenSession;
            seenSession = live;
            await GuardAsync(LoadSessionsAsync);
            // Новый живой сеанс — то, что происходит сейчас: его и показываем. Первое подключение без сведений о
            // сеансе (наблюдатель до Э4.6) берёт живой сеанс из списка, как раньше.
            var target = appeared ? Sessions.FirstOrDefault(s => s.Id == live)
                : SelectedSession is null ? Sessions.FirstOrDefault(s => s.Active)
                : null;
            if (target is not null && target.Id != SelectedSession?.Id)
            {
                await SelectAsync(target);
            }
        }
    }

    private void Lose(string reason)
    {
        Connected = false;
        Activity = null;
        Status = reason;
    }

    /// <summary>
    /// Поток фактов сеанса: журнал с начала, потом новые. Обрыв — не конец сеанса: продолжаем с номера, сколько бы
    /// обрывов ни было, — рендер идёт часами. Конец сеанса — только факт <c>session-finished</c>: поток, закрытый
    /// остановкой наблюдателя, кончается без него.
    /// </summary>
    private async Task PumpAsync(string session, CancellationToken cancellationToken)
    {
        var after = 0L;
        var attempts = 0;
        var finished = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            string? reason;
            try
            {
                await foreach (var fact in client!.StreamAsync(session, after, null, cancellationToken))
                {
                    after = fact.Number;
                    attempts = 0;
                    finished = fact.Kind == ProgramFactKinds.SessionFinished;
                    Apply(fact);
                }
                // Оборванный сеанс — наблюдатель сняли посреди него — тоже кончается без session-finished, но он уже
                // не живой, и ждать его продолжения нечего.
                if (finished || await IsClosedAsync(session))
                {
                    Status = $"сеанс {session} закрыт";
                    ScenarioRunning = false;
                    await GuardAsync(LoadSessionsAsync);
                    await RefreshArtifactsAsync();
                    return;
                }
                reason = "поток кончился без конца сеанса";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                // Отмена не наша — предел ожидания заголовков у клиента: тот же обрыв.
                reason = "наблюдатель не ответил";
            }
            catch (ObserverException e) when (e.Error?.Error == ObserverErrors.UnknownSession)
            {
                Status = $"сеанс {session} наблюдателю неизвестен";
                return;
            }
            catch (Exception e) when (e is IOException or HttpRequestException or JsonException or ObserverException)
            {
                reason = e.Message;
            }
            attempts++;
            Status = $"поток оборван после {after.ToString(CultureInfo.InvariantCulture)}, "
                + $"переподключение {attempts.ToString(CultureInfo.InvariantCulture)}: {reason}";
            try
            {
                await Task.Delay(streamRetryPause, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Сеанс не живой по списку наблюдателя. Нет ответа — неизвестно, и поток пробуют снова.</summary>
    private async Task<bool> IsClosedAsync(string session)
    {
        try
        {
            var summary = (await client!.SessionsAsync()).FirstOrDefault(s => s.Id == session);
            return summary is not { Active: true };
        }
        catch (Exception e) when (e is IOException or HttpRequestException or JsonException or ObserverException or OperationCanceledException)
        {
            return false;
        }
    }

    private void Apply(Fact fact)
    {
        var row = new FactRow(fact);
        Facts.Add(row);
        while (Facts.Count > TapeLimit)
        {
            Facts.RemoveAt(0);
        }
        if (row.IsMilestone)
        {
            Milestones.Add(row);
            while (Milestones.Count > TapeLimit)
            {
                Milestones.RemoveAt(0);
            }
        }

        switch (fact.Kind)
        {
            case ScenarioFactKinds.ScenarioStarted:
                // Прогон идёт по журналу, а не по тому, кто его дал: сценарий агента пульт видит так же.
                ScenarioRunning = true;
                lastScenarioStatus = null;
                break;
            case ScenarioFactKinds.StepStarted:
                Mark(fact, StepState.Running, "");
                // Шаг узнаётся по его тексту тем же разбором: сценарий мог дать и не пульт, а агент.
                AwaitingConfirm = Text(fact, "step") is { } step
                    && ScenarioParser.Parse(step).Scenario?.Steps[0] is WaitConfirmStep;
                break;
            case ScenarioFactKinds.StepDone:
                Mark(fact, StepState.Done, Seconds(fact));
                AwaitingConfirm = false;
                break;
            case ScenarioFactKinds.StepFailed:
                Mark(fact, StepState.Failed, Text(fact, "reason") ?? "");
                AwaitingConfirm = false;
                break;
            case ScenarioFactKinds.OperatorInstruction:
                Instruction = Text(fact, "text") ?? "";
                break;
            case ScenarioFactKinds.ScenarioFinished:
                ScenarioRunning = false;
                AwaitingConfirm = false;
                var status = Text(fact, "status") ?? "";
                lastScenarioStatus = status;
                Outcome = $"сценарий: {status}"
                    + (Text(fact, "reason") is { } reason ? $", {reason}" : "")
                    + (Number(fact, "line") is { } line ? $", строка {line.ToString(CultureInfo.InvariantCulture)}" : "");
                Status = Outcome;
                // Отменённый шаг своего факта не получает: исполнитель выходит из него броском. Закрываем
                // его исходом сценария, иначе шаг так и остался бы в пульте начатым.
                foreach (var running in Steps.Where(шаг => шаг.State == StepState.Running))
                {
                    running.State = StepState.Failed;
                    running.Note = status;
                }
                Refresh();
                break;
            case ProgramFactKinds.DialogOpened:
            case ProgramFactKinds.DialogClosed:
            case ProgramFactKinds.DialogPressed:
            case ScenarioFactKinds.UnexpectedDialog:
                // Диалоги пульт не собирает из фактов, а спрашивает: в ответе они такие, какие сейчас на экране.
                _ = RefreshDialogsAsync();
                break;
            default:
                break;
        }
    }

    private void Mark(Fact fact, StepState state, string note)
    {
        if (Number(fact, "line") is { } line && Steps.FirstOrDefault(s => s.Line == line) is { } step)
        {
            step.State = state;
            step.Note = note;
            Refresh();
        }
    }

    /// <summary>
    /// Пересчитывает лампы и главную кнопку. Их входы — связь, состояние наблюдателя, шаги, диалоги — меняются в
    /// разных местах, а вывод у них один; одна функция вместо уведомлений из каждого сеттера.
    /// </summary>
    private void Refresh()
    {
        LinkLamp = client is null
            ? new Lamp("Связь", Address.Length == 0 ? "адрес не задан" : "не подключён", LampTone.Off)
            : KeyRejected ? new Lamp("Связь", "ключ не принят (401)", LampTone.Alarm)
            : Connected ? new Lamp("Связь", "на связи" + (Version.Length > 0 ? $" · {Version}" : ""), LampTone.On)
            : new Lamp("Связь", "нет связи", LampTone.Alarm);

        ProgramLamp = !Connected ? new Lamp("ProShow", "неизвестно", LampTone.Off)
            : Activity is not { } now ? new Lamp("ProShow", "наблюдатель не сообщает", LampTone.Off)
            : now.Program switch
            {
                ProgramStates.None => new Lamp("ProShow", "не запущен", LampTone.Off),
                ProgramStates.Launched => new Lamp("ProShow", "под наблюдением · запуск" + Pid(now), LampTone.On),
                ProgramStates.Attached => new Lamp("ProShow", "под наблюдением · подключение" + Pid(now), LampTone.On),
                ProgramStates.Unobserved => new Lamp("ProShow", "запущен мимо наблюдателя", LampTone.Attention),
                var other => new Lamp("ProShow", other, LampTone.Off),
            };

        var running = Steps.Select((s, i) => (s, i)).FirstOrDefault(p => p.s.IsCurrent);
        RunLamp = ScenarioRunning
            ? new Lamp("Прогон", running.s is null
                ? "идёт"
                : $"идёт · шаг {(running.i + 1).ToString(CultureInfo.InvariantCulture)} из {Steps.Count.ToString(CultureInfo.InvariantCulture)}",
                LampTone.On)
            // Сценарий в сеансе, которого лента не показывает: о нём знает только опрос.
            : Activity is { Scenario: true } elsewhere && elsewhere.Session != SelectedSession?.Id
                ? new Lamp("Прогон", "идёт · в другом сеансе", LampTone.On)
            : lastScenarioStatus is { } done ? new Lamp("Прогон", $"кончился · {done}",
                done == "completed" ? LampTone.Off : LampTone.Attention)
            : new Lamp("Прогон", "нет", LampTone.Off);

        OperatorLamp = CanConfirm ? new Lamp("Оператор", "ждём «Сделано»", LampTone.Attention)
            : Dialogs.Count > 0 ? new Lamp("Оператор", $"открыт диалог «{Dialogs[^1].Title}»", LampTone.Attention)
            : new Lamp("Оператор", "ничего не ждём", LampTone.Off);

        OnPropertyChanged(nameof(CanConfirm));
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(CurrentStep));
        OnPropertyChanged(nameof(Primary));
        OnPropertyChanged(nameof(PrimaryText));
        OnPropertyChanged(nameof(CanPrimary));
    }

    private static string Pid(ObserverActivity now) =>
        now.ProcessId is { } pid ? $" · pid {pid.ToString(CultureInfo.InvariantCulture)}" : "";

    private void RememberShow()
    {
        var path = ShowPath.Trim();
        if (path.Length == 0)
        {
            return;
        }
        var index = RecentShows.IndexOf(path);
        if (index != 0)
        {
            if (index > 0) RecentShows.RemoveAt(index);
            RecentShows.Insert(0, path);
            while (RecentShows.Count > WorkbenchSettings.RecentLimit) RecentShows.RemoveAt(RecentShows.Count - 1);
        }
        SaveSettings();
    }

    /// <summary>Записывает то, что пульт помнит. Не записалось — пульт работает дальше, сказав об этом.</summary>
    private void SaveSettings()
    {
        if (settingsPath is null)
        {
            return;
        }
        try
        {
            new WorkbenchSettings(Address, KeyFile, ExchangeDirectory, ShowPath, [.. RecentShows], SelectedExperiment?.Name)
                .Save(settingsPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Status = $"настройки пульта не сохранены: {e.Message}";
        }
    }

    private static string Seconds(Fact fact) =>
        fact.Data.TryGetProperty("seconds", out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble().ToString("0.###", CultureInfo.InvariantCulture) + " с"
            : "";

    private static int? Number(Fact fact, string name) =>
        fact.Data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;

    private static string? Text(Fact fact, string name) =>
        fact.Data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private async Task StopPumpAsync()
    {
        if (pumpCancel is null)
        {
            return;
        }
        await pumpCancel.CancelAsync();
        try
        {
            await pump;
        }
        catch (OperationCanceledException)
        {
        }
        pumpCancel.Dispose();
        pumpCancel = null;
        pump = Task.CompletedTask;
    }

    private async Task StopBeatAsync()
    {
        if (beatCancel is null)
        {
            return;
        }
        await beatCancel.CancelAsync();
        try
        {
            await beat;
        }
        catch (OperationCanceledException)
        {
        }
        beatCancel.Dispose();
        beatCancel = null;
        beat = Task.CompletedTask;
    }

    /// <summary>
    /// Один разговор с наблюдателем. Отказ показывается устойчивым именем, как пришёл: толковать его —
    /// не дело пульта, а угадывать фразу по имени — прямой путь к двум разным словарям.
    /// </summary>
    private async Task GuardAsync(Func<Task> work)
    {
        // Кнопки окна доступны и до подключения: без клиента говорить не с кем, а не падать.
        if (client is null)
        {
            Status = "нет связи с наблюдателем: нажмите «Подключиться»";
            return;
        }
        Busy = true;
        try
        {
            await work();
        }
        catch (ObserverException e) when (e.Status == HttpStatusCode.Unauthorized)
        {
            KeyRejected = true;
            Lose("наблюдатель не принял ключ (401)");
        }
        catch (ObserverException e)
        {
            Status = e.Error is null
                ? e.Message
                : $"отказ: {e.Error.Error}"
                    + (e.Error.Errors is { Count: > 0 } errors ? " — " + string.Join("; ", errors.Select(Describe)) : "");
        }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException)
        {
            // Лампу гасит не упавший запрос, а опрос: один сорвавшийся ответ — ещё не обрыв связи.
            Status = $"запрос не прошёл: {e.Message}";
        }
        finally
        {
            Busy = false;
        }
    }
}
