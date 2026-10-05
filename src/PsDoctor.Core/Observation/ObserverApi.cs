using PsDoctor.Core.Scenarios;

namespace PsDoctor.Core.Observation;

/// <summary>
/// Маршруты наблюдателя — общие для сервера и клиента, чтобы строка пути жила в одном месте.
/// </summary>
public static class ObserverRoutes
{
    public const string Health = "/health";

    /// <summary>POST <see cref="RunScenarioRequest"/>: выполнить сценарий.</summary>
    public const string Scenarios = "/scenarios";

    /// <summary>POST: отменить выполняемый сценарий. Программу не трогает.</summary>
    public const string CancelScenario = "/scenarios/cancel";

    /// <summary>
    /// POST: оператор сделал сказанное — кнопка «Сделано». Засчитывается начатому шагу <c>wait confirm</c>;
    /// в другое время принимается и пропадает, фактом не пишется.
    /// </summary>
    public const string ConfirmScenario = "/scenarios/confirm";

    /// <summary>GET: список сеансов, <see cref="SessionSummary"/>.</summary>
    public const string Sessions = "/sessions";

    /// <summary>POST: начать пассивное наблюдение за уже работающим ProShow.</summary>
    public const string Attach = "/sessions/attach";

    /// <summary>GET <c>?after=N</c>: журнал сеанса JSON Lines, сколько есть на момент запроса.</summary>
    public static string Facts(string session) => $"/sessions/{Uri.EscapeDataString(session)}/facts";

    /// <summary>
    /// GET <c>?after=N&amp;kind=a,b</c>: поток Server-Sent Events. Событие <c>fact</c> с номером факта в <c>id</c>;
    /// событие <c>end</c> — сеанс закрыт и всё отдано. Заголовок <c>Last-Event-ID</c> работает как <c>after</c>.
    /// </summary>
    public static string Stream(string session) => $"/sessions/{Uri.EscapeDataString(session)}/stream";

    /// <summary>GET: открытые диалоги живого сеанса с текстами и кнопками, <see cref="Scenarios.DialogInfo"/>.</summary>
    public static string Dialogs(string session) => $"/sessions/{Uri.EscapeDataString(session)}/dialogs";

    /// <summary>POST: прекратить наблюдение. Программу не закрывает.</summary>
    public static string Stop(string session) => $"/sessions/{Uri.EscapeDataString(session)}/stop";

    /// <summary>GET <c>?from=&amp;to=</c>: сырьё сеанса за отрезок. До Э4.2 сырья нет — ответ <see cref="ObserverErrors.NoRaw"/>.</summary>
    public static string Raw(string session) => $"/sessions/{Uri.EscapeDataString(session)}/raw";

    /// <summary>GET: все доступные ETW-события сеанса.</summary>
    public static string RawAll(string session) => $"/sessions/{Uri.EscapeDataString(session)}/raw/all";

    /// <summary>GET: MP4, изменившиеся в результате рендера сеанса.</summary>
    public static string Artifacts(string session) => $"/sessions/{Uri.EscapeDataString(session)}/artifacts";

    /// <summary>GET: скачать один артефакт по непрозрачному идентификатору.</summary>
    public static string Artifact(string session, string id) =>
        $"/sessions/{Uri.EscapeDataString(session)}/artifacts/{Uri.EscapeDataString(id)}";

    /// <summary>
    /// POST <see cref="IncidentRequest"/>: метка инцидента — монтажёр сказал «плохо» вот сейчас. GET: все метки,
    /// <see cref="IncidentRecord"/>, в порядке записи.
    /// </summary>
    public const string Incidents = "/incidents";

    /// <summary>
    /// GET: события журнала Windows о кусте ProShow, <see cref="WindowsEvent"/>, в порядке находки — из файла событий
    /// наблюдателя, начиная с ротированного.
    /// </summary>
    public const string WindowsEvents = "/windows-events";

    /// <summary>
    /// GET: слепок окружения машины наблюдателя (Э6.3), снятый сейчас; он же сохраняется рядом с журналами.
    /// Не умеет снимать — <see cref="ObserverErrors.NoEnvironment"/>.
    /// </summary>
    public const string Environment = "/environment";

    /// <summary>GET: сохранённый слепок по идентификатору из факта <c>environment</c>; незнакомый — <see cref="ObserverErrors.NoEnvironment"/>.</summary>
    public static string StoredEnvironment(string id) => $"/environment/{Uri.EscapeDataString(id)}";

    /// <summary>
    /// GET: сохранённые сводки дней (Э6.6), <see cref="DailySummary"/>, по порядку дней; <c>?from=ГГГГ-ММ-ДД</c> — с этого
    /// дня. Сегодняшней среди них нет: она неполная и строится по запросу <see cref="Summary"/>.
    /// </summary>
    public const string Summaries = "/summaries";

    /// <summary>Слово вместо даты: сводка сегодняшнего дня, построенная сейчас.</summary>
    public const string Today = "today";

