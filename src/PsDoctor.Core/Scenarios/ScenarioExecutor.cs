using System.Threading.Channels;
using PsDoctor.Core.Observation;

namespace PsDoctor.Core.Scenarios;

/// <summary>
/// Исполнитель сценария: шаги по порядку, остановка на первом сорвавшемся шаге и на неожиданном диалоге.
/// Каждый шаг — факты сеанса: начат, выполнен или сорвался, за сколько секунд.
/// </summary>
/// <remarks>
/// <para>
/// Правило неожиданного диалога, решение владельца 17.09.2026: диалог, появившийся после конца прошлого
/// шага или во время текущего, ждёт следующего шага. Если следующий шаг — <c>wait dialog</c>, диалог
/// засчитывается ему сразу, иначе это неожиданный диалог, и ничего не нажимается наугад. Поэтому диалог
/// посреди <c>launch</c> ждёт следующего шага, а диалог посреди любого ожидания, кроме <c>wait dialog</c>,
/// останавливает сценарий сразу.
/// </para>
/// <para>
/// Часов у исполнителя нет: время приходит в сигналах, и по нему считаются таймауты шагов и их длительность.
/// Единственное исключение — предел времени действия: он поставлен таймером на отмену действия, потому что
/// сигналы кончаются вместе с программой, а ожидание итога действия должно сорваться и после этого.
/// </para>
/// </remarks>
public sealed class ScenarioExecutor
{
    /// <summary>Сколько ждать итога действия. У ожиданий таймаут записан в сценарии.</summary>
    public static readonly TimeSpan DefaultActionTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Сколько ждать итога <c>render</c>: он проходит три окна подряд, и первое из них программа показывает
    /// после похода в сеть за списком форматов. Само время рендера сюда не входит — это <c>wait render-done</c>.
    /// </summary>
    public static readonly TimeSpan RenderActionTimeout = TimeSpan.FromMinutes(5);

    private readonly IScenarioActions actions;
    private readonly IFactRecorder facts;
    private readonly TimeSpan actionTimeout;

    public ScenarioExecutor(IScenarioActions actions, IFactRecorder facts, TimeSpan? actionTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(facts);
        this.actions = actions;
        this.facts = facts;
        this.actionTimeout = actionTimeout ?? DefaultActionTimeout;
    }

    public Task<ScenarioOutcome> RunAsync(Scenario scenario, ChannelReader<ScenarioSignal> signals, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(signals);
        return new Run(this, signals, cancellationToken).ExecuteAsync(scenario);
    }

    /// <param name="Reason">Причина срыва; у выполненного шага <c>null</c>.</param>
    /// <param name="Dialog">Диалог, засчитанный шагу или остановивший его.</param>
    /// <param name="Error">Тип исключения, если действие бросило.</param>
    private sealed record StepVerdict(string? Reason, DialogInfo? Dialog = null, string? Error = null)
    {
        public static StepVerdict Done { get; } = new((string?)null);
    }

    private sealed class Run(ScenarioExecutor owner, ChannelReader<ScenarioSignal> signals, CancellationToken cancellation)
    {
        private readonly Queue<ScenarioSignal> inbox = new();

        // Диалоги, появившиеся после конца прошлого шага или во время текущего и не засчитанные ни одному шагу.
        private readonly List<DialogInfo> unclaimed = [];

        private Task<bool>? arrival;
        private bool ended;
        private TimeSpan now;
        private string? title;
        private bool exited;
        private TimeSpan? quietSince;

        // Ход рендера: хэндл окна рендера, если оно появлялось, и первый диалог после него.
        private long? rendering;
        private DialogInfo? renderDone;

        private bool renderingSeen => rendering is not null;

