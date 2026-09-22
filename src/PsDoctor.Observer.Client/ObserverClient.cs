using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PsDoctor.Core.Observation;
using PsDoctor.Core.Scenarios;

namespace PsDoctor.Observer.Client;

/// <summary>
/// Типизированный доступ к наблюдателю. Им пользуются CLI и пульт; окно Ольги потом станет ещё одним клиентом.
/// </summary>
/// <remarks>
/// Потоки фактов не ограничены по времени: рендер идёт часами. Короткие запросы ограничены <see cref="RequestTimeout"/>.
/// Отказ наблюдателя с известным именем — <see cref="ObserverException"/> с этим именем; сеть и чужие ответы — как есть.
/// </remarks>
public sealed class ObserverClient : IDisposable
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient http;

    public ObserverClient(Uri baseAddress, string key, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentException.ThrowIfNullOrEmpty(key);
        http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.BaseAddress = baseAddress;
        http.Timeout = Timeout.InfiniteTimeSpan;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public void Dispose() => http.Dispose();

    public async Task<ObserverHealth> HealthAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = Limit(cancellationToken);
        using var response = await http.GetAsync(ObserverRoutes.Health, timeout.Token).ConfigureAwait(false);
        return await ReadAsync<ObserverHealth>(response, timeout.Token).ConfigureAwait(false);
    }

    /// <summary>Отдаёт сценарий на выполнение. Итог сценария — в фактах сеанса после <see cref="RunScenarioAccepted.After"/>.</summary>
    public async Task<RunScenarioAccepted> RunAsync(string scenario, CancellationToken cancellationToken = default)
    {
        using var timeout = Limit(cancellationToken);
        using var response = await http.PostAsJsonAsync(ObserverRoutes.Scenarios, new RunScenarioRequest(scenario), ObservationJson.Options, timeout.Token)
            .ConfigureAwait(false);
        return await ReadAsync<RunScenarioAccepted>(response, timeout.Token).ConfigureAwait(false);
    }

    /// <summary>Подключается к уже работающему ProShow для пассивной диагностики.</summary>
    public async Task<AttachAccepted> AttachAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = Limit(cancellationToken);
        using var response = await http.PostAsync(ObserverRoutes.Attach, null, timeout.Token).ConfigureAwait(false);
        return await ReadAsync<AttachAccepted>(response, timeout.Token).ConfigureAwait(false);
    }
    public async Task<CancelAccepted> CancelAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = Limit(cancellationToken);
        using var response = await http.PostAsync(ObserverRoutes.CancelScenario, null, timeout.Token).ConfigureAwait(false);
        return await ReadAsync<CancelAccepted>(response, timeout.Token).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SessionSummary>> SessionsAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = Limit(cancellationToken);
        using var response = await http.GetAsync(ObserverRoutes.Sessions, timeout.Token).ConfigureAwait(false);
        return await ReadAsync<List<SessionSummary>>(response, timeout.Token).ConfigureAwait(false);
    }

    /// <summary>Открытые диалоги живого сеанса с текстами и кнопками.</summary>
    public async Task<IReadOnlyList<DialogInfo>> DialogsAsync(string session, CancellationToken cancellationToken = default)
    {
        using var timeout = Limit(cancellationToken);
        using var response = await http.GetAsync(ObserverRoutes.Dialogs(session), timeout.Token).ConfigureAwait(false);
        return await ReadAsync<List<DialogInfo>>(response, timeout.Token).ConfigureAwait(false);
    }

    /// <summary>Прекращает наблюдение. Программа не закрывается.</summary>
    public async Task StopAsync(string session, CancellationToken cancellationToken = default)
    {
        using var timeout = Limit(cancellationToken);
        using var response = await http.PostAsync(ObserverRoutes.Stop(session), null, timeout.Token).ConfigureAwait(false);
        await ReadAsync<CancelAccepted>(response, timeout.Token).ConfigureAwait(false);
    }

    /// <summary>Скачивает сырьё ETW за UTC-отрезок без ограничения длительности запроса.</summary>
    public async Task DownloadRawAsync(string session, DateTime fromUtc, DateTime toUtc, Stream destination,
        CancellationToken cancellationToken = default)
    {
        var path = ObserverRoutes.Raw(session) + "?from=" +
            Uri.EscapeDataString(fromUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)) +
            "&to=" + Uri.EscapeDataString(toUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        using var response = await SendForStreamAsync(path, "application/x-ndjson", cancellationToken).ConfigureAwait(false);
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Скачивает весь сохранённый кольцевой буфер ETW сеанса.</summary>
    public async Task DownloadRawAsync(string session, Stream destination, CancellationToken cancellationToken = default)
    {
        using var response = await SendForStreamAsync(ObserverRoutes.RawAll(session), "application/x-ndjson", cancellationToken)
            .ConfigureAwait(false);
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SessionArtifact>> ArtifactsAsync(string session, CancellationToken cancellationToken = default)
    {
        using var timeout = Limit(cancellationToken);
        using var response = await http.GetAsync(ObserverRoutes.Artifacts(session), timeout.Token).ConfigureAwait(false);
        return await ReadAsync<List<SessionArtifact>>(response, timeout.Token).ConfigureAwait(false);
    }

    /// <summary>Скачивает журнал фактов без разбора и повторной сериализации.</summary>
    public async Task DownloadFactsAsync(string session, Stream destination, CancellationToken cancellationToken = default)
    {
        using var response = await SendForStreamAsync(ObserverRoutes.Facts(session) + "?after=0", "application/x-ndjson", cancellationToken)
            .ConfigureAwait(false);
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Скачивает артефакт во временный файл вызывающей стороны.</summary>
    public async Task DownloadArtifactAsync(string session, string id, Stream destination,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendForStreamAsync(ObserverRoutes.Artifact(session, id), "application/octet-stream", cancellationToken)
            .ConfigureAwait(false);
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
    }
    /// <summary>Журнал сеанса, сколько есть на момент запроса, с номера.</summary>
    public async IAsyncEnumerable<Fact> ReadFactsAsync(
        string session,
        long after = 0,
        IReadOnlyCollection<string>? kinds = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var response = await SendForStreamAsync(Query(ObserverRoutes.Facts(session), after, kinds), null, cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), Encoding.UTF8);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length > 0)
            {
                yield return ParseFact(line);
            }
        }
    }

    /// <summary>
    /// Поток фактов сеанса с номера: сначала всё, что уже есть, потом новые по мере записи. Кончается, когда
    /// сеанс закрыт и всё отдано. Оборванное соединение — исключение; продолжать — с номера последнего полученного.
    /// </summary>
    public async IAsyncEnumerable<Fact> StreamAsync(
        string session,
        long after = 0,
        IReadOnlyCollection<string>? kinds = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var response = await SendForStreamAsync(Query(ObserverRoutes.Stream(session), after, kinds), "text/event-stream", cancellationToken)
            .ConfigureAwait(false);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), Encoding.UTF8);

        string? kind = null;
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                if (kind == "end")
                {
                    yield break;
                }
                if (kind == "fact" && data.Length > 0)
                {
                    yield return ParseFact(data.ToString());
                }
                kind = null;
                data.Clear();
                continue;
            }
            if (line[0] == ':')
            {
                continue;
            }
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..].TrimStart(' ');
            switch (field)
            {
                case "event":
                    kind = value;
                    break;
                case "data":
                    if (data.Length > 0)
                    {
                        data.Append('\n');
                    }
                    data.Append(value);
                    break;
            }
        }
        throw new ObserverStreamEndedException();
    }

    private static string Query(string path, long after, IReadOnlyCollection<string>? kinds)
    {
        var query = new StringBuilder(path).Append("?after=").Append(after.ToString(CultureInfo.InvariantCulture));
        if (kinds is { Count: > 0 })
        {
            query.Append("&kind=").Append(Uri.EscapeDataString(string.Join(',', kinds)));
        }
        return query.ToString();
    }

    private async Task<HttpResponseMessage> SendForStreamAsync(string path, string? accept, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (accept is not null)
        {
            request.Headers.Accept.ParseAdd(accept);
        }
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            using (response)
            {
                throw await ErrorAsync(response, cancellationToken).ConfigureAwait(false);
            }
        }
        return response;
    }

    private static Fact ParseFact(string json) =>
        JsonSerializer.Deserialize<Fact>(json, ObservationJson.Options)
        ?? throw new JsonException("пустой факт");

    private static CancellationTokenSource Limit(CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(RequestTimeout);
        return source;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw await ErrorAsync(response, cancellationToken).ConfigureAwait(false);
        }
        return await response.Content.ReadFromJsonAsync<T>(ObservationJson.Options, cancellationToken).ConfigureAwait(false)
            ?? throw new JsonException("пустой ответ наблюдателя");
    }

    private static async Task<ObserverException> ErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        ObserverError? error = null;
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            try
            {
                error = await response.Content.ReadFromJsonAsync<ObserverError>(ObservationJson.Options, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException)
            {
            }
        }
        return new ObserverException(response.StatusCode, error);
    }
}

/// <summary>Наблюдатель отказал. <see cref="Error"/> — тело отказа, если оно было; у 401 его нет.</summary>
public sealed class ObserverException(HttpStatusCode status, ObserverError? error)
    : Exception($"наблюдатель ответил {(int)status}{(error is null ? "" : ": " + error.Error)}")
{
    public HttpStatusCode Status { get; } = status;

    public ObserverError? Error { get; } = error;
}

/// <summary>Соединение закрылось без события <c>end</c>: сеанс не кончился, поток оборван.</summary>
public sealed class ObserverStreamEndedException() : IOException("поток фактов оборван до конца сеанса");
