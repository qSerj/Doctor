using PsDoctor.Core.Observation;

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
        public int ProcessId { get; } = processId;

        public bool Disposed { get; private set; }

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
