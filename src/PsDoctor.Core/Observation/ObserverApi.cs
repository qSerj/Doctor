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
    public const string ProgramNotRunning = "program-not-running";
    public const string AmbiguousProgram = "ambiguous-program";
    public const string AttachFailed = "attach-failed";
    public const string PassiveSession = "passive-session";
    public const string EtwUnavailable = "etw-unavailable";

    /// <summary>Запрос не разобран: нет тела, не JSON, кривой номер.</summary>
    public const string BadRequest = "bad-request";
}

public sealed record RunScenarioRequest(string Text);

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

/// <summary>Ответ <c>/health</c>.</summary>
public sealed record ObserverHealth(string Version, string? Commit);