    /// <summary>
    /// GET: сводка одного дня — <c>ГГГГ-ММ-ДД</c> или <see cref="Today"/>. Сегодняшняя строится сейчас и не сохраняется;
    /// прошлый день без сохранённой сводки — <see cref="ObserverErrors.NoSummary"/>.
    /// </summary>
    public static string Summary(string day) => $"/summaries/{Uri.EscapeDataString(day)}";

    /// <summary>Дата в маршрутах сводок.</summary>
    public const string DayFormat = "yyyy-MM-dd";
}

/// <summary>Устойчивые имена отказов API, не фразы.</summary>
public static class ObserverErrors
{
    /// <summary>Текст сценария не разобран; строки и виды ошибок — в <see cref="ObserverError.Errors"/>.</summary>
    public const string BadScenario = "bad-scenario";

    /// <summary>Программа уже запущена — наблюдателем или мимо него.</summary>
    public const string ProgramRunning = "program-running";

    /// <summary>Сценарий уже выполняется: второй одновременно не принимается.</summary>
    public const string ScenarioRunning = "scenario-running";

    /// <summary>Сценарий начинается не с <c>launch</c>, а живого сеанса нет.</summary>
    public const string NoSession = "no-session";

    public const string UnknownSession = "unknown-session";

    /// <summary>Сеанс уже закрыт.</summary>
    public const string SessionFinished = "session-finished";

    public const string NothingRunning = "nothing-running";

    public const string NoRaw = "no-raw";

    /// <summary>Слепка окружения нет: наблюдатель не умеет его снимать или такой идентификатор ему незнаком.</summary>
    public const string NoEnvironment = "no-environment";

    /// <summary>Сводки этого дня нет: он старше построенных или ещё не кончился достаточно давно.</summary>
    public const string NoSummary = "no-summary";
    public const string NoArtifacts = "no-artifacts";
    public const string ProgramNotRunning = "program-not-running";
    public const string AmbiguousProgram = "ambiguous-program";
    public const string AttachFailed = "attach-failed";
    /// <summary>Сценарий трогает программу, а сеанс подключён пассивно: принимаются только <c>say</c> и <c>wait</c>.</summary>
    public const string PassiveSession = "passive-session";
    public const string EtwUnavailable = "etw-unavailable";

    /// <summary>Запрос не разобран: нет тела, не JSON, кривой номер.</summary>
    public const string BadRequest = "bad-request";

    /// <summary>Дежурство не подключается к программе, наблюдение за которой прекращено явным <c>stop</c>, пока она жива.</summary>
    public const string StoppedProgram = "stopped-program";

    /// <summary>Метка не легла никуда: ни в живой сеанс, ни в файл меток.</summary>
    public const string IncidentNotStored = "incident-not-stored";
}

/// <param name="Origin">Кто начал сеанс, <see cref="SessionOrigins"/>; действует, только если сценарий открывает сеанс.</param>
public sealed record RunScenarioRequest(string Text, string? Origin = null);

/// <summary>Тело POST <see cref="ObserverRoutes.Attach"/>; тела может не быть вовсе.</summary>
public sealed record AttachRequest(string? Origin = null);

/// <summary>
/// Кто начал сеанс. Пишется в факт <c>session-started</c> полем <c>origin</c>; сеанс мастера хранится дольше
/// обычного. Незнакомое значение наблюдатель не отвергает, а пишет как есть: это метка, а не команда.
/// </summary>
public static class SessionOrigins
{
    /// <summary>Мастер App «Решить проблему».</summary>
    public const string Wizard = "wizard";

    /// <summary>Дежурство наблюдателя: увидел ProShow — подключился, монтажёру ничего не показывает.</summary>
    public const string Watch = "watch";
}

/// <summary>Тело POST <see cref="ObserverRoutes.Incidents"/>.</summary>
/// <param name="Source">Откуда метка, <see cref="IncidentSources"/>; незнакомое значение пишется как есть.</param>
/// <param name="Note">Пояснение, например что Doctor по кнопке монтажёра завершил ProShow.</param>
public sealed record IncidentRequest(string Source, string? Note = null)
{
    public const int MaxSource = 64;
    public const int MaxNote = 1000;
}

/// <summary>Кто поставил метку инцидента.</summary>
public static class IncidentSources
{
    /// <summary>Кнопка «Решить проблему» в App.</summary>
    public const string Wizard = "wizard";

    /// <summary><c>psdoctor observe incident</c>.</summary>
    public const string Cli = "cli";
}

/// <summary>
/// Метка инцидента: ответ POST и строка файла меток. Вердикта в ней нет — только когда нажали и что в этот миг
/// видел наблюдатель.
/// </summary>
/// <param name="Session">Живой сеанс, в который лёг факт <c>incident</c>; <c>null</c> — сеанса не было или он закрывался.</param>
/// <param name="Number">Номер факта <c>incident</c> в <paramref name="Session"/>.</param>
/// <param name="Recent">
/// Последний сеанс, кончившийся не раньше чем за 30 мин до метки, в том числе оборванный: метка после падения
/// ProShow или перезагрузки относится к нему.
/// </param>
/// <param name="Program">Состояние программы, <see cref="ProgramStates"/>.</param>
/// <param name="ProcessId">Процесс программы под наблюдением.</param>
public sealed record IncidentRecord(DateTime AtUtc, string Source, string? Note, string? Session, long? Number,
    string? Recent, string Program, int? ProcessId);

