using PsDoctor.Core.Observation;
using PsDoctor.Core.Scenarios;

namespace PsDoctor.Observer;

/// <summary>
/// Действия сценария в сеансе. В Ш2 наблюдатель умеет только запускать: окна, <c>press</c> и <c>close</c> — Ш3,
/// <c>render</c> — когда найден способ. Неумение — срыв шага с причиной, а не молчаливый пропуск.
/// </summary>
public sealed class SessionActions(ObservationSession session, IProgramLauncher launcher) : IScenarioActions
{
    public Task<ActionResult> RunAsync(ActionStep step, CancellationToken cancellationToken) =>
        Task.FromResult(step switch
        {
            LaunchStep launch => session.Launch(launch.ShowPath, launcher),
            _ => ActionResult.Failed(StepFailures.Unsupported),
        });
}
