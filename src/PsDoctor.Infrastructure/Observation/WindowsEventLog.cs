using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Runtime.Versioning;
using PsDoctor.Core.Observation;

namespace PsDoctor.Infrastructure.Observation;

/// <summary>
/// События журнала Windows, отобранные по имени образа в параметрах события. Так в опыте 08
/// нашлись отчёты <c>RADAR_PRE_LEAK_WOW64</c> о программе, которые сама программа не сообщает.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsEventLog
{
    public const string Application = "Application";

    /// <summary>Падение приложения, отчёт Windows Error Reporting, зависание приложения — как в опытах 08 и 09.</summary>
    public static readonly IReadOnlyList<int> CrashIds = [1000, 1001, 1002];

    /// <summary>События за отрезок времени — итог сеанса запуска.</summary>
    public static WindowsEventsRead Read(string log, IReadOnlyList<int> ids, IReadOnlyList<string> names, DateTimeOffset from, DateTimeOffset to)
    {
        ArgumentException.ThrowIfNullOrEmpty(log);
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(names);
        var found = new List<WindowsEvent>();
        var scanned = 0;
        string? error = null;
        try
        {
            // Отбор по коду и времени делает сам журнал; по имени — здесь, потому что имя живёт в параметрах события.
            var query = new EventLogQuery(log, PathType.LogName,
                $"*[System[({IdFilter(ids)}) and TimeCreated[@SystemTime>='{Xml(from)}' and @SystemTime<='{Xml(to)}']]]");
            using var reader = new EventLogReader(query);
            for (var record = reader.ReadEvent(); record is not null; record = reader.ReadEvent())
            {
                using (record)
                {
                    scanned++;
                    if (Matching(record, log, names) is { } matching)
                    {
                        found.Add(matching);
                    }
                }
            }
        }
        catch (Exception exception) when (exception is EventLogException or UnauthorizedAccessException or InvalidOperationException)
        {
            error = exception.GetType().Name;
        }
        return new WindowsEventsRead(from, to, log, ids, names, scanned, found, error);
    }

    /// <summary>
    /// События после закладки — для опроса наблюдателем (Э6.2, часть Г). Сначала читается номер самой новой записи, потом
    /// события с номером после <paramref name="after"/> и не больше него: событие, записанное между двумя чтениями,
    /// достанется следующему опросу, а не пропадёт. Без закладки — события не старше <paramref name="since"/>.
    /// </summary>
    public static WindowsEventsBatch ReadAfter(string log, IReadOnlyList<int> ids, IReadOnlyList<string> names, long? after, DateTimeOffset since)
    {
        ArgumentException.ThrowIfNullOrEmpty(log);
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(names);
        try
        {
            var newest = Newest(log);
            // Журнал пуст, новых записей нет или он очищен и нумерует заново: последнее решает вызывающий по Newest.
            if (newest is not { } last || after >= last)
            {
                return new WindowsEventsBatch([], newest);
            }
            var range = after is { } bookmark
                ? $"EventRecordID>{bookmark.ToString(CultureInfo.InvariantCulture)}"
                : $"TimeCreated[@SystemTime>='{Xml(since)}']";
            var query = new EventLogQuery(log, PathType.LogName,
                $"*[System[({IdFilter(ids)}) and {range} and EventRecordID<={last.ToString(CultureInfo.InvariantCulture)}]]");
            var found = new List<WindowsEvent>();
            using var reader = new EventLogReader(query);
            for (var record = reader.ReadEvent(); record is not null; record = reader.ReadEvent())
            {
                using (record)
                {
                    if (Matching(record, log, names) is { } matching)
                    {
                        found.Add(matching);
                    }
                }
            }
            return new WindowsEventsBatch(found, newest);
        }
        catch (Exception exception) when (exception is EventLogException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new WindowsEventsBatch([], null, exception.GetType().Name);
        }
    }

    /// <summary>Номер самой новой записи журнала; журнал пуст — <c>null</c>.</summary>
    private static long? Newest(string log)
    {
        using var reader = new EventLogReader(new EventLogQuery(log, PathType.LogName) { ReverseDirection = true });
        using var record = reader.ReadEvent();
        return record?.RecordId;
    }

    /// <summary>Событие, в параметрах которого есть имя одного из образов; иначе <c>null</c>.</summary>
    private static WindowsEvent? Matching(EventRecord record, string log, IReadOnlyList<string> names)
    {
        var properties = record.Properties.Select(p => Convert.ToString(p.Value, CultureInfo.InvariantCulture) ?? "").ToList();
        return WindowsEvent.Mentions(properties, names)
            ? new WindowsEvent(
                record.TimeCreated is { } time ? new DateTimeOffset(time.ToUniversalTime(), TimeSpan.Zero) : default,
                log,
                record.ProviderName,
                record.Id,
                record.RecordId,
                properties)
            : null;
    }

    private static string IdFilter(IReadOnlyList<int> ids) =>
        string.Join(" or ", ids.Select(id => $"EventID={id.ToString(CultureInfo.InvariantCulture)}"));

    private static string Xml(DateTimeOffset time) =>
        time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
