using System.Text.Json;
using System.Threading.Channels;
using PsDoctor.Core.Observation;
using PsDoctor.Core.Scenarios;
using Xunit;

namespace PsDoctor.Core.Tests;

public sealed class ScenarioExecutorTests
{
    private static readonly DialogInfo OldFormat = new(0x10, "ProShow Gold", ["Old Show format detected."], ["ОК"]);

    private static readonly DialogInfo MissingFont = new(0x20, "Message", [], ["Ok to All", "Ok"]);

    [Fact]
    public async Task Диалог_во_время_запуска_засчитывается_следующему_ожиданию_диалога()
    {
        var stand = new Stand();
        stand.Actions.On<LaunchStep>(() => stand.Send(new DialogOpened(Seconds(7), OldFormat)));

        var outcome = await stand.RunAsync("launch \"C:\\lab\\p\\1.psh\"\nwait dialog 120\npress \"ОК\"");

        Assert.Equal(new ScenarioOutcome(ScenarioStatus.Completed, null, null), outcome);
        Assert.Equal(["launch", "press"], stand.Actions.Called);
        var claimed = stand.Facts.Single(fact => fact.Kind == ScenarioFactKinds.StepDone && Line(fact) == 2);
        Assert.Equal("ОК", claimed.Data.GetProperty("dialog").GetProperty("buttons")[0].GetString());
        Assert.DoesNotContain(stand.Facts, fact => fact.Kind == ScenarioFactKinds.UnexpectedDialog);
    }

    [Fact]
    public async Task Диалог_во_время_ожидания_заголовка_останавливает_сценарий_и_ничего_не_нажимается()
    {
        var stand = new Stand();
        stand.OnStepStarted(2, () =>
        {
            stand.Send(new TimeTick(Seconds(10)));
            stand.Send(new DialogOpened(Seconds(11), MissingFont));
        });

        var outcome = await stand.RunAsync("launch \"C:\\lab\\p\\1.psh\"\nwait title \"1.psh\" 600\npress \"Ok\"\nclose");

        Assert.Equal(new ScenarioOutcome(ScenarioStatus.Stopped, 2, StepFailures.UnexpectedDialog), outcome);
        Assert.Equal(["launch"], stand.Actions.Called);
        var fact = Assert.Single(stand.Facts, fact => fact.Kind == ScenarioFactKinds.UnexpectedDialog);
        Assert.Equal(2, Line(fact));
        var dialog = fact.Data.GetProperty("dialog");
        Assert.Equal("Message", dialog.GetProperty("title").GetString());
        Assert.Equal(["Ok to All", "Ok"], dialog.GetProperty("buttons").EnumerateArray().Select(button => button.GetString()));
        Assert.Equal(ScenarioFactKinds.ScenarioFinished, stand.Facts[^1].Kind);
    }

    [Fact]
    public async Task Диалог_после_конца_ожидания_останавливает_следующее_действие_до_его_вызова()
    {
        var stand = new Stand();
        stand.OnStepStarted(1, () =>
        {
            stand.Send(new TitleChanged(Seconds(20), "1.psh - ProShow Gold"));
            stand.Send(new DialogOpened(Seconds(21), MissingFont));
        });

        var outcome = await stand.RunAsync("wait title \"1.psh\" 600\nclose\nwait exit 60");

        Assert.Equal(new ScenarioOutcome(ScenarioStatus.Stopped, 2, StepFailures.UnexpectedDialog), outcome);
        Assert.Empty(stand.Actions.Called);
        Assert.Contains(stand.Facts, fact => fact.Kind == ScenarioFactKinds.StepDone && Line(fact) == 1);
        Assert.DoesNotContain(stand.Facts, fact => fact.Kind == ScenarioFactKinds.StepStarted && Line(fact) == 2);
    }

    [Fact]
    public async Task Диалог_закрывшийся_сам_не_ждёт_следующего_шага()
    {
        var stand = new Stand();
        stand.Actions.On<LaunchStep>(() =>
        {
            stand.Send(new DialogOpened(Seconds(3), OldFormat));
            stand.Send(new DialogClosed(Seconds(4), OldFormat.Handle));
        });

        var outcome = await stand.RunAsync("launch \"1.psh\"\nclose");

        Assert.Equal(ScenarioStatus.Completed, outcome.Status);
        Assert.Equal(["launch", "close"], stand.Actions.Called);
    }

    [Fact]
    public async Task Сорвавшийся_шаг_останавливает_сценарий()
    {
        var stand = new Stand();
        stand.Actions.On<LaunchStep>(() => ActionResult.Failed("already-running"));

        var outcome = await stand.RunAsync("launch \"1.psh\"\nwait title \"1.psh\" 600\nclose");

        Assert.Equal(new ScenarioOutcome(ScenarioStatus.Failed, 1, "already-running"), outcome);
        Assert.Equal(["launch"], stand.Actions.Called);
        var failed = Assert.Single(stand.Facts, fact => fact.Kind == ScenarioFactKinds.StepFailed);
        Assert.Equal("already-running", failed.Data.GetProperty("reason").GetString());
        Assert.DoesNotContain(stand.Facts, fact => fact.Kind == ScenarioFactKinds.StepStarted && Line(fact) == 2);
    }

