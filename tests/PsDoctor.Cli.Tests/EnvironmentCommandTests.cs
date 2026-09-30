using System.Text.Json;
using PsDoctor.Cli;
using PsDoctor.Core.Observation;
using Xunit;

namespace PsDoctor.Cli.Tests;

/// <summary><c>psdoctor environment diff</c> на файлах слепков: без наблюдателя и на любой ОС.</summary>
public sealed class EnvironmentCommandTests : IDisposable
{
    private static readonly DateTimeOffset Снят = new(2026, 9, 30, 18, 0, 0, TimeSpan.Zero);

    private readonly string _каталог = Directory.CreateTempSubdirectory("psdoctor-diff-").FullName;

    public void Dispose() => Directory.Delete(_каталог, recursive: true);

    private static EnvironmentEntry Фильтр(string clsid, string имя, string merit) =>
        new(EnvironmentSections.DirectShow, 32, $"{{CAT}}/{clsid}",
            new Dictionary<string, string?> { ["name"] = имя, ["merit"] = merit },
            new EnvironmentFile($@"C:\Windows\SysWOW64\{имя}.ax", true, "1.0", 1000, Снят));

    private string Файл(string имя, params EnvironmentEntry[] записи)
    {
        var путь = Path.Combine(_каталог, имя);
        File.WriteAllText(путь, EnvironmentSnapshotJson.Serialize(EnvironmentSnapshot.Create(Снят, 1, записи)));
        return путь;
    }

    private static (int Code, string[] Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = EnvironmentCommand.Run(args, stdout, stderr);
        return (code, stdout.ToString().Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries), stderr.ToString());
    }

    [Fact]
    public void Одинаковые_слепки_ноль_и_строка_итога()
    {
        var стенд = Файл("стенд.json", Фильтр("{AAA}", "LAV", "0x00800003"));

        var (code, stdout, _) = Run("diff", стенд, стенд);

        Assert.Equal(EnvironmentExitCodes.Same, code);
        Assert.Contains("добавлено 0, убрано 0, изменено 0", Assert.Single(stdout), StringComparison.Ordinal);
    }

    [Fact]
    public void Разные_слепки_единица_и_записи_по_знакам()
    {
        var стенд = Файл("стенд.json", Фильтр("{AAA}", "LAV", "0x00800003"), Фильтр("{BBB}", "DTV", "0x005FFFFF"));
        var ольга = Файл("ольга.json", Фильтр("{AAA}", "LAV", "0xFF800001"), Фильтр("{CCC}", "ffdshow", "0xFF800000"));

        var (code, stdout, _) = Run("diff", стенд, ольга);

        Assert.Equal(EnvironmentExitCodes.Different, code);
        Assert.Contains("добавлено 1, убрано 1, изменено 1", stdout[0], StringComparison.Ordinal);
        Assert.Contains(stdout, line => line.StartsWith("+ directshow 32 {CAT}/{CCC}", StringComparison.Ordinal) && line.Contains("name=ffdshow", StringComparison.Ordinal));
        Assert.Contains(stdout, line => line.StartsWith("- directshow 32 {CAT}/{BBB}", StringComparison.Ordinal));
        Assert.Contains(stdout, line => line == "~ directshow 32 {CAT}/{AAA}");
        Assert.Contains(stdout, line => line.Trim() == "merit: 0x00800003 → 0xFF800001");
    }

    [Fact]
    public void Json_разница_одной_строкой()
    {
        var стенд = Файл("стенд.json", Фильтр("{AAA}", "LAV", "0x00800003"));
        var ольга = Файл("ольга.json");

        var (code, stdout, _) = Run("diff", стенд, ольга, "--json");

        Assert.Equal(EnvironmentExitCodes.Different, code);
        var разница = JsonSerializer.Deserialize<EnvironmentDiff>(Assert.Single(stdout), ObservationJson.Options)!;
        Assert.Equal("{CAT}/{AAA}", Assert.Single(разница.Removed).Key);
    }

    [Fact]
    public void Не_слепок_или_нет_файла_ошибка()
    {
        var мусор = Path.Combine(_каталог, "мусор.json");
        File.WriteAllText(мусор, "не json");

        Assert.Equal(EnvironmentExitCodes.Error, Run("diff", мусор, мусор).Code);
        Assert.Equal(EnvironmentExitCodes.Error, Run("diff", Path.Combine(_каталог, "нет.json"), мусор).Code);
        Assert.Equal(EnvironmentExitCodes.Error, Run("diff", мусор).Code);
    }
}
