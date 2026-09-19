using System.Runtime.Versioning;
using PsDoctor.Infrastructure.Observation;
using Xunit;

namespace PsDoctor.Infrastructure.Tests;

[SupportedOSPlatform("windows")]
public sealed class ProShowCacheCleanerTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("psdoctor-cache-").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void Находит_только_кэш_выбранного_проекта_и_две_копии_phd()
    {
        var (cleaner, show, projectCache, virtualCache, installedCache) = Arrange(() => false);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(show)!, "other.pxc"), "чужой проект");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(installedCache)!, "proshow.cfg"), "настройки");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(virtualCache)!, "autosave.psh"), "автосохранение");

        Assert.Equal([projectCache, virtualCache, installedCache], cleaner.Find(show).Select(item => item.Path));
        Assert.Equal([virtualCache, installedCache], cleaner.Find(null).Select(item => item.Path));
    }

    [Fact]
    public void Переименовывает_кэши_сохраняет_байты_и_не_трогает_проект()
    {
        var (cleaner, show, projectCache, virtualCache, installedCache) = Arrange(() => false);
        var originalShow = File.ReadAllBytes(show);

        var result = cleaner.Clean(show);

        Assert.False(result.ProgramRunning);
        Assert.Empty(result.Failed);
        Assert.Equal([projectCache, virtualCache, installedCache], result.Saved.Select(item => item.OriginalPath));
        Assert.Equal(originalShow, File.ReadAllBytes(show));
        foreach (var item in result.Saved)
        {
            Assert.False(File.Exists(item.OriginalPath));
            Assert.True(File.Exists(item.SavedPath));
            Assert.Equal("кэш", File.ReadAllText(item.SavedPath));
        }
    }

    [Fact]
    public void Не_трогает_файлы_если_ProShow_работает_или_запустился_перед_переименованием()
    {
        var (busyCleaner, show, projectCache, _, _) = Arrange(() => true);
        Assert.True(busyCleaner.Clean(show).ProgramRunning);
        Assert.True(File.Exists(projectCache));

        var checks = 0;
        var programPath = Path.Combine(root, "Program Files", "Photodex", "ProShow Producer", "proshow.exe");
        var lateCleaner = new ProShowCacheCleaner(programPath, Path.Combine(root, "Local"), () => ++checks > 1);
        Assert.True(lateCleaner.Clean(show).ProgramRunning);
        Assert.True(File.Exists(projectCache));
    }

    private (ProShowCacheCleaner Cleaner, string Show, string ProjectCache, string VirtualCache, string InstalledCache)
        Arrange(Func<bool> running)
    {
        var program = Path.Combine(root, "Program Files", "Photodex", "ProShow Producer", "proshow.exe");
        var programDirectory = Path.GetDirectoryName(program)!;
        Directory.CreateDirectory(programDirectory);
        var show = Path.Combine(root, "Project", "show.psh");
        Directory.CreateDirectory(Path.GetDirectoryName(show)!);
        File.WriteAllText(show, "проект");
        var projectCache = Path.ChangeExtension(show, ".pxc");
        var local = Path.Combine(root, "Local");
        var virtualCache = Path.Combine(local, "VirtualStore",
            Path.GetRelativePath(Path.GetPathRoot(programDirectory)!, programDirectory), "proshow.phd");
        var installedCache = Path.Combine(programDirectory, "proshow.phd");
        foreach (var path in new[] { projectCache, virtualCache, installedCache })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "кэш");
        }
        return (new ProShowCacheCleaner(program, local, running), show, projectCache, virtualCache, installedCache);
    }
}
