using PsDoctor.Core.Observation;
using Xunit;

namespace PsDoctor.Observer.Tests;

/// <summary>Слепки окружения рядом с журналами (Э6.3, часть Б) на подменённом запуске.</summary>
public sealed class EnvironmentFilesTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Снят = new(2026, 9, 30, 18, 0, 0, TimeSpan.Zero);

    private static readonly EnvironmentFacts Окружение = new("10.0.19045.4894", "22H2", @"C:\ProShow\proshow.exe", true, "1, 0, 0, 1",
        false, 1, null, [], 16L << 30, 8L << 30, null, null, "9,00,0,3797");

    private readonly string _каталог = Directory.CreateTempSubdirectory("psdoctor-слепок-").FullName;

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        Directory.Delete(_каталог, recursive: true);
        return Task.CompletedTask;
    }

    private static EnvironmentSnapshot Слепок(DateTimeOffset снят, string merit = "0x00800003") =>
        EnvironmentSnapshot.Create(снят, 1.5,
        [
            new EnvironmentEntry(EnvironmentSections.DirectShow, 32, "{CAT}/{AAA}",
                new Dictionary<string, string?> { ["name"] = "LAV Video Decoder", ["merit"] = merit },
                new EnvironmentFile(@"C:\Windows\SysWOW64\lav.ax", true, "0.79.2.0", 1000, Снят)),
        ]);

    [Fact]
    public void Слепок_ложится_один_раз_повтор_того_же_не_переписывает()
    {
        var первый = Слепок(Снят);
        var тот_же_позже = Слепок(Снят.AddHours(5));

        Assert.True(EnvironmentFiles.Save(_каталог, первый));
        Assert.True(EnvironmentFiles.Save(_каталог, тот_же_позже));

        Assert.Equal(первый.Id, тот_же_позже.Id);
        Assert.Single(Directory.GetFiles(EnvironmentFiles.Directory(_каталог)));
        Assert.Equal(Снят, EnvironmentFiles.Load(_каталог, первый.Id)!.Taken);
    }

    [Fact]
    public void Другой_слепок_ложится_рядом_и_читается_целым()
    {
        var до = Слепок(Снят);
        var после = Слепок(Снят, merit: "0xFF800001");

        EnvironmentFiles.Save(_каталог, до);
        EnvironmentFiles.Save(_каталог, после);

        Assert.Equal(2, Directory.GetFiles(EnvironmentFiles.Directory(_каталог)).Length);
        var прочитан = EnvironmentFiles.Load(_каталог, после.Id)!;
        Assert.Equal(после.Id, EnvironmentSnapshot.ComputeId(прочитан.Entries));
        var изменение = Assert.Single(EnvironmentComparison.Compare(до, прочитан).Changed);
        Assert.Equal(new EnvironmentFieldChange("merit", "0x00800003", "0xFF800001"), Assert.Single(изменение.Fields));
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("ABCDEF0123456789")]
    [InlineData("0123")]
    [InlineData("0000000000000000")]
    public void Негодный_или_незнакомый_идентификатор_даёт_null(string id)
    {
        Assert.Null(EnvironmentFiles.Load(_каталог, id));
    }

    [Fact]
    public async Task Подключение_кладёт_слепок_и_факт_environment_несёт_его_идентификатор()
    {
        var слепок = Слепок(Снят);
        var запуск = new FakeLauncher { Foreign = true, Environment = Окружение, Snapshot = слепок };
        await using var служба = new ObservationService(_каталог, запуск, new ObserverHealth("test", null));

        var (принято, _, ошибка) = служба.Attach();

        Assert.Null(ошибка);
        var факты = служба.Live(принято!.Session)!.After(0);
        Assert.Equal(ProgramFactKinds.Environment, факты[1].Kind);
        Assert.Equal(слепок.Id, факты[1].Data.GetProperty("snapshot").GetString());
        Assert.True(File.Exists(EnvironmentFiles.Snapshot(_каталог, слепок.Id)));
    }

    [Fact]
    public async Task Без_слепка_факт_environment_прежний()
    {
        var запуск = new FakeLauncher { Foreign = true, Environment = Окружение };
        await using var служба = new ObservationService(_каталог, запуск, new ObserverHealth("test", null));

        var (принято, _, _) = служба.Attach();

        var факты = служба.Live(принято!.Session)!.After(0);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, факты[1].Data.GetProperty("snapshot").ValueKind);
        Assert.False(Directory.Exists(EnvironmentFiles.Directory(_каталог)));
    }
}
