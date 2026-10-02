using PsDoctor.Core.Observation;
using Xunit;

namespace PsDoctor.Observer.Tests;

/// <summary>Детектор цикла <c>qtime.exe</c> работает в каждом сеансе наблюдателя (Э4.4): эпизод — фактом журнала.</summary>
public sealed class EpisodeTests : IDisposable
{
    private const int ProShow = 6812;

    private readonly string _каталог = Directory.CreateTempSubdirectory("psdoctor-эпизоды-").FullName;

    public void Dispose() => Directory.Delete(_каталог, recursive: true);

    [Fact]
    public async Task Цикл_qtime_в_сеансе_пишется_фактом_episode()
    {
        var диск = new StringWriter();
        var сеанс = ObservationSession.Open(_каталог, "20261002-120000-000", new ObserverHealth("test", null), _ => { }, journal: диск);
        for (var i = 1; i <= 25; i++)
        {
            сеанс.Log.Record(ProgramFactKinds.EtwProcessStarted,
                new { image = "qtime.exe", commandLine = $"qtime.exe PhotodexDShowFileMap{ProShow}-{i}", parentProcessId = ProShow, timeUtc = (string?)null },
                7000 + i);
        }
        await сеанс.FinishAsync(SessionEndReasons.Stopped);

        var эпизод = Assert.Single(сеанс.Log.After(0), fact => fact.Kind == ProgramFactKinds.Episode);
        var двадцатый = сеанс.Log.After(0).Where(fact => fact.Kind == ProgramFactKinds.EtwProcessStarted).ElementAt(19);
        Assert.Equal(двадцатый.Number + 1, эпизод.Number);
        Assert.Equal(ProShow, эпизод.ProcessId);
        Assert.Equal("qtime-loop", эпизод.Data.GetProperty("pattern").GetString());
        Assert.Equal(20, эпизод.Data.GetProperty("launches").GetInt32());

        // На диске — то же, что в памяти: эпизод не только для потока.
        var сДиска = FactJournalReader.ReadAfter(new StringReader(диск.ToString()), 0).ToList();
        Assert.Equal(эпизод.Number, Assert.Single(сДиска, fact => fact.Kind == ProgramFactKinds.Episode).Number);
    }
}
