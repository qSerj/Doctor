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
/// Пульт целиком: связь с наблюдателем, сеансы, лента фактов, шаги сценария и диалоги. Avalonia сюда не
/// заходит — окно только показывает это и зовёт методы, поэтому пульт проверяется тестами без интерфейса.
/// </summary>
/// <remarks>
/// <para><b>Один поток.</b> Все методы вызываются с потока интерфейса и <c>ConfigureAwait(false)</c> не
/// делают нарочно: продолжения возвращаются туда же, и списки меняются только там. Иначе пришлось бы
/// заводить диспетчер, а с ним — вторую истину о том, где живёт состояние.</para>
/// <para><b>Пульт ничего не толкует.</b> Вид факта, имя отказа, причина срыва показываются как пришли:
/// вердикты — не его дело, а слова для Ольги живут в окне оператора, а не здесь.</para>
/// </remarks>
public sealed class WorkbenchViewModel : ObservableObject, IDisposable
{
    /// <summary>Сколько последних фактов держит лента. За рендер их тысячи, и держать всё пульту незачем.</summary>
    public const int TapeLimit = 2000;

    /// <summary>Каталог сценариев опытов рядом с пультом: файл <c>*.txt</c> на опыт.</summary>
    public const string ExperimentsFolder = "Experiments";

    /// <summary>Сколько раз пульт переподключается к оборванному потоку, прежде чем бросить.</summary>
    public const int StreamRetries = 5;

    private static readonly TimeSpan StreamRetryPause = TimeSpan.FromSeconds(2);

    private readonly Func<Uri, string, ObserverClient> connect;
    private readonly Func<string, string?> environment;
    private readonly string experimentsDirectory;

    private ObserverClient? client;
    private CancellationTokenSource? pumpCancel;
    private Task pump = Task.CompletedTask;

    private string address;
    private string keyFile;
    private string status = "";
    private string outcome = "";
    private string version = "";
    private string scenarioText = "";
    private string showPath = "";
    private string kindFilter = "";
    private SessionRow? selectedSession;
    private bool connected;
    private bool scenarioRunning;
    private bool busy;
    private string exchangeDirectory;
    private bool includeRaw;
    private SessionArtifact? selectedArtifact;
    private string instruction = "";
    private bool awaitingConfirm;

    /// <param name="connect">Как создаётся клиент; тест подставляет свой.</param>
    /// <param name="environment">Откуда берутся значения по умолчанию для адреса и ключа.</param>
    /// <param name="experimentsDirectory">Каталог сценариев опытов; по умолчанию — <see cref="ExperimentsFolder"/> рядом с пультом.</param>
    public WorkbenchViewModel(Func<Uri, string, ObserverClient>? connect = null, Func<string, string?>? environment = null,
        string? experimentsDirectory = null)
    {
        this.connect = connect ?? ((uri, key) => new ObserverClient(uri, key));
        this.environment = environment ?? Environment.GetEnvironmentVariable;
        this.experimentsDirectory = experimentsDirectory ?? Path.Combine(AppContext.BaseDirectory, ExperimentsFolder);
        LoadExperiments();
        address = this.environment(ObserverConnection.UrlVariable) ?? "";
        keyFile = this.environment(ObserverConnection.KeyFileVariable) ?? "";
        exchangeDirectory = this.environment("PSDOCTOR_EXCHANGE_DIR") ?? WorkbenchSettings.LoadExchangeDirectory() ?? "";
    }

