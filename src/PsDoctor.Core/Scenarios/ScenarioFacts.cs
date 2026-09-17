namespace PsDoctor.Core.Scenarios;

/// <summary>Виды фактов, которые пишет исполнитель сценария.</summary>
public static class ScenarioFactKinds
{
    public const string ScenarioStarted = "scenario-started";
    public const string StepStarted = "step-started";
    public const string StepDone = "step-done";
    public const string StepFailed = "step-failed";

    /// <summary>Диалог, которого сценарий не ждал: с текстом и кнопками, ничего не нажато.</summary>
    public const string UnexpectedDialog = "unexpected-dialog";

    public const string ScenarioFinished = "scenario-finished";
}

/// <summary>Причины срыва шага, которые назначает сам исполнитель. Причины действий назначают действия.</summary>
public static class StepFailures
{
    public const string Timeout = "timeout";
    public const string ProgramExited = "program-exited";

    /// <summary>Поток сигналов закончился, а ожидание не выполнено: дождаться уже нечего.</summary>
    public const string SignalsEnded = "signals-ended";

    /// <summary>Действие бросило исключение. Тип исключения — в факте.</summary>
    public const string Exception = "exception";

    /// <summary>Шаг объявлен, но способ его выполнить не найден.</summary>
    public const string Unsupported = "unsupported";

    public const string UnexpectedDialog = "unexpected-dialog";
}

public enum ScenarioStatus
{
    Completed,

    /// <summary>Шаг сорвался.</summary>
    Failed,

    /// <summary>Встал неожиданный диалог.</summary>
    Stopped,

    Cancelled,
}

/// <param name="Line">Строка шага, на котором сценарий кончился не выполнением.</param>
public sealed record ScenarioOutcome(ScenarioStatus Status, int? Line, string? Reason);
