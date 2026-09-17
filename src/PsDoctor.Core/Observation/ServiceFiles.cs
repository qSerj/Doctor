namespace PsDoctor.Core.Observation;

/// <summary>Место служебных файлов программы: каталог, обходится ли он вглубь и маска имён.</summary>
public sealed record ServiceFilePlace(string Path, bool Recursive, string Pattern = "*");

/// <param name="Exists">Каталог есть. Места, которого нет, — тоже факт: в опыте 03 так было видно, где программа не пишет.</param>
/// <param name="Error">Каталог не обошёлся целиком: имя исключения. Файлы, снятые до сбоя, в снимке есть.</param>
public sealed record ServiceFilePlaceState(string Path, bool Recursive, string Pattern, bool Exists, int Files, string? Error);

/// <param name="Written">Время последней записи, UTC.</param>
public sealed record ServiceFileState(long Size, DateTimeOffset Written);

/// <summary>Снимок служебных файлов: путь — размер и время записи.</summary>
public sealed record ServiceFilesSnapshot(IReadOnlyList<ServiceFilePlaceState> Places, IReadOnlyDictionary<string, ServiceFileState> Files);

public sealed record ServiceFileEntry(string Path, long Size, DateTimeOffset Written);

public sealed record ServiceFileChange(string Path, long SizeBefore, long SizeAfter, DateTimeOffset WrittenBefore, DateTimeOffset WrittenAfter);

/// <summary>Факт <see cref="ProgramFactKinds.ServiceFilesBefore"/>: снимок снят до запуска программы.</summary>
/// <param name="Seconds">Сколько шёл обход — это часть цены наблюдения.</param>
public sealed record ServiceFilesTaken(IReadOnlyList<ServiceFilePlaceState> Places, int Files, double Seconds);

/// <summary>
/// Факт <see cref="ProgramFactKinds.ServiceFiles"/>: что появилось, изменилось и пропало за сеанс. Вердиктов нет —
/// «кэш переписан» или «автосохранение осталось» говорит тот, кто читает журнал.
/// </summary>
/// <param name="ProgramAlive">Процессы программы ещё жили, когда снимали «после»: наблюдение прекращено раньше выхода.</param>
public sealed record ServiceFilesDiff(
    IReadOnlyList<ServiceFilePlaceState> Places,
    int FilesBefore,
    int FilesAfter,
    double Seconds,
    bool ProgramAlive,
    IReadOnlyList<ServiceFileEntry> Appeared,
    IReadOnlyList<ServiceFileChange> Changed,
    IReadOnlyList<ServiceFileEntry> Disappeared);

public static class ServiceFileComparison
{
    /// <summary>
    /// Разница двух снимков. Изменённым считается файл, у которого сменился размер или время записи: в опыте 03
    /// <c>proshow.phd</c> переписан без смены размера. Списки упорядочены по пути.
    /// </summary>
    public static (IReadOnlyList<ServiceFileEntry> Appeared, IReadOnlyList<ServiceFileChange> Changed, IReadOnlyList<ServiceFileEntry> Disappeared) Compare(
        IReadOnlyDictionary<string, ServiceFileState> before,
        IReadOnlyDictionary<string, ServiceFileState> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var appeared = new List<ServiceFileEntry>();
        var changed = new List<ServiceFileChange>();
        var disappeared = new List<ServiceFileEntry>();
        foreach (var (path, now) in after.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!before.TryGetValue(path, out var was))
            {
                appeared.Add(new ServiceFileEntry(path, now.Size, now.Written));
            }
            else if (was != now)
            {
                changed.Add(new ServiceFileChange(path, was.Size, now.Size, was.Written, now.Written));
            }
        }
        foreach (var (path, was) in before.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!after.ContainsKey(path))
            {
                disappeared.Add(new ServiceFileEntry(path, was.Size, was.Written));
            }
        }
        return (appeared, changed, disappeared);
    }
}

/// <summary>Событие журнала Windows: поля из системной части и параметры события строками, без локализованного текста.</summary>
public sealed record WindowsEvent(DateTimeOffset Time, string Log, string Provider, int Id, long? RecordId, IReadOnlyList<string> Properties);

/// <summary>
/// Факт <see cref="ProgramFactKinds.WindowsEvents"/>: события журнала за время сеанса, в параметрах которых есть имя
/// одного из образов программы. Пустой список — тоже факт: журнал прочитан, событий нет.
/// </summary>
/// <param name="Error">Журнал не прочитался: имя исключения.</param>
public sealed record WindowsEventsRead(
    DateTimeOffset From,
    DateTimeOffset To,
    string Log,
    IReadOnlyList<int> Ids,
    IReadOnlyList<string> Names,
    int Scanned,
    IReadOnlyList<WindowsEvent> Events,
    string? Error);
