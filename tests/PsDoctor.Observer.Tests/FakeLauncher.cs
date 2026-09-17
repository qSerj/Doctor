using PsDoctor.Core.Observation;
using PsDoctor.Core.Scenarios;

namespace PsDoctor.Observer.Tests;

/// <summary>Подменённый запуск: процессы — факты, которые пишет тест, выход — по команде теста.</summary>
public sealed class FakeLauncher : IProgramLauncher
{
    private readonly Lock gate = new();
    private readonly List<FakeRun> runs = [];

    /// <summary>Программа запущена мимо наблюдателя.</summary>
    public bool Foreign { get; set; }

    public IReadOnlyList<FakeRun> Runs
    {
        get
        {
            lock (gate)
            {
                return [.. runs];
            }
        }
    }

    /// <summary>Запуск не удаётся: Windows не создала процесс.</summary>
    public bool Fails { get; set; }

    public bool IsProgramRunning() => Foreign;

    public IProgramRun Launch(string showPath, IFactRecorder facts, IProgramEvents events)
    {
        if (Fails)
        {
            facts.Record(ProgramFactKinds.LaunchFailed, new LaunchFailed("proshow.exe", "create-process", 2));
            throw new ProgramLaunchException("create-process: ошибка Win32 2");
        }
        var run = new FakeRun(1000, facts, events);
        facts.Record(ProgramFactKinds.ProgramLaunched, new ProgramLaunched("proshow.exe", $"proshow.exe \"{showPath}\"", null, true), run.ProcessId);
        facts.Record(ProgramFactKinds.ProcessStarted, new ProcessStarted(1, "proshow.exe", $"proshow.exe \"{showPath}\""), run.ProcessId);
        lock (gate)
        {
            runs.Add(run);
        }
        return run;
    }

    public sealed class FakeRun(int processId, IFactRecorder facts, IProgramEvents events) : IProgramRun
    {
        private readonly Lock gate = new();
        private readonly List<DialogInfo> dialogs = [];
        private readonly List<string> pressed = [];

        public int ProcessId { get; } = processId;

        public bool Disposed { get; private set; }

        /// <summary>Сколько раз программе послан <c>close</c>.</summary>
        public int CloseRequests { get; private set; }

        /// <summary>Программа выходит сама вскоре после <c>close</c>, как ProShow без вопроса о сохранении.</summary>
        public bool ExitsOnClose { get; set; } = true;

        /// <summary>Что нажимали, по порядку.</summary>
        public IReadOnlyList<string> Pressed
        {
            get
            {
                lock (gate)
                {
                    return [.. pressed];
                }
            }
        }

        /// <summary>Вызывается после нажатия: тест открывает следующий диалог, как это делает программа.</summary>
        public Action<string>? AfterPress { get; set; }

        public void Title(string? title)
        {
            facts.Record(ProgramFactKinds.MainWindow, new MainWindowState(title is null ? null : 0x100, title, false), ProcessId);
            events.TitleChanged(title);
        }

        public void OpenDialog(DialogInfo dialog)
        {
            lock (gate)
            {
                dialogs.Add(dialog);
            }
            facts.Record(ProgramFactKinds.DialogOpened, dialog, ProcessId);
            events.DialogAppeared(dialog);
        }

        public void CloseDialog(long handle)
        {
            lock (gate)
            {
                dialogs.RemoveAll(d => d.Handle == handle);
            }
            facts.Record(ProgramFactKinds.DialogClosed, new WindowClosed(handle));
            events.DialogDisappeared(handle);
        }

        public void Activity(bool quiet) => events.Activity(quiet);

        public IReadOnlyList<DialogInfo> Dialogs()
        {
            lock (gate)
            {
                return [.. dialogs];
            }
        }

        public Task<ActionResult> PressAsync(string button, CancellationToken cancellationToken)
        {
            var open = Dialogs();
            if (open.Count == 0)
            {
                return Task.FromResult(ActionResult.Failed(WindowActionFailures.NoDialog));
            }
            var dialog = open.LastOrDefault(d => d.Buttons.Contains(button));
            if (dialog is null)
            {
                return Task.FromResult(ActionResult.Failed(WindowActionFailures.NoButton));
            }
            facts.Record(ProgramFactKinds.DialogPressed, new DialogPressed(dialog.Handle, 1, button, "fake", "invoked", 1, false), ProcessId);
            lock (gate)
            {
                pressed.Add(button);
            }
            CloseDialog(dialog.Handle);
            AfterPress?.Invoke(button);
            return Task.FromResult(ActionResult.Done);
        }

        public ActionResult Close()
        {
            CloseRequests++;
            facts.Record(ProgramFactKinds.CloseRequested, new CloseRequested(0x100), ProcessId);
            if (ExitsOnClose)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(20);
                    Title(null);
                    Exit(0);
                });
            }
            return ActionResult.Done;
        }

        public void Sample(int count)
        {
            for (var i = 0; i < count; i++)
            {
                facts.Record(ProgramFactKinds.ProcessSample, new ProcessSample(i, 0, 0, 1000 + i, 1000 + i, 2000, 2000, 500, 10), ProcessId);
            }
        }

        public void Exit(int code)
        {
            facts.Record(ProgramFactKinds.ProcessExited, new ProcessExited(code, false, 1, 1100, 2000, 500), ProcessId);
            events.MainExited(code);
            facts.Record(ProgramFactKinds.JobAccounting, new JobAccounting(1, 0, 0, 1, 0, 0, 1100, 1100));
            events.AllExited();
        }

        public void Dispose() => Disposed = true;
    }
}
