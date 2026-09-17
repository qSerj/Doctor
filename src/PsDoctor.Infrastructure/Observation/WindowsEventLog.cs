using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Runtime.Versioning;
using PsDoctor.Core.Observation;

namespace PsDoctor.Infrastructure.Observation;

/// <summary>
/// События журнала Windows за отрезок времени, отобранные по имени образа в параметрах события. Так в опыте 08
/// нашлись отчёты <c>RADAR_PRE_LEAK_WOW64</c> о программе, которые сама программа не сообщает.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsEventLog
{
    public const string Application = "Application";

    /// <summary>Падение приложения, отчёт Windows Error Reporting, зависание приложения — как в опытах 08 и 09.</summary>
    public static readonly IReadOnlyList<int> CrashIds = [1000, 1001, 1002];

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
            var idFilter = string.Join(" or ", ids.Select(id => $"EventID={id.ToString(CultureInfo.InvariantCulture)}"));
            var query = new EventLogQuery(log, PathType.LogName,
                $"*[System[({idFilter}) and TimeCreated[@SystemTime>='{Xml(from)}' and @SystemTime<='{Xml(to)}']]]");
            using var reader = new EventLogReader(query);
            for (var record = reader.ReadEvent(); record is not null; record = reader.ReadEvent())
            {
                using (record)
                {
                    scanned++;
                    var properties = record.Properties.Select(p => Convert.ToString(p.Value, CultureInfo.InvariantCulture) ?? "").ToList();
                    if (properties.Any(value => names.Any(name => value.Contains(name, StringComparison.OrdinalIgnoreCase))))
                    {
                        found.Add(new WindowsEvent(
                            record.TimeCreated is { } time ? new DateTimeOffset(time.ToUniversalTime(), TimeSpan.Zero) : default,
                            log,
                            record.ProviderName,
                            record.Id,
                            record.RecordId,
                            properties));
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

    private static string Xml(DateTimeOffset time) =>
        time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
