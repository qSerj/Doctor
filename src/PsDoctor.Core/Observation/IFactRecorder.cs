using System.Text.Json;

namespace PsDoctor.Core.Observation;

/// <summary>
/// Куда пишутся факты. Номер и время ставит тот, кто записывает, а не источник факта:
/// источников в сеансе несколько, а порядок у журнала один.
/// </summary>
public interface IFactRecorder
{
    Fact Record(string kind, int? processId, JsonElement data);
}

public static class FactRecorderExtensions
{
    public static Fact Record<T>(this IFactRecorder recorder, string kind, T data, int? processId = null)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        return recorder.Record(kind, processId, ObservationJson.ToElement(data));
    }
}
