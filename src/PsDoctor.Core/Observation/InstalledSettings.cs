using System.Net;
using System.Text.Json;

namespace PsDoctor.Core.Observation;

/// <summary>Как сторож следит за наблюдателем.</summary>
/// <param name="Poll">Пауза между опросами <c>/health</c>.</param>
/// <param name="Timeout">Сколько ждать ответа на один опрос.</param>
/// <param name="Misses">Сколько опросов подряд без ответа — и наблюдатель перезапускается.</param>
public sealed record WatchdogTiming(TimeSpan Poll, TimeSpan Timeout, int Misses);

/// <summary>
/// Настройки установленного Doctor — файл <c>settings.json</c>, который пишет установщик и правит инженер.
/// Отсутствующее поле берёт значение по умолчанию: старый файл переживает новую версию без правки.
/// </summary>
/// <param name="Listen">Где слушает наблюдатель. По умолчанию все адреса машины: инженер подключается из локальной сети.</param>
/// <param name="AllowNetworks">Сети, из которых пускают кроме петли. Пусто — только петля, и тогда <paramref name="Listen"/> обязан быть петлёй.</param>
/// <param name="Watch">
/// Дежурство: наблюдатель сам подключается к каждому запущенному ProShow. Нет поля — выключено; шаблон установщика
/// его включает (Э6.2).
/// </param>
public sealed record InstalledSettings(string Listen, IReadOnlyList<IPNetwork> AllowNetworks, WatchdogTiming Watchdog, RetentionLimits Retention,
    bool Watch = false)
{
    /// <summary>
    /// Первые пределы хранения — из веса сеанса рендера в <c>e43-render-002</c>: 12,5 мин рендера — журнал 12,7 МБ
    /// и сырьё ETW около 1,3 МБ в минуту, всего около 30 МБ. 2 ГБ — десятки таких сеансов.
    /// </summary>
    public static InstalledSettings Default { get; } = new(
        "0.0.0.0:8100",
        [IPNetwork.Parse("192.168.0.0/24")],
        new WatchdogTiming(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), 3),
        new RetentionLimits(TimeSpan.FromDays(30), 2048L * 1024 * 1024, TimeSpan.FromDays(180)));

    /// <summary>Адрес, по которому к наблюдателю ходят с этой же машины.</summary>
    public Uri LocalUrl
    {
        get
        {
            var colon = Listen.LastIndexOf(':');
            var host = Listen[..colon];
            if (IPAddress.TryParse(host, out var address) && (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)))
            {
                host = "127.0.0.1";
            }
            return new Uri($"http://{host}:{Listen[(colon + 1)..]}/");
        }
    }

    /// <summary>Разбирает текст файла. Ошибка — строка для человека, а не исключение.</summary>
    public static (InstalledSettings? Settings, string? Error) Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Dto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<Dto>(text, Json);
        }
        catch (JsonException e)
        {
            return (null, $"настройки не JSON: {e.Message}");
        }
        if (dto is null)
        {
            return (null, "настройки пусты");
        }

        var d = Default;
        var listen = dto.Listen ?? d.Listen;
        var colon = listen.LastIndexOf(':');
        if (colon <= 0 || !IPAddress.TryParse(listen[..colon], out var address)
            || !int.TryParse(listen[(colon + 1)..], out var port) || port is < 1 or > 65535)
        {
            return (null, $"listen: ожидается адрес:порт, получено «{listen}»");
        }
        var networks = new List<IPNetwork>();
        foreach (var networkText in dto.AllowNetworks ?? [.. d.AllowNetworks.Select(n => n.ToString())])
        {
            var (network, error) = RemoteAccess.ParseNetwork(networkText);
            if (network is null)
            {
                return (null, $"allowNetworks: {error}");
            }
            networks.Add(network.Value);
        }
        if (!IPAddress.IsLoopback(address) && networks.Count == 0)
        {
            return (null, $"listen {listen}: не-петлевой адрес открывается только с непустым allowNetworks");
        }

        var poll = dto.Watchdog?.PollSeconds ?? d.Watchdog.Poll.TotalSeconds;
        var timeout = dto.Watchdog?.TimeoutSeconds ?? d.Watchdog.Timeout.TotalSeconds;
        var misses = dto.Watchdog?.Misses ?? d.Watchdog.Misses;
        if (poll <= 0 || timeout <= 0 || misses < 1)
        {
            return (null, "watchdog: pollSeconds и timeoutSeconds больше нуля, misses не меньше 1");
        }

        var days = dto.Retention?.Days ?? d.Retention.Age.TotalDays;
        var megabytes = dto.Retention?.Megabytes ?? d.Retention.Bytes / (1024 * 1024);
        var markedDays = dto.Retention?.MarkedDays ?? d.Retention.MarkedAge.TotalDays;
        if (days <= 0 || megabytes <= 0 || markedDays <= 0)
        {
            return (null, "retention: days, megabytes и markedDays больше нуля");
        }

        return (new InstalledSettings(
            listen,
            networks,
            new WatchdogTiming(TimeSpan.FromSeconds(poll), TimeSpan.FromSeconds(timeout), misses),
            new RetentionLimits(TimeSpan.FromDays(days), (long)(megabytes * 1024 * 1024), TimeSpan.FromDays(markedDays)),
            dto.Watch ?? d.Watch), null);
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed record Dto(string? Listen, string[]? AllowNetworks, WatchdogDto? Watchdog, RetentionDto? Retention, bool? Watch);

    private sealed record WatchdogDto(double? PollSeconds, double? TimeoutSeconds, int? Misses);

    private sealed record RetentionDto(double? Days, double? Megabytes, double? MarkedDays);
}
