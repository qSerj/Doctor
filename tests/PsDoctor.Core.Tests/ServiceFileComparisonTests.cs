using PsDoctor.Core.Observation;
using Xunit;

namespace PsDoctor.Core.Tests;

public sealed class ServiceFileComparisonTests
{
    private static readonly DateTimeOffset Было = new(2026, 9, 16, 8, 47, 16, TimeSpan.Zero);
    private static readonly DateTimeOffset Стало = Было.AddSeconds(30);

    [Fact]
    public void Появившийся_изменённый_и_пропавший_файлы_видны_нетронутый_нет()
    {
        // Как в опыте 03: кэш переходов вырос, кэш .phd переписан того же размера, автосохранение пропало.
        var до = new Dictionary<string, ServiceFileState>
        {
            [@"C:\ProgramData\Photodex\ProShow\Transitions\Cache\transitioncache.dat"] = new(1580454, Было),
            [@"C:\VirtualStore\proshow.phd"] = new(464026, Было),
            [@"C:\VirtualStore\autosave.psh"] = new(1200, Было),
            [@"C:\Photodex\proshow.exe"] = new(9000000, Было),
        };
        var после = new Dictionary<string, ServiceFileState>
        {
            [@"C:\ProgramData\Photodex\ProShow\Transitions\Cache\transitioncache.dat"] = new(1580897, Стало),
            [@"C:\VirtualStore\proshow.phd"] = new(464026, Стало),
            [@"C:\Photodex\proshow.exe"] = new(9000000, Было),
            [@"C:\Temp\py1A.tmp"] = new(0, Стало),
        };

        var (появились, изменились, пропали) = ServiceFileComparison.Compare(до, после);

        Assert.Equal([@"C:\Temp\py1A.tmp"], появились.Select(f => f.Path));
        Assert.Equal([@"C:\ProgramData\Photodex\ProShow\Transitions\Cache\transitioncache.dat", @"C:\VirtualStore\proshow.phd"], изменились.Select(f => f.Path));
        Assert.Equal(new ServiceFileChange(@"C:\VirtualStore\proshow.phd", 464026, 464026, Было, Стало), изменились[1]);
        Assert.Equal([new ServiceFileEntry(@"C:\VirtualStore\autosave.psh", 1200, Было)], пропали);
    }

    [Fact]
    public void Одинаковые_снимки_разницы_не_дают()
    {
        var снимок = new Dictionary<string, ServiceFileState> { ["a"] = new(1, Было) };

        var (появились, изменились, пропали) = ServiceFileComparison.Compare(снимок, new Dictionary<string, ServiceFileState>(снимок));

        Assert.Empty(появились);
        Assert.Empty(изменились);
        Assert.Empty(пропали);
    }

    [Fact]
    public void Время_в_другом_часовом_поясе_то_же_мгновение_не_изменение()
    {
        var до = new Dictionary<string, ServiceFileState> { ["a"] = new(1, Было) };
        var после = new Dictionary<string, ServiceFileState> { ["a"] = new(1, Было.ToOffset(TimeSpan.FromHours(3))) };

        Assert.Empty(ServiceFileComparison.Compare(до, после).Changed);
    }
}
