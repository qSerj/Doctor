using PsDoctor.Core.Observation;
using PsDoctor.Core.Scenarios;

namespace PsDoctor.Workbench.Tests;

/// <summary>
/// Подменённый запуск программы: диалог открыт с самого начала, нажатие его закрывает, выход — по команде
/// теста. ProShow для этих тестов не нужен: пульт проверяется на настоящих маршрутах наблюдателя.
/// </summary>
internal sealed class Запуск : IProgramLauncher
{
    private readonly Lock замок = new();

    public Прогон? Последний { get; private set; }

    public bool IsProgramRunning() => Последний is { Жив: true };

    public IProgramRun Launch(string showPath, IFactRecorder facts, IProgramEvents events)
    {
        var прогон = new Прогон(facts, events);
        facts.Record(ProgramFactKinds.ProgramLaunched, new ProgramLaunched("proshow.exe", $"proshow.exe \"{showPath}\"", null, true), прогон.ProcessId);
        прогон.ОткрытьДиалог(Диалог);
        lock (замок)
        {
            Последний = прогон;
        }
        return прогон;
    }

    /// <summary>Диалог о старом формате: такой же, каким его видит сценарий на стенде.</summary>
    public static DialogInfo Диалог { get; } =
        new(0x20, "ProShow Producer", ["Проект сохранён прежней версией"], ["Ok to All", "Ok"], "#32770", 1000);

    internal sealed class Прогон(IFactRecorder факты, IProgramEvents события) : IProgramRun
    {
        private readonly Lock замок = new();
        private readonly List<DialogInfo> диалоги = [];
        private readonly List<string> нажатия = [];

        public int ProcessId => 1000;

        public bool Жив { get; private set; } = true;

        /// <summary>Сколько раз программе послали <c>close</c>: после «прекратить наблюдение» должно остаться нулём.</summary>
        public int Закрытий { get; private set; }

        /// <summary>Наблюдение за прогоном прекращено.</summary>
        public bool Отпущен { get; private set; }

        public IReadOnlyList<string> Нажатия
        {
            get
            {
                lock (замок)
                {
                    return [.. нажатия];
                }
            }
        }

        public void ОткрытьДиалог(DialogInfo диалог)
        {
            lock (замок)
            {
                диалоги.Add(диалог);
            }
            факты.Record(ProgramFactKinds.DialogOpened, диалог, ProcessId);
            события.DialogAppeared(диалог);
        }

        public IReadOnlyList<DialogInfo> Dialogs()
        {
            lock (замок)
            {
                return [.. диалоги];
            }
        }

        public Task<ActionResult> PressAsync(string button, CancellationToken cancellationToken)
        {
            var открытые = Dialogs();
            var диалог = открытые.LastOrDefault(d => d.Buttons.Contains(button));
            if (диалог is null)
            {
                return Task.FromResult(ActionResult.Failed(открытые.Count == 0 ? WindowActionFailures.NoDialog : WindowActionFailures.NoButton));
            }
            факты.Record(ProgramFactKinds.DialogPressed, new DialogPressed(диалог.Handle, 1, button, "fake", "invoked", 1, false), ProcessId);
            lock (замок)
            {
                нажатия.Add(button);
                диалоги.Remove(диалог);
            }
            факты.Record(ProgramFactKinds.DialogClosed, new WindowClosed(диалог.Handle));
            события.DialogDisappeared(диалог.Handle);
            return Task.FromResult(ActionResult.Done);
        }

        public ActionResult Close()
        {
            Закрытий++;
            факты.Record(ProgramFactKinds.CloseRequested, new CloseRequested(0x100), ProcessId);
            Выйти();
            return ActionResult.Done;
        }

        public Task<ActionResult> RenderAsync(CancellationToken cancellationToken) =>
            Task.FromResult(ActionResult.Failed(WindowActionFailures.NoWindow));

        public void Выйти()
        {
            if (!Жив)
            {
                return;
            }
            Жив = false;
            факты.Record(ProgramFactKinds.ProcessExited, new ProcessExited(0, false, 1, 1100, 2000, 500), ProcessId);
            события.MainExited(0);
            факты.Record(ProgramFactKinds.JobAccounting, new JobAccounting(1, 0, 0, 1, 0, 0, 1100, 1100));
            события.AllExited();
        }

        public void Conclude() => факты.Record(ProgramFactKinds.ServiceFiles, new ServiceFilesDiff([], 0, 0, 0, false, [], [], []));

        public void Dispose() => Отпущен = true;
    }
}
