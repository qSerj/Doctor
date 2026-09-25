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
        Assert.Equal(InstalledSettings.Default, настройки);
        Assert.Equal(new Uri("http://127.0.0.1:8100/"), настройки!.LocalUrl);
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
    public void Сетевой_адрес_без_allowRemote_отвергается()
    {
        var (настройки, ошибка) = InstalledSettings.Parse("""{ "listen": "0.0.0.0:8100" }""");

        Assert.Null(настройки);
        Assert.Contains("allowRemote", ошибка!, StringComparison.Ordinal);
    }

    [Fact]
    public void Сетевой_адрес_с_allowRemote_виден_с_этой_машины_через_петлю()
    {
        var (настройки, _) = InstalledSettings.Parse("""{ "listen": "0.0.0.0:8100", "allowRemote": true }""");

        Assert.Equal(new Uri("http://127.0.0.1:8100/"), настройки!.LocalUrl);
    }

    [Theory]
    [InlineData("не json")]
    [InlineData("""{ "listen": "петля" }""")]
    [InlineData("""{ "watchdog": { "misses": 0 } }""")]
    [InlineData("""{ "retention": { "days": -1 } }""")]
    public void Испорченные_настройки_ошибка_а_не_умолчания(string текст)
    {
        var (настройки, ошибка) = InstalledSettings.Parse(текст);

        Assert.Null(настройки);
        Assert.NotNull(ошибка);
    }
}