    [Fact]
    public async Task Ожидание_истекает_по_времени_из_сигналов()
    {
        var stand = new Stand();
        stand.Send(new TimeTick(Seconds(100)));
        stand.OnStepStarted(1, () =>
        {
            stand.Send(new TitleChanged(Seconds(105), "Untitled - ProShow Gold"));
            stand.Send(new TimeTick(Seconds(129)));
            stand.Send(new TimeTick(Seconds(130)));
        });

        var outcome = await stand.RunAsync("wait title \"1.psh\" 30\nclose");

        Assert.Equal(new ScenarioOutcome(ScenarioStatus.Failed, 1, StepFailures.Timeout), outcome);
        var failed = Assert.Single(stand.Facts, fact => fact.Kind == ScenarioFactKinds.StepFailed);
        Assert.Equal(30, failed.Data.GetProperty("seconds").GetDouble());
        Assert.Empty(stand.Actions.Called);
    }

    [Fact]
    public async Task Ожидание_выполняется_когда_заголовок_приходит_до_таймаута()
    {
        var stand = new Stand();
        stand.Actions.On<LaunchStep>(() => stand.Send(new TimeTick(Seconds(2))));
        stand.OnStepStarted(2, () =>
        {
            stand.Send(new TitleChanged(Seconds(8), "Untitled - ProShow Gold"));
            stand.Send(new TitleChanged(Seconds(14), "1.psh - ProShow Gold"));
        });

        var outcome = await stand.RunAsync("launch \"1.psh\"\nwait title \"1.psh\" 600");

        Assert.Equal(ScenarioStatus.Completed, outcome.Status);
        var done = stand.Facts.Single(fact => fact.Kind == ScenarioFactKinds.StepDone && Line(fact) == 2);
        Assert.Equal(12, done.Data.GetProperty("seconds").GetDouble());
    }

