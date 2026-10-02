using System.Text;
using PsDoctor.Core.Observation;
using PsDoctor.Infrastructure.Observation;
using Xunit;

namespace PsDoctor.Infrastructure.Tests;

/// <summary>Рецепт эпизода <c>qtime-loop</c> (Э4.4): галка в действующем <c>proshow.cfg</c>. Путь к файлу подложен, реестра нет.</summary>
public sealed class ProShowVideoImportFixTests : IDisposable
{
    private static readonly DateTime Сейчас = new(2026, 10, 2, 15, 30, 0);

    private readonly string root = Directory.CreateTempSubdirectory("psdoctor-cfg-").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    private string Копии => Path.Combine(root, "backup");

    private string Настройки(string галка)
    {
        var путь = Path.Combine(root, "VirtualStore", ProShowConfig.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(путь)!);
        File.WriteAllBytes(путь,
        [
            0x95, 0x07,
            .. Encoding.ASCII.GetBytes($"cpicName\0ProShow Producer\0prefDShowUseFFMPEG\0{галка}\0prefMemHeadroom\03099648\0"),
        ]);
        return путь;
    }

    private ProShowVideoImportFix Рецепт(string? файл, bool запущен = false) => new(() => файл, Копии, () => запущен, () => Сейчас);

    [Fact]
    public void Галка_отмечается_и_прежний_файл_остаётся_копией()
    {
        var файл = Настройки("0");
        var было = File.ReadAllBytes(файл);

        var итог = Рецепт(файл).Apply();

        Assert.Equal(VideoImportFixOutcome.Fixed, итог.Outcome);
        Assert.Equal(файл, итог.Config);
        Assert.Equal(Path.Combine(Копии, "proshow.cfg.20261002-153000"), итог.Backup);
        Assert.Equal(было, File.ReadAllBytes(итог.Backup!));
        Assert.Equal("1", ProShowConfig.Value(File.ReadAllBytes(файл), ProShowConfig.DShowUseFfmpeg));
        Assert.Equal("1", Рецепт(файл).CurrentValue());
        Assert.Equal(было.Length, new FileInfo(файл).Length);
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(файл)!, "*.tmp"));
    }

    [Fact]
    public void При_запущенном_ProShow_ничего_не_трогается()
    {
        var файл = Настройки("0");
        var было = File.ReadAllBytes(файл);

        var итог = Рецепт(файл, запущен: true).Apply();

        Assert.Equal(VideoImportFixOutcome.ProgramRunning, итог.Outcome);
        Assert.Equal(было, File.ReadAllBytes(файл));
        Assert.False(Directory.Exists(Копии));
    }

    [Fact]
    public void Отмеченная_галка_не_переписывается()
    {
        var файл = Настройки("1");
        var время = File.GetLastWriteTimeUtc(файл);

        var итог = Рецепт(файл).Apply();

        Assert.Equal(VideoImportFixOutcome.AlreadySet, итог.Outcome);
        Assert.Equal(время, File.GetLastWriteTimeUtc(файл));
        Assert.False(Directory.Exists(Копии));
    }

    [Fact]
    public void Неизвестный_файл_оставляет_галку_человеку()
    {
        Assert.Equal(VideoImportFixOutcome.ConfigUnknown, Рецепт(null).Apply().Outcome);
        Assert.Null(Рецепт(null).CurrentValue());
    }

    [Fact]
    public void Файл_без_настройки_не_правится()
    {
        var файл = Path.Combine(root, ProShowConfig.FileName);
        File.WriteAllBytes(файл, [0x95, 0x07, .. Encoding.ASCII.GetBytes("cpicName\0ProShow Producer\0")]);
        var было = File.ReadAllBytes(файл);

        Assert.Equal(VideoImportFixOutcome.ConfigUnknown, Рецепт(файл).Apply().Outcome);
        Assert.Equal(было, File.ReadAllBytes(файл));
    }

    [Fact]
    public void Пропавший_файл_не_пишется_заново()
    {
        var файл = Path.Combine(root, "нет", ProShowConfig.FileName);

        var итог = Рецепт(файл).Apply();

        Assert.Equal(VideoImportFixOutcome.NotWritable, итог.Outcome);
        Assert.False(File.Exists(файл));
    }
}
