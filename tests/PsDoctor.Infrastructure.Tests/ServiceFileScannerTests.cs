using PsDoctor.Core.Observation;
using PsDoctor.Infrastructure.Observation;
using Xunit;

namespace PsDoctor.Infrastructure.Tests;

/// <summary>Обход мест служебных файлов — на временном каталоге, везде.</summary>
public sealed class ServiceFileScannerTests : IDisposable
{
    private readonly string _корень = Directory.CreateTempSubdirectory("psdoctor-служебные-").FullName;

    public void Dispose() => Directory.Delete(_корень, recursive: true);

    [Fact]
    public void Вглубь_по_маске_и_без_вложенных_отсутствующее_место_тоже_в_снимке()
    {
        var кэш = Directory.CreateDirectory(Path.Combine(_корень, "Photodex", "Transitions", "Cache")).FullName;
        File.WriteAllBytes(Path.Combine(кэш, "transitioncache.dat"), new byte[10]);
        var временный = Directory.CreateDirectory(Path.Combine(_корень, "Temp")).FullName;
        File.WriteAllBytes(Path.Combine(временный, "py1A.tmp"), new byte[3]);
        File.WriteAllBytes(Path.Combine(временный, "чужой.tmp"), new byte[3]);
        Directory.CreateDirectory(Path.Combine(временный, "вложенный"));
        File.WriteAllBytes(Path.Combine(временный, "вложенный", "py2B.tmp"), new byte[3]);
        var нет = Path.Combine(_корень, "нет");

        var снимок = ServiceFileScanner.Take([
            new ServiceFilePlace(Path.Combine(_корень, "Photodex"), true),
            new ServiceFilePlace(временный, false, "py*"),
            new ServiceFilePlace(нет, true),
        ]);

        Assert.Equal(
            [Path.Combine(кэш, "transitioncache.dat"), Path.Combine(временный, "py1A.tmp")],
            снимок.Files.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(10, снимок.Files[Path.Combine(кэш, "transitioncache.dat")].Size);
        Assert.Equal([1, 1, 0], снимок.Places.Select(p => p.Files));
        Assert.Equal([true, true, false], снимок.Places.Select(p => p.Exists));
        Assert.All(снимок.Places, p => Assert.Null(p.Error));
    }

    [Fact]
    public void Разница_двух_снимков_после_записи_дописывания_и_удаления()
    {
        var место = new ServiceFilePlace(_корень, true);
        var phd = Path.Combine(_корень, "proshow.phd");
        var autosave = Path.Combine(_корень, "autosave.psh");
        File.WriteAllBytes(phd, new byte[100]);
        File.WriteAllBytes(autosave, new byte[5]);
        File.SetLastWriteTimeUtc(phd, DateTime.UtcNow.AddMinutes(-5));
        var до = ServiceFileScanner.Take([место]);

        // Переписан тот же размер — как proshow.phd в опыте 03.
        File.WriteAllBytes(phd, new byte[100]);
        File.Delete(autosave);
        File.WriteAllBytes(Path.Combine(_корень, "proshow.cfg"), new byte[7]);
        var после = ServiceFileScanner.Take([место]);

        var (появились, изменились, пропали) = ServiceFileComparison.Compare(до.Files, после.Files);
        Assert.Equal([Path.Combine(_корень, "proshow.cfg")], появились.Select(f => f.Path));
        Assert.Equal([phd], изменились.Select(f => f.Path));
        Assert.Equal([autosave], пропали.Select(f => f.Path));
    }
}
