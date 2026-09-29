using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using PsDoctor.Observer;
using Xunit;

namespace PsDoctor.Observer.Tests;

public sealed class HealthTests : IAsyncLifetime
{
    private const string Ключ = "test-key-0123456789";
    // Свой каталог: на Windows наблюдатель с настоящим запускателем читает журнал Windows и пишет файлы событий, а
    // каталог по умолчанию — журналы установленного Doctor, на стенде живые.
    private readonly string _каталог = Directory.CreateTempSubdirectory("psdoctor-health-").FullName;
    private WebApplication _наблюдатель = null!;
    private HttpClient _клиент = null!;

    public async Task InitializeAsync()
    {
        // Настоящий Kestrel на свободном порту петли: проверяется тот же путь, что на стенде.
        _наблюдатель = ObserverHost.Build(new ObserverOptions(IPAddress.Loopback, 0, Ключ, _каталог));
        await _наблюдатель.StartAsync();
        _клиент = new HttpClient { BaseAddress = new Uri(_наблюдатель.Urls.Single()) };
    }

    public async Task DisposeAsync()
    {
        _клиент.Dispose();
        await _наблюдатель.StopAsync();
        await _наблюдатель.DisposeAsync();
        Directory.Delete(_каталог, recursive: true);
    }

    [Fact]
    public async Task С_ключом_отдаёт_версию_и_коммит_сборки()
    {
        using var запрос = new HttpRequestMessage(HttpMethod.Get, "/health");
        запрос.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Ключ);

        using var ответ = await _клиент.SendAsync(запрос);

        Assert.Equal(HttpStatusCode.OK, ответ.StatusCode);
        var сборка = await ответ.Content.ReadFromJsonAsync<BuildInfo>();
        Assert.Equal(BuildInfo.Of(typeof(ObserverHost).Assembly), сборка);
    }

    [Fact]
    public async Task Без_ключа_отказ()
    {
        using var ответ = await _клиент.GetAsync("/health");

        Assert.Equal(HttpStatusCode.Unauthorized, ответ.StatusCode);
    }

    [Fact]
    public async Task С_чужим_ключом_отказ()
    {
        using var запрос = new HttpRequestMessage(HttpMethod.Get, "/health");
        запрос.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Ключ + "x");

        using var ответ = await _клиент.SendAsync(запрос);

        Assert.Equal(HttpStatusCode.Unauthorized, ответ.StatusCode);
    }
}
