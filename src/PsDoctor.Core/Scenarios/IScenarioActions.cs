namespace PsDoctor.Core.Scenarios;

/// <summary>
/// Исполнение действий. Реализация под ProShow живёт в <c>Infrastructure</c>; ядро знает только
/// имя действия и итог. Факты о самой программе действие пишет само, мимо исполнителя.
/// </summary>
public interface IScenarioActions
{
    /// <remarks>Отмена приходит, когда истёк таймаут действия или отменён сценарий.</remarks>
    Task<ActionResult> RunAsync(ActionStep step, CancellationToken cancellationToken);
}

/// <param name="Reason">Устойчивое имя причины срыва, не фраза; у выполненного — <c>null</c>.</param>
public sealed record ActionResult(bool Succeeded, string? Reason)
{
    public static ActionResult Done { get; } = new(true, null);

    public static ActionResult Failed(string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        return new ActionResult(false, reason);
    }
}
