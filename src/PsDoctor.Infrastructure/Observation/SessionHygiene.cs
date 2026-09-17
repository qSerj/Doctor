using System.Diagnostics;
using System.Runtime.Versioning;
using PsDoctor.Core.Observation;

namespace PsDoctor.Infrastructure.Observation;

/// <summary>
/// Гигиена сеанса: снимок служебных файлов до запуска, разница и события журнала Windows после. Только факты —
/// вердиктов по разнице нет.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SessionHygiene
{
    /// <summary>
    /// Запас назад от начала сеанса при чтении журнала: время события ставит система, а начало берётся по часам
    /// наблюдателя до запуска, и граница с точностью до миллисекунд не нужна.
    /// </summary>
    private static readonly TimeSpan EventSlack = TimeSpan.FromSeconds(1);

    private readonly IReadOnlyList<ServiceFilePlace> places;
    private readonly IReadOnlyList<string> imageNames;
    private readonly IFactRecorder facts;
    private readonly Lock gate = new();
    private ServiceFilesSnapshot? before;
    private DateTimeOffset started;
    private bool concluded;

    /// <param name="imageNames">Имена образов программы, по которым отбираются события журнала.</param>
    public SessionHygiene(IReadOnlyList<ServiceFilePlace> places, IReadOnlyList<string> imageNames, IFactRecorder facts)
    {
        ArgumentNullException.ThrowIfNull(places);
        ArgumentNullException.ThrowIfNull(imageNames);
        ArgumentNullException.ThrowIfNull(facts);
        this.places = places;
        this.imageNames = imageNames;
        this.facts = facts;
    }

    /// <summary>Снимок «до». Вызывается перед запуском программы.</summary>
    public void Begin()
    {
        started = DateTimeOffset.UtcNow;
        var watch = Stopwatch.StartNew();
        var snapshot = ServiceFileScanner.Take(places);
        before = snapshot;
        facts.Record(ProgramFactKinds.ServiceFilesBefore, new ServiceFilesTaken(snapshot.Places, snapshot.Files.Count, Seconds(watch)));
    }

    /// <summary>Снимок «после», разница и события журнала. Второй вызов ничего не делает.</summary>
    /// <param name="programAlive">В задании ещё есть процессы.</param>
    public void Conclude(bool programAlive)
    {
        lock (gate)
        {
            if (concluded || before is null)
            {
                return;
            }
            concluded = true;
        }
        var watch = Stopwatch.StartNew();
        var after = ServiceFileScanner.Take(places);
        var (appeared, changed, disappeared) = ServiceFileComparison.Compare(before.Files, after.Files);
        facts.Record(
            ProgramFactKinds.ServiceFiles,
            new ServiceFilesDiff(after.Places, before.Files.Count, after.Files.Count, Seconds(watch), programAlive, appeared, changed, disappeared));

        facts.Record(
            ProgramFactKinds.WindowsEvents,
            WindowsEventLog.Read(WindowsEventLog.Application, WindowsEventLog.CrashIds, imageNames, started - EventSlack, DateTimeOffset.UtcNow));
    }

    private static double Seconds(Stopwatch watch) => Math.Round(watch.Elapsed.TotalSeconds, 3);
}