    [Fact]
    public async Task Действие_без_итога_срывается_по_таймауту_и_получает_отмену()
    {
        var stand = new Stand();
        var cancelled = false;
        stand.Actions.On<CloseStep>(async token =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                throw;
            }
            return ActionResult.Done;
        });
        stand.OnStepStarted(1, () => stand.Send(new TimeTick(Seconds(61))));

        var outcome = await stand.RunAsync("close\nwait exit 60");

        Assert.Equal(new ScenarioOutcome(ScenarioStatus.Failed, 1, StepFailures.Timeout), outcome);
        Assert.True(cancelled);
    }

    [Fact]
    public async Task Исключение_действия_срывает_шаг_с_типом_исключения()
    {
        var stand = new Stand();
        stand.Actions.On<PressStep>(() => throw new InvalidOperationException("кнопки нет"));

        var outcome = await stand.RunAsync("press \"Ok\"");

        Assert.Equal(new ScenarioOutcome(ScenarioStatus.Failed, 1, StepFailures.Exception), outcome);
        var failed = Assert.Single(stand.Facts, fact => fact.Kind == ScenarioFactKinds.StepFailed);
        Assert.Equal("System.InvalidOperationException", failed.Data.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Выход_программы_срывает_ожидание_заголовка_и_выполняет_ожидание_выхода()
    {
        var stand = new Stand();
        stand.OnStepStarted(1, () => stand.Send(new ProgramExited(Seconds(3), -1)));

        var failed = await stand.RunAsync("wait title \"1.psh\" 600");
        var done = await new Stand().Also(next => next.Send(new ProgramExited(Seconds(3), 0))).RunAsync("wait exit 60");

        Assert.Equal(new ScenarioOutcome(ScenarioStatus.Failed, 1, StepFailures.ProgramExited), failed);
        Assert.Equal(ScenarioStatus.Completed, done.Status);
    }

    [Fact]
    public async Task Покой_засчитывается_после_заданных_секунд_без_активности()
    {
        var stand = new Stand();
        stand.OnStepStarted(1, () =>
        {
            stand.Send(new ActivitySampled(Seconds(1), Quiet: true));
            stand.Send(new ActivitySampled(Seconds(2), Quiet: false));
            stand.Send(new ActivitySampled(Seconds(3), Quiet: true));
            stand.Send(new ActivitySampled(Seconds(6), Quiet: true));
            stand.Send(new ActivitySampled(Seconds(8), Quiet: true));
        });

        var outcome = await stand.RunAsync("wait idle 5 60");

        Assert.Equal(ScenarioStatus.Completed, outcome.Status);
        var done = Assert.Single(stand.Facts, fact => fact.Kind == ScenarioFactKinds.StepDone);
        Assert.Equal(8, done.Data.GetProperty("seconds").GetDouble());
    }

    [Fact]
    public async Task Конец_потока_сигналов_срывает_невыполнимое_ожидание()
    {
        var stand = new Stand();
        stand.Signals.Writer.Complete();

        var outcome = await stand.RunAsync("wait dialog 60");

        Assert.Equal(new ScenarioOutcome(ScenarioStatus.Failed, 1, StepFailures.SignalsEnded), outcome);
    }

    [Fact]
    public async Task Ожидание_конца_рендера_не_умеет()
    {
        var outcome = await new Stand().RunAsync("wait render-done 3600");

        Assert.Equal(new ScenarioOutcome(ScenarioStatus.Failed, 1, StepFailures.Unsupported), outcome);
    }

    [Fact]
    public async Task Отмена_прерывает_ожидание()
    {
        var stand = new Stand();
        using var cancellation = new CancellationTokenSource();
        stand.OnStepStarted(2, cancellation.Cancel);

        var outcome = await stand.RunAsync("launch \"1.psh\"\nwait exit 600", cancellation.Token);

        Assert.Equal(new ScenarioOutcome(ScenarioStatus.Cancelled, 2, null), outcome);
        Assert.Equal("cancelled", stand.Facts[^1].Data.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Факты_шагов_идут_по_порядку()
    {
        var stand = new Stand();

        await stand.RunAsync("launch \"1.psh\"\nclose");

        Assert.Equal(
            ["scenario-started", "step-started", "step-done", "step-started", "step-done", "scenario-finished"],
            stand.Facts.Select(fact => fact.Kind));
        Assert.Equal(Enumerable.Range(1, 6).Select(number => (long)number), stand.Facts.Select(fact => fact.Number));
    }

    private static TimeSpan Seconds(int seconds) => TimeSpan.FromSeconds(seconds);

    private static int Line(Fact fact) => fact.Data.GetProperty("line").GetInt32();

    /// <summary>Подменённый стенд: действия, поток сигналов и журнал в памяти.</summary>
    private sealed class Stand : IFactRecorder
    {
        private readonly List<(int Line, Action Hook)> stepHooks = [];

        public Channel<ScenarioSignal> Signals { get; } = Channel.CreateUnbounded<ScenarioSignal>();

        public FakeActions Actions { get; } = new();

        public List<Fact> Facts { get; } = [];

        public void Send(ScenarioSignal signal) => Assert.True(Signals.Writer.TryWrite(signal));

        public Stand Also(Action<Stand> setup)
        {
            setup(this);
            return this;
        }

        /// <summary>Выполнить, когда записан факт начала шага: сигнал заведомо приходит во время шага.</summary>
        public void OnStepStarted(int line, Action hook) => stepHooks.Add((line, hook));

        public Task<ScenarioOutcome> RunAsync(string text, CancellationToken cancellationToken = default)
        {
            var scenario = ScenarioParser.Parse(text).Scenario ?? throw new ArgumentException("сценарий не разобран", nameof(text));
            return new ScenarioExecutor(Actions, this).RunAsync(scenario, Signals.Reader, cancellationToken);
        }

        public Fact Record(string kind, int? processId, JsonElement data)
        {
            var fact = new Fact(Facts.Count + 1, TimeSpan.Zero, "сеанс", processId, kind, data.Clone());
            Facts.Add(fact);
            if (kind == ScenarioFactKinds.StepStarted)
            {
                foreach (var (line, hook) in stepHooks.Where(entry => entry.Line == Line(fact)))
                {
                    hook();
                }
            }
            return fact;
        }
    }

    private sealed class FakeActions : IScenarioActions
    {
        private readonly Dictionary<Type, Func<CancellationToken, Task<ActionResult>>> behaviour = [];

        public List<string> Called { get; } = [];

        public void On<TStep>(Action effect) where TStep : ActionStep =>
            behaviour[typeof(TStep)] = _ =>
            {
                effect();
                return Task.FromResult(ActionResult.Done);
            };

        public void On<TStep>(Func<ActionResult> result) where TStep : ActionStep =>
            behaviour[typeof(TStep)] = _ => Task.FromResult(result());

        public void On<TStep>(Func<CancellationToken, Task<ActionResult>> run) where TStep : ActionStep =>
            behaviour[typeof(TStep)] = run;

        public Task<ActionResult> RunAsync(ActionStep step, CancellationToken cancellationToken)
        {
            Called.Add(step.Text.Split(' ')[0]);
            return behaviour.TryGetValue(step.GetType(), out var run) ? run(cancellationToken) : Task.FromResult(ActionResult.Done);
        }
    }
}
