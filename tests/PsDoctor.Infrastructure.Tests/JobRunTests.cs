using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using PsDoctor.Core.Observation;
using PsDoctor.Infrastructure.Observation;
using Xunit;

namespace PsDoctor.Infrastructure.Tests;

/// <summary>
/// Запуск под заданием на настоящем <c>cmd.exe</c>, без ProShow. Только Windows: на других системах тесты
/// молча не выполняются, и зелёный прогон на Linux о них не говорит ничего — они гоняются на стенде.
/// </summary>
/// <remarks>Атрибут платформы — для анализатора внутри лямбд; от запуска на Linux защищает проверка в начале теста.</remarks>
[SupportedOSPlatform("windows")]
public sealed class JobRunTests
{
    private static readonly TimeSpan Терпение = TimeSpan.FromSeconds(30);

    private static string Cmd => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    private sealed class События : IProgramEvents
    {
        public TaskCompletionSource<int?> ОсновнойВышел { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ВсеВышли { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void MainExited(int? exitCode) => ОсновнойВышел.TrySetResult(exitCode);

        public void AllExited() => ВсеВышли.TrySetResult();
    }

    private static FactLog НовыйЖурнал()
    {
        var часы = Stopwatch.StartNew();
        return new FactLog(new FactJournalWriter(new StringWriter(), "тест", () => часы.Elapsed));
    }

    private static T Данные<T>(Fact факт) => факт.Data.Deserialize<T>(ObservationJson.Options)!;

    [Fact]
    public async Task Куст_cmd_и_ping_виден_целиком_с_кодом_выхода_и_учётом_задания()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var журнал = НовыйЖурнал();
        var события = new События();

        using (var запуск = JobRun.Start(Cmd, $"\"{Cmd}\" /c \"ping -n 2 127.0.0.1 >nul & exit /b 7\"", null, журнал, события))
        {
            await события.ВсеВышли.Task.WaitAsync(Терпение);
            Assert.Equal(7, await события.ОсновнойВышел.Task.WaitAsync(Терпение));

            var факты = журнал.After(0);
            var запущен = Данные<ProgramLaunched>(факты.Single(f => f.Kind == ProgramFactKinds.ProgramLaunched));
            Assert.Equal(Cmd, запущен.Application);

            var старты = факты.Where(f => f.Kind == ProgramFactKinds.ProcessStarted).ToList();
            var ping = старты.Single(f => Данные<ProcessStarted>(f).Image?.EndsWith("PING.EXE", StringComparison.OrdinalIgnoreCase) == true);
            Assert.Contains("127.0.0.1", Данные<ProcessStarted>(ping).CommandLine, StringComparison.Ordinal);
            Assert.Equal(запуск.ProcessId, Данные<ProcessStarted>(ping).ParentProcessId);
            Assert.Contains("exit /b 7", Данные<ProcessStarted>(старты.Single(f => f.ProcessId == запуск.ProcessId)).CommandLine, StringComparison.Ordinal);

            var выходОсновного = Данные<ProcessExited>(факты.Single(f => f.Kind == ProgramFactKinds.ProcessExited && f.ProcessId == запуск.ProcessId));
            Assert.Equal(7, выходОсновного.ExitCode);
            Assert.True(выходОсновного.PeakPagefileUsage > 0, "пик выделенной памяти при выходе не прочитан");
            Assert.Equal(старты.Count, факты.Count(f => f.Kind == ProgramFactKinds.ProcessExited));

            // Пункт 3 критерия: число фактов о старте равно числу процессов по учёту задания.
            var учёт = Данные<JobAccounting>(факты.Single(f => f.Kind == ProgramFactKinds.JobAccounting));
            Assert.Equal(старты.Count, учёт.TotalProcesses);
            Assert.Equal(0, учёт.ActiveProcesses);
            Assert.True(учёт.PeakProcessMemoryUsed > 0);
        }
    }

    [Fact]
    public async Task Прекращение_наблюдения_не_завершает_программу()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var журнал = НовыйЖурнал();
        var события = new События();
        var запуск = JobRun.Start(Cmd, $"\"{Cmd}\" /c \"ping -n 120 127.0.0.1 >nul\"", null, журнал, события);
        var pid = запуск.ProcessId;
        try
        {
            using var время = new CancellationTokenSource(Терпение);
            while (журнал.After(0).Count(f => f.Kind == ProgramFactKinds.ProcessStarted) < 2)
            {
                await Task.Delay(50, время.Token);
            }

            // Хэндлы задания, порта и процессов закрыты — ровно то, что делает выход наблюдателя.
            запуск.Dispose();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Task.Delay(TimeSpan.FromSeconds(2));

            using var программа = Process.GetProcessById(pid);
            Assert.False(программа.HasExited, "выход наблюдателя завершил программу");
            Assert.False(события.ВсеВышли.Task.IsCompleted);
            Assert.Single(журнал.After(0), f => f.Kind == ProgramFactKinds.JobAccounting);
        }
        finally
        {
            try
            {
                using var программа = Process.GetProcessById(pid);
                программа.Kill(entireProcessTree: true);
            }
            catch (ArgumentException)
            {
            }
        }
    }

    [Fact]
    public async Task Замеры_раз_в_секунду_у_живого_процесса()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var журнал = НовыйЖурнал();
        var события = new События();

        using (var запуск = JobRun.Start(Cmd, $"\"{Cmd}\" /c \"ping -n 4 127.0.0.1 >nul\"", null, журнал, события))
        {
            await события.ВсеВышли.Task.WaitAsync(Терпение);

            var замеры = журнал.After(0).Where(f => f.Kind == ProgramFactKinds.ProcessSample && f.ProcessId == запуск.ProcessId).ToList();
            Assert.InRange(замеры.Count, 2, 6);
            var замер = Данные<ProcessSample>(замеры[^1]);
            Assert.True(замер.PagefileUsage > 0);
            Assert.True(замер.VirtualSize > 0);
            Assert.True(замер.WorkingSetSize > 0);
            Assert.True(замер.Handles > 0);
            Assert.NotNull(замер.CpuSeconds);
            Assert.NotNull(замер.ReadBytes);
        }
    }

    [Fact]
    public void Несуществующая_программа_срывает_запуск_фактом()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var журнал = НовыйЖурнал();
        var нет = Path.Combine(Path.GetTempPath(), "psdoctor-нет-такой.exe");

        Assert.Throws<ProgramLaunchException>(() => JobRun.Start(нет, $"\"{нет}\"", null, журнал, new События()));

        var срыв = Данные<LaunchFailed>(Assert.Single(журнал.After(0)));
        Assert.Equal("create-process", срыв.Stage);
        Assert.Equal(2, срыв.Error);
    }

    [Fact]
    public void Запущенная_мимо_наблюдателя_программа_видна_по_имени_образа()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var ping = Path.Combine(Environment.SystemDirectory, "PING.EXE");
        Assert.False(new ProShowLauncher(Path.Combine(Environment.SystemDirectory, "psdoctor-нет-такой.exe")).IsProgramRunning());

        using var чужая = Process.Start(new ProcessStartInfo(ping, "-n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        try
        {
            Assert.True(new ProShowLauncher(ping).IsProgramRunning());
        }
        finally
        {
            чужая.Kill();
        }
    }
}
