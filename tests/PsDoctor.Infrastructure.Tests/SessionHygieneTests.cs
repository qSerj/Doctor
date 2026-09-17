using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using PsDoctor.Core.Observation;
using PsDoctor.Infrastructure.Observation;
using Xunit;

namespace PsDoctor.Infrastructure.Tests;

/// <summary>
/// Гигиена сеанса на настоящем запуске под заданием: Windows PowerShell пишет и удаляет файлы в месте служебных
/// файлов и падает через <c>FailFast</c>. Окон не нужно — выполняется и в нулевом сеансе; не на Windows молча не выполняется.
/// </summary>
/// <remarks>
/// Падение даёт событие 1000 «Application Error» с <c>powershell.exe</c> в параметрах в момент падения — так на стенде
/// 17.09.2026; отчёт 1001 пришёл через 13 с, поэтому его тест не ждёт. На машине с выключенным Windows Error Reporting
/// события 1000 нет, и тест сорвётся — это свойство машины, а не кода.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class SessionHygieneTests : IDisposable
{
    private static readonly TimeSpan Терпение = TimeSpan.FromSeconds(60);

    private readonly string _место = Directory.CreateTempSubdirectory("psdoctor-гигиена-").FullName;

    public void Dispose() => Directory.Delete(_место, recursive: true);

    private sealed class События : IProgramEvents
    {
        public TaskCompletionSource ВсеВышли { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void MainExited(int? exitCode)
        {
        }

        public void AllExited() => ВсеВышли.TrySetResult();

        public void TitleChanged(string? title)
        {
        }

        public void DialogAppeared(Core.Scenarios.DialogInfo dialog)
        {
        }

        public void DialogDisappeared(long handle)
        {
        }

        public void Activity(bool quiet)
        {
        }
    }

    [Fact]
    public async Task Разница_служебных_файлов_и_событие_падения_после_сеанса()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var автосохранение = Path.Combine(_место, "autosave.psh");
        File.WriteAllBytes(автосохранение, new byte[5]);
        var часы = Stopwatch.StartNew();
        var журнал = new FactLog(new FactJournalWriter(new StringWriter(), "тест", () => часы.Elapsed));
        var гигиена = new SessionHygiene([new ServiceFilePlace(_место, true)], ["powershell"], журнал);
        var скрипт = $"""
            Set-Content -LiteralPath '{Path.Combine(_место, "proshow.cfg")}' -Value 'x'
            Remove-Item -LiteralPath '{автосохранение}'
            [Environment]::FailFast('psdoctor-гигиена')
            """;
        var powershell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        var закодирован = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(скрипт));
        var события = new События();

        using (var запуск = ProgramRun.Start(powershell, $"\"{powershell}\" -NoProfile -EncodedCommand {закодирован}", null, журнал, события, hygiene: гигиена))
        {
            await события.ВсеВышли.Task.WaitAsync(Терпение);
            запуск.Dispose();
            запуск.Conclude();
            запуск.Conclude();
        }

        var факты = журнал.After(0);
        var до = Данные<ServiceFilesTaken>(факты.Single(f => f.Kind == ProgramFactKinds.ServiceFilesBefore));
        Assert.Equal(1, до.Files);
        Assert.True(факты.ToList().FindIndex(f => f.Kind == ProgramFactKinds.ServiceFilesBefore) < факты.ToList().FindIndex(f => f.Kind == ProgramFactKinds.ProgramLaunched));

        var разница = Данные<ServiceFilesDiff>(факты.Single(f => f.Kind == ProgramFactKinds.ServiceFiles));
        Assert.False(разница.ProgramAlive);
        Assert.Equal([Path.Combine(_место, "proshow.cfg")], разница.Appeared.Select(f => f.Path));
        Assert.Equal([автосохранение], разница.Disappeared.Select(f => f.Path));
        Assert.Empty(разница.Changed);

        var журналWindows = Данные<WindowsEventsRead>(факты.Single(f => f.Kind == ProgramFactKinds.WindowsEvents));
        Assert.Null(журналWindows.Error);
        var падение = Assert.Single(журналWindows.Events, e => e.Id == 1000);
        Assert.Equal("Application Error", падение.Provider);
        Assert.Contains("powershell.exe", падение.Properties);
    }

    private static T Данные<T>(Fact факт) => факт.Data.Deserialize<T>(ObservationJson.Options)!;
}
