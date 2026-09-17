using PsDoctor.Core.Observation;
using Xunit;

namespace PsDoctor.Core.Tests;

public sealed class FactLogTests
{
    private static (FactLog Log, StringWriter Disk) NewLog()
    {
        var disk = new StringWriter();
        return (new FactLog(new FactJournalWriter(disk, "s", () => TimeSpan.Zero)), disk);
    }

    [Fact]
    public void Хвост_после_номера_тот_же_что_в_журнале_на_диске()
    {
        var (log, disk) = NewLog();
        for (var i = 0; i < 5; i++)
        {
            log.Record("k", new { i });
        }

        var сДиска = FactJournalReader.ReadAfter(new StringReader(disk.ToString()), 2).Select(f => f.Number);

        Assert.Equal(сДиска, log.After(2).Select(f => f.Number));
        Assert.Equal([3L, 4, 5], log.After(2).Select(f => f.Number));
        Assert.Empty(log.After(5));
        Assert.Empty(log.After(9));
        Assert.Equal(5, log.LastNumber);
    }

    [Fact]
    public async Task Ожидание_просыпается_на_новом_факте()
    {
        var (log, _) = NewLog();
        log.Record("k", new { });

        Assert.True(log.WhenAfter(0).IsCompleted);
        var ожидание = log.WhenAfter(1);
        Assert.False(ожидание.IsCompleted);

        log.Record("k", new { });

        await ожидание.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Закрытие_будит_ждущих_и_запрещает_запись()
    {
        var (log, _) = NewLog();
        var ожидание = log.WhenAfter(0);

        log.Complete();

        await ожидание.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(log.IsCompleted);
        Assert.True(log.WhenAfter(100).IsCompleted);
        Assert.Throws<InvalidOperationException>(() => log.Record("k", new { }));
    }

    [Fact]
    public void Параллельная_запись_не_рвёт_нумерацию()
    {
        var (log, disk) = NewLog();

        Parallel.For(0, 300, i => log.Record("k", new { i }));

        Assert.Equal(Enumerable.Range(1, 300).Select(n => (long)n), log.After(0).Select(f => f.Number));
        Assert.Equal(300, FactJournalReader.ReadAfter(new StringReader(disk.ToString()), 0).Count());
    }
}
