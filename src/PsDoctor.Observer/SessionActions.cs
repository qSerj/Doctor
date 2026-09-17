using PsDoctor.Core.Observation;
using PsDoctor.Core.Scenarios;

namespace PsDoctor.Observer;

/// <summary>
/// Действия сценария в сеансе: <c>launch</c>, <c>press</c>, <c>close</c>, <c>render</c>. Шаг, которого сеанс
/// не умеет, — срыв с причиной, а не молчаливый пропуск.
/// </summary>
public sealed class SessionActions(ObservationSession session, IProgramLauncher launcher) : IScenarioActions
{
    public Task<ActionResult> RunAsync(ActionStep step, CancellationToken cancellationToken) =>
        step switch
        {
            LaunchStep launch => Task.FromResult(session.Launch(launch.ShowPath, launcher)),
            PressStep press => session.PressAsync(press.Button, cancellationToken),
            CloseStep => Task.FromResult(session.Close()),
            RenderStep => session.RenderAsync(cancellationToken),
            _ => Task.FromResult(ActionResult.Failed(StepFailures.Unsupported)),
        };
}