        public async Task<ScenarioOutcome> ExecuteAsync(Scenario scenario)
        {
            owner.facts.Record(ScenarioFactKinds.ScenarioStarted, new { steps = scenario.Steps.Count });
            ScenarioStep? current = null;
            try
            {
                foreach (var step in scenario.Steps)
                {
                    current = step;
                    cancellation.ThrowIfCancellationRequested();

                    // Всё, что уже пришло, случилось до начала шага.
                    Collect();
                    while (inbox.TryDequeue(out var signal))
                    {
                        Apply(signal);
                    }
                    if (step is not (WaitDialogStep or WaitRenderDoneStep) && unclaimed.Count > 0)
                    {
                        owner.facts.Record(ScenarioFactKinds.UnexpectedDialog, new { line = step.Line, dialog = unclaimed[0] });
                        return Finish(ScenarioStatus.Stopped, step.Line, StepFailures.UnexpectedDialog);
                    }

                    owner.facts.Record(ScenarioFactKinds.StepStarted, new { line = step.Line, step = step.Text });
                    var start = now;
                    var verdict = step switch
                    {
                        ActionStep action => await RunActionAsync(action, start),
                        WaitStep wait => await RunWaitAsync(wait, start),
                        _ => throw new NotSupportedException(step.GetType().Name),
                    };
                    var seconds = Math.Round((now - start).TotalSeconds, 3);

                    if (verdict.Reason is null)
                    {
                        owner.facts.Record(ScenarioFactKinds.StepDone, new { line = step.Line, step = step.Text, seconds, dialog = verdict.Dialog });
                        continue;
                    }

                    owner.facts.Record(ScenarioFactKinds.StepFailed, new { line = step.Line, step = step.Text, seconds, reason = verdict.Reason, error = verdict.Error });
                    if (verdict.Reason == StepFailures.UnexpectedDialog)
                    {
                        owner.facts.Record(ScenarioFactKinds.UnexpectedDialog, new { line = step.Line, dialog = verdict.Dialog });
                        return Finish(ScenarioStatus.Stopped, step.Line, verdict.Reason);
                    }
                    return Finish(ScenarioStatus.Failed, step.Line, verdict.Reason);
                }
                return Finish(ScenarioStatus.Completed, null, null);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return Finish(ScenarioStatus.Cancelled, current?.Line, null);
            }
        }

        private async Task<StepVerdict> RunActionAsync(ActionStep step, TimeSpan start)
        {
            var limit = step is RenderStep ? RenderActionTimeout : owner.actionTimeout;
            using var stepCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            // Предел ставится сразу, а не только в цикле ниже: сигналы кончаются в любой момент — программа
            // вышла, пришёл stop, — и тогда времени, по которому считается таймаут, больше не приходит,
            // а ожидание итога действия осталось бы без предела вовсе.
            stepCancellation.CancelAfter(limit);
            var task = InvokeAsync(step, stepCancellation.Token);

            // Предел шага сработал сам, а сценарий не отменяли: это таймаут действия, а не его сбой.
            bool Timed() => stepCancellation.IsCancellationRequested && !cancellation.IsCancellationRequested;

            while (!task.IsCompleted && !ended)
            {
                await ArrivalAsync(task);

                // Во время действия сигналы только учитываются: диалог, вставший посреди действия, ждёт следующего шага.
                while (inbox.TryDequeue(out var signal))
                {
                    Apply(signal);
                }
                if (!task.IsCompleted && now - start >= limit)
                {
                    await stepCancellation.CancelAsync();
                    await SettleAsync(task);
                    return new StepVerdict(StepFailures.Timeout);
                }
            }

            try
            {
                var result = await task;
                if (result.Succeeded)
                {
                    return StepVerdict.Done;
                }
                return Timed() ? new StepVerdict(StepFailures.Timeout) : new StepVerdict(result.Reason ?? StepFailures.Exception);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                return Timed()
                    ? new StepVerdict(StepFailures.Timeout)
                    : new StepVerdict(StepFailures.Exception, Error: exception.GetType().FullName);
            }
        }

        // Действие, бросившее исключение до первого await, тоже должно дать задачу, а не пролететь мимо шага.
        private async Task<ActionResult> InvokeAsync(ActionStep step, CancellationToken token) =>
            await owner.actions.RunAsync(step, token);

        private async Task<StepVerdict> RunWaitAsync(WaitStep step, TimeSpan start)
        {
            while (true)
            {
                if (Check(step, start) is { } verdict)
                {
                    return verdict;
                }

                if (inbox.TryDequeue(out var signal))
                {
                    Apply(signal);
                    if (signal is DialogOpened opened && step is not (WaitDialogStep or WaitRenderDoneStep))
                    {
                        return new StepVerdict(StepFailures.UnexpectedDialog, opened.Dialog);
                    }
                    continue;
                }

                if (ended)
                {
                    return new StepVerdict(StepFailures.SignalsEnded);
                }
                await ArrivalAsync(null);
            }
        }