/// <param name="After">Номер последнего факта сеанса до начала сценария: факты сценария идут после него.</param>
public sealed record RunScenarioAccepted(string Session, long After);

public sealed record AttachAccepted(string Session, int ProcessId, DateTime StartedUtc);

public sealed record ObserverError(string Error, IReadOnlyList<ScenarioError>? Errors = null);

/// <param name="LastNumber">Номер последнего факта; у закрытого сеанса с диска — <c>null</c>, журнал не перечитывается.</param>
/// <param name="Finished">
/// Журнал кончается фактом <c>session-finished</c>: сеанс доведён до конца. У оборванного — <c>false</c>:
/// наблюдатель сняли посреди сеанса, и последняя строка какая угодно, вплоть до недописанной.
/// </param>
public sealed record SessionSummary(string Id, bool Active, long? LastNumber, bool Finished = false);

public sealed record CancelAccepted(string Session);

/// <summary>Подтверждение передано выполняемому сценарию сеанса.</summary>
public sealed record ConfirmAccepted(string Session);

/// <summary>Ответ <c>/health</c>.</summary>
/// <param name="Activity">Что наблюдатель делает сейчас; у наблюдателя до Э4.6 — <c>null</c>.</param>
/// <param name="Watch">Дежурство; у наблюдателя до Э6.2 — <c>null</c>.</param>
/// <param name="WindowsEvents">Опрос журнала Windows; <c>null</c> — журнала читать нечем (не Windows) или наблюдатель до Э6.2.</param>
/// <param name="ProgramFile">ProShow, за которым следит наблюдатель; <c>null</c> — вне Windows или наблюдатель до Э6.2.</param>
public sealed record ObserverHealth(string Version, string? Commit, ObserverActivity? Activity = null, WatchStatus? Watch = null,
    WindowsEventsStatus? WindowsEvents = null, ProgramFile? ProgramFile = null);

/// <summary>
/// ProShow в ответе <c>/health</c>: путь и есть ли файл в момент запроса. В первый вечер у монтажёра путь проверяется
/// одним запросом, без чтения файлов на его машине.
/// </summary>
public sealed record ProgramFile(string Path, bool Exists);

/// <summary>Дежурство в ответе <c>/health</c>: включено ли и чем кончилось последнее неудачное подключение.</summary>
/// <param name="LastRefusal">Имя последнего отказа подключения, <see cref="ObserverErrors"/>; удачное подключение его не стирает.</param>
public sealed record WatchStatus(bool Enabled, string? LastRefusal = null, DateTime? LastRefusalUtc = null);

/// <summary>
/// Опрос журналов Windows в ответе <c>/health</c>: без него «событий нет» на машине монтажёра не отличить от «журнал не
/// читается».
/// </summary>
/// <param name="LastReadUtc">Последний опрос, прочитавший хоть один журнал; <c>null</c> — ещё ни одного.</param>
/// <param name="LastError">
/// Чем сорвался последний опрос: имя исключения у Application, «журнал: имя» у остальных, через «; »; опрос без сбоев
/// его стирает.
/// </param>
public sealed record WindowsEventsStatus(DateTime? LastReadUtc, string? LastError = null);

/// <summary>
/// Состояние наблюдателя в ответе <c>/health</c>: по нему пульт зажигает лампы, не читая журнал.
/// </summary>
/// <param name="Program">Программа, <see cref="ProgramStates"/>.</param>
/// <param name="ProcessId">Процесс программы под наблюдением; у запущенной мимо наблюдателя — <c>null</c>.</param>
/// <param name="Session">Живой сеанс, если есть.</param>
/// <param name="Scenario">Выполняется сценарий.</param>
public sealed record ObserverActivity(string Program, int? ProcessId, string? Session, bool Scenario);

/// <summary>Устойчивые имена состояния программы в <see cref="ObserverActivity"/>.</summary>
public static class ProgramStates
{
    /// <summary>Программа не запущена.</summary>
    public const string None = "none";

    /// <summary>Под наблюдением сеанса, начатого запуском.</summary>
    public const string Launched = "launched";

    /// <summary>Под наблюдением сеанса, начатого подключением к уже работающей программе.</summary>
    public const string Attached = "attached";

    /// <summary>Запущена мимо наблюдателя: живого сеанса нет, а программа есть — <c>launch</c> получит <c>program-running</c>.</summary>
    public const string Unobserved = "unobserved";
}

/// <summary>Результат рендера, зарегистрированный наблюдателем после закрытия программы.</summary>
public sealed record SessionArtifact(string Id, string Name, long Bytes, DateTime LastWriteUtc, string Sha256);