    public string Address
    {
        get => address;
        set => SetProperty(ref address, value);
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
            if (SetProperty(ref exchangeDirectory, value)) OnPropertyChanged(nameof(CanExport));
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

    public bool Connected
    {
        get => connected;
        private set
        {
            if (SetProperty(ref connected, value))
            {
                OnPropertyChanged(nameof(CanRun));
                OnPropertyChanged(nameof(CanExport));
                OnPropertyChanged(nameof(CanConfirm));
            }
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
            if (SetProperty(ref awaitingConfirm, value))
            {
                OnPropertyChanged(nameof(CanConfirm));
            }
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
            if (SetProperty(ref scenarioRunning, value))
            {
                OnPropertyChanged(nameof(CanRun));
            }
        }
    }

    /// <summary>Второй сценарий одновременно наблюдатель не принимает — кнопка не предлагает этого и пультом.</summary>
    public bool CanRun => Connected && !ScenarioRunning;

    public bool CanExport => Connected && !Busy && SelectedSession is { Finished: true } && ExchangeDirectory.Length > 0;

    public string ScenarioText
    {
        get => scenarioText;
        set => SetProperty(ref scenarioText, value);
    }

    /// <summary>Путь к файлу шоу <b>в гостевой системе стенда</b>: пульт его не проверяет и не открывает.</summary>
    public string ShowPath
    {
        get => showPath;
        set => SetProperty(ref showPath, value);
    }

    /// <summary>Виды фактов через запятую; пусто — все. Фильтрует наблюдатель, пульт только передаёт.</summary>
    public string KindFilter
    {
        get => kindFilter;
        set => SetProperty(ref kindFilter, value);
    }

    public ObservableCollection<SessionRow> Sessions { get; } = [];

    public ObservableCollection<FactRow> Facts { get; } = [];

    public ObservableCollection<StepRow> Steps { get; } = [];

    public ObservableCollection<DialogRow> Dialogs { get; } = [];

    public ObservableCollection<SessionArtifact> Artifacts { get; } = [];

    /// <summary>Сценарии опытов из каталога рядом с пультом, по имени файла.</summary>
    public ObservableCollection<ExperimentRow> Experiments { get; } = [];

    /// <summary>Выбранный сеанс. Выбор переключает ленту: для этого есть <see cref="SelectAsync"/>.</summary>
    public SessionRow? SelectedSession
    {
        get => selectedSession;
        private set
        {
            if (SetProperty(ref selectedSession, value))
            {
                OnPropertyChanged(nameof(CanExport));
            }
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
            if (SetProperty(ref busy, value)) OnPropertyChanged(nameof(CanExport));
        }
    }

    /// <summary>Связывается с наблюдателем: читает ключ из файла, спрашивает <c>/health</c>, берёт сеансы.</summary>
    public async Task ConnectAsync()
    {
        await StopPumpAsync();
        client?.Dispose();
        client = null;
        Connected = false;
        Version = "";
        Sessions.Clear();
        Facts.Clear();
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
        await GuardAsync(async () =>
        {
            var health = await client.HealthAsync();
            Version = health.Commit is null ? health.Version : $"{health.Version} ({health.Commit})";
            Connected = true;
            Status = "наблюдатель отвечает";
            await LoadSessionsAsync();
            if (Sessions.FirstOrDefault(s => s.Active) is { } живой)
            {
                await SelectAsync(живой);
            }
        });
    }

    /// <summary>Перечитывает список сеансов, сохраняя выбор.</summary>
    public Task RefreshSessionsAsync() => GuardAsync(LoadSessionsAsync);

    /// <summary>Переключает ленту на сеанс: журнал с начала, дальше поток новых фактов.</summary>
    public async Task SelectAsync(SessionRow? session)
    {
        await StopPumpAsync();
        SelectedSession = session;
        Facts.Clear();
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
        ScenarioText = DiagnosticScenario(withStartupDialog);
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
            WorkbenchSettings.SaveExchangeDirectory(ExchangeDirectory);
            var path = await new LabPackageExporter().ExportAsync(client, session.Id, ScenarioText, ShowPath,
                ExchangeDirectory, SelectedArtifact, IncludeRaw);
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
            Status = $"опыт {experiment.Name} загружен";
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
        Status = $"подключён к ProShow, pid {accepted.ProcessId.ToString(CultureInfo.InvariantCulture)}, сеанс {accepted.Session}";
        await LoadSessionsAsync();
        await SelectAsync(Sessions.FirstOrDefault(s => s.Id == accepted.Session));
    });

    /// <summary>Быстрая кнопка «Запустить»: сценарий из одной строки.</summary>
    public Task LaunchAsync() => RunTextAsync($"launch {Quote(ShowPath)}");

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
        pumpCancel?.Cancel();
        pumpCancel?.Dispose();
        pumpCancel = null;
        client?.Dispose();
        client = null;
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
            return;
        }

        Steps.Clear();
        foreach (var step in parsed.Scenario.Steps)
        {
            Steps.Add(new StepRow(step.Line, step.Text));
        }

        await GuardAsync(async () =>
        {
            var accepted = await client.RunAsync(text);
            ScenarioRunning = true;
            Outcome = "";
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

    /// <summary>Поток фактов сеанса: журнал с начала, потом новые. Обрыв — не конец сеанса, продолжаем с номера.</summary>
    private async Task PumpAsync(string session, CancellationToken cancellationToken)
    {
        var after = 0L;
        var attempts = 0;
        IReadOnlyCollection<string>? kinds = KindFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: > 0 } list
            ? list
            : null;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var fact in client!.StreamAsync(session, after, kinds, cancellationToken))
                {
                    after = fact.Number;
                    attempts = 0;
                    Apply(fact);
                }
                Status = $"сеанс {session} закрыт";
                ScenarioRunning = false;
                await GuardAsync(LoadSessionsAsync);
                await RefreshArtifactsAsync();
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e) when (e is IOException or HttpRequestException or JsonException)
            {
                if (++attempts > StreamRetries)
                {
                    Status = $"поток фактов оборван после {after.ToString(CultureInfo.InvariantCulture)} и не восстановлен: {e.Message}";
                    return;
                }
                Status = $"поток оборван после {after.ToString(CultureInfo.InvariantCulture)}, "
                    + $"попытка {attempts.ToString(CultureInfo.InvariantCulture)} из {StreamRetries.ToString(CultureInfo.InvariantCulture)}";
                try
                {
                    await Task.Delay(StreamRetryPause, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private void Apply(Fact fact)
    {
        Facts.Add(new FactRow(fact));
        while (Facts.Count > TapeLimit)
        {
            Facts.RemoveAt(0);
        }

        switch (fact.Kind)
        {
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
            Status = "наблюдатель не принял ключ (401)";
            Connected = false;
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
            Status = $"нет связи с наблюдателем: {e.Message}";
            Connected = false;
        }
        finally
        {
            Busy = false;
        }
    }
}