        /// <summary>Итог ожидания по тому, что известно сейчас; <c>null</c> — ждать дальше.</summary>
        private StepVerdict? Check(WaitStep step, TimeSpan start)
        {
            switch (step)
            {
                case WaitDialogStep when unclaimed.Count > 0:
                    var dialog = unclaimed[0];
                    unclaimed.RemoveAt(0);
                    return new StepVerdict(null, dialog);
                // Рендер кончился, когда окно рендера исчезло и вслед за ним встал диалог. Какой это диалог —
                // об окончании или об ошибке, — по нему не понять: текст в окнах программы нарисован. Диалог
                // засчитывается шагу и ждёт следующего: его кнопка нажимается отдельным press.
                case WaitRenderDoneStep when renderDone is { } finished:
                    renderDone = null;
                    rendering = null;
                    unclaimed.RemoveAll(open => open.Handle == finished.Handle);
                    return new StepVerdict(null, finished);
                case WaitTitleStep wait when title is not null && title.Contains(wait.Substring, StringComparison.Ordinal):
                case WaitExitStep when exited:
                case WaitIdleStep idle when quietSince is { } since && now - since >= idle.Quiet:
                    return StepVerdict.Done;
            }

            if (exited && step is not WaitExitStep)
            {
                return new StepVerdict(StepFailures.ProgramExited);
            }
            return now - start >= step.Timeout ? new StepVerdict(StepFailures.Timeout) : null;
        }

        private void Apply(ScenarioSignal signal)
        {
            if (signal.At > now)
            {
                now = signal.At;
            }

            switch (signal)
            {
                case DialogOpened opened:
                    unclaimed.Add(opened.Dialog);
                    // Концом рендера считается любой диалог, кроме самого окна рендера, после того как оно
                    // появилось. Порядка событий правило не требует: опрос окон за один заход записывает сперва
                    // появившиеся окна и лишь затем исчезнувшие, поэтому диалог об окончании приходит то после
                    // закрытия окна рендера, то перед ним. 17.09.2026 такой прогон повис на час до таймаута.
                    if (opened.Dialog.Title == ProShowWindows.RenderingWindow)
                    {
                        rendering = opened.Dialog.Handle;
                    }
                    else if (renderingSeen)
                    {
                        renderDone ??= opened.Dialog;
                    }
                    break;
                case DialogClosed closed:
                    // Диалог, закрывшийся сам, никого не ждёт: нажимать в нём уже нечего.
                    unclaimed.RemoveAll(dialog => dialog.Handle == closed.Handle);
                    break;
                case TitleChanged changed:
                    title = changed.Title;
                    break;
                case ActivitySampled sample:
                    // Покой отсчитывается от первого спокойного замера, а не от предыдущего: с запасом в один отрезок.
                    quietSince = sample.Quiet ? quietSince ?? sample.At : null;
                    break;
                case ProgramExited:
                    exited = true;
                    break;
            }
        }

        /// <summary>Дождаться нового сигнала или конца задачи и забрать всё, что пришло.</summary>
        private async Task ArrivalAsync(Task? other)
        {
            arrival ??= signals.WaitToReadAsync(cancellation).AsTask();
            await (other is null ? (Task)arrival : Task.WhenAny(other, arrival));
            Collect();
        }

        private void Collect()
        {
            if (arrival is { IsCompleted: true })
            {
                var more = arrival.GetAwaiter().GetResult();
                arrival = null;
                ended |= !more;
            }
            while (signals.TryRead(out var signal))
            {
                inbox.Enqueue(signal);
            }
            ended |= signals.Completion.IsCompleted;
        }

        private ScenarioOutcome Finish(ScenarioStatus status, int? line, string? reason)
        {
            owner.facts.Record(ScenarioFactKinds.ScenarioFinished, new { status, line, reason });
            return new ScenarioOutcome(status, line, reason);
        }

        private static async Task SettleAsync(Task task)
        {
            try
            {
                await task;
            }
            catch (Exception)
            {
                // Итог действия после таймаута уже не нужен: шаг сорвался.
            }
        }
    }
}
