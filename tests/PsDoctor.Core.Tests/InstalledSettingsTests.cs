using System.Net;
using PsDoctor.Core.Observation;
using Xunit;

namespace PsDoctor.Core.Tests;

public sealed class InstalledSettingsTests
{
    [Fact]
    public void Пустой_объект_даёт_значения_по_умолчанию()
    {
        var (настройки, ошибка) = InstalledSettings.Parse("{}");

        Assert.Null(ошибка);
        var d = InstalledSettings.Default;
        Assert.Equal(d.Listen, настройки!.Listen);
        Assert.Equal(d.AllowNetworks, настройки.AllowNetworks);
        Assert.Equal(d.Watchdog, настройки.Watchdog);
        Assert.Equal(d.Retention, настройки.Retention);
        Assert.Equal(new Uri("http://127.0.0.1:8100/"), настройки.LocalUrl);
    }

    [Fact]
    public void По_умолчанию_слушает_сеть_и_пускает_192_168_0()
    {
        var d = InstalledSettings.Default;

        Assert.Equal("0.0.0.0:8100", d.Listen);
        Assert.Equal([IPNetwork.Parse("192.168.0.0/24")], d.AllowNetworks);
    }

    [Fact]
    public void Файл_установщика_с_комментариями_читается()
    {
        var (настройки, ошибка) = InstalledSettings.Parse("""
            {
              // адрес
              "listen": "127.0.0.1:8200",
              "watchdog": { "pollSeconds": 2, "timeoutSeconds": 1, "misses": 2 },
              "retention": { "days": 1, "megabytes": 0.5, "markedDays": 7 },
            }
            """);

        Assert.Null(ошибка);
        Assert.Equal(new WatchdogTiming(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), 2), настройки!.Watchdog);
        Assert.Equal(new RetentionLimits(TimeSpan.FromDays(1), 512 * 1024, TimeSpan.FromDays(7)), настройки.Retention);
        Assert.Equal(new Uri("http://127.0.0.1:8200/"), настройки.LocalUrl);
    }

    [Fact]
    public void Сетевой_адрес_с_пустым_списком_сетей_отвергается()
    {
        var (настройки, ошибка) = InstalledSettings.Parse("""{ "listen": "0.0.0.0:8100", "allowNetworks": [] }""");

        Assert.Null(настройки);
        Assert.Contains("allowNetworks", ошибка!, StringComparison.Ordinal);
    }

    [Fact]
    public void Петля_с_пустым_списком_сетей_допустима()
    {
        var (настройки, ошибка) = InstalledSettings.Parse("""{ "listen": "127.0.0.1:8100", "allowNetworks": [] }""");

        Assert.Null(ошибка);
        Assert.Empty(настройки!.AllowNetworks);
    }

    [Fact]
    public void Список_сетей_из_файла_заменяет_умолчание()
    {
        var (настройки, _) = InstalledSettings.Parse("""{ "allowNetworks": ["192.168.1.0/24", "10.0.0.0/8"] }""");

        Assert.Equal([IPNetwork.Parse("192.168.1.0/24"), IPNetwork.Parse("10.0.0.0/8")], настройки!.AllowNetworks);
    }

    [Fact]
    public void Прежний_allowRemote_не_мешает_чтению()
    {
        var (настройки, ошибка) = InstalledSettings.Parse("""{ "listen": "127.0.0.1:8100", "allowRemote": false }""");

        Assert.Null(ошибка);
        Assert.Equal("127.0.0.1:8100", настройки!.Listen);
    }
}
