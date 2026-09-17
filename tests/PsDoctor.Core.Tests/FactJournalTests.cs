using System.Text.Json;
using PsDoctor.Core.Observation;
using Xunit;

namespace PsDoctor.Core.Tests;

public sealed class FactJournalTests
{
    [Fact]
    public void Номера_идут_с_единицы_без_пропусков()
    {
        var (writer, _) = Journal(out _);

        var numbers = Enumerable.Range(0, 4).Select(_ => writer.Record("tick", new { }).Number).ToArray();

        Assert.Equal([1L, 2, 3, 4], numbers);
        Assert.Equal(4, writer.LastNumber);
    }

    [Theory]
    [InlineData(0, new long[] { 1, 2, 3, 4, 5 })]
    [InlineData(2, new long[] { 3, 4, 5 })]
    [InlineData(4, new long[] { 5 })]
    [InlineData(5, new long[] { })]
    [InlineData(9, new long[] { })]
    public void Чтение_с_номера_отдаёт_ровно_хвост_после_него(long after, long[] expected)
    {
        var (writer, text) = Journal(out _);
        for (var index = 0; index < 5; index++)
        {
            writer.Record("tick", new { index });
        }

        var facts = FactJournalReader.ReadAfter(new StringReader(text.ToString()), after).ToList();

        Assert.Equal(expected, facts.Select(fact => fact.Number));
        Assert.All(facts, fact => Assert.Equal(fact.Number - 1, fact.Data.GetProperty("index").GetInt32()));
    }

    [Fact]
    public void Факт_переживает_запись_и_чтение()
    {
        var (writer, text) = Journal(out var clock);
        clock.Now = TimeSpan.FromMilliseconds(12_345.6789);

        var written = writer.Record("dialog-opened", new { title = "Message", buttons = new[] { "Ok to All", "Ok" } }, processId: 4242);
        var read = Assert.Single(FactJournalReader.ReadAfter(new StringReader(text.ToString()), 0));

        Assert.Equal(written.Number, read.Number);
        Assert.Equal(clock.Now, read.Elapsed);
        Assert.Equal("сеанс-1", read.Session);
        Assert.Equal(4242, read.ProcessId);
        Assert.Equal("dialog-opened", read.Kind);
        Assert.Equal("Ok to All", read.Data.GetProperty("buttons")[0].GetString());
    }

    [Fact]
    public void Кириллица_в_журнале_не_экранируется()
    {
        var (writer, text) = Journal(out _);

        writer.Record("title", new { title = "Проект.psh - ProShow" });

        Assert.Contains("Проект.psh", text.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Факт_без_процесса_и_данных_пишет_null_и_пустой_объект()
    {
        var (writer, text) = Journal(out _);

        writer.Record("scenario-started", null, ObservationJson.Empty);
        var read = Assert.Single(FactJournalReader.ReadAfter(new StringReader(text.ToString()), 0));

        Assert.Contains("\"processId\":null", text.ToString(), StringComparison.Ordinal);
        Assert.Null(read.ProcessId);
        Assert.Equal(JsonValueKind.Object, read.Data.ValueKind);
    }

    [Fact]
    public void Дописанный_журнал_продолжает_номера()
    {
        var (first, text) = Journal(out _);
        first.Record("tick", new { });
        first.Record("tick", new { });

        var clock = new FakeClock();
        var second = new FactJournalWriter(text, "сеанс-1", () => clock.Now, lastNumber: first.LastNumber);
        second.Record("tick", new { });

        var numbers = FactJournalReader.ReadAfter(new StringReader(text.ToString()), 0).Select(fact => fact.Number);
        Assert.Equal([1L, 2, 3], numbers);
    }

    [Fact]
    public void Оборванная_последняя_строка_ещё_не_факт()
    {
        var (writer, text) = Journal(out _);
        writer.Record("tick", new { });
        writer.Record("tick", new { });
        text.Write("{\"number\":3,\"elapsed\":\"00:00");

        var facts = FactJournalReader.ReadAfter(new StringReader(text.ToString()), 0).ToList();

        Assert.Equal([1L, 2], facts.Select(fact => fact.Number));
    }

    [Fact]
    public void Испорченная_строка_в_середине_журнала_не_пропускается_молча()
    {
        var (writer, text) = Journal(out _);
        writer.Record("tick", new { });
        text.WriteLine("мусор");
        writer.Record("tick", new { });

        var error = Assert.Throws<InvalidDataException>(() => FactJournalReader.ReadAfter(new StringReader(text.ToString()), 0).ToList());

        Assert.Contains("строка 2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Данные_факта_только_объект()
    {
        var (writer, _) = Journal(out _);

        Assert.Throws<ArgumentException>(() => writer.Record("tick", 5));
    }

    [Fact]
    public void Факты_из_разных_потоков_не_путают_номера()
    {
        var (writer, text) = Journal(out _);

        Parallel.For(0, 200, index => writer.Record("tick", new { index }));

        var numbers = FactJournalReader.ReadAfter(new StringReader(text.ToString()), 0).Select(fact => fact.Number);
        Assert.Equal(Enumerable.Range(1, 200).Select(number => (long)number), numbers);
    }

    private static (FactJournalWriter Writer, StringWriter Text) Journal(out FakeClock clock)
    {
        var text = new StringWriter();
        var ownClock = new FakeClock();
        clock = ownClock;
        return (new FactJournalWriter(text, "сеанс-1", () => ownClock.Now), text);
    }

    private sealed class FakeClock
    {
        public TimeSpan Now { get; set; }
    }
}
