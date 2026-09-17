using System.Text.Json;

namespace PsDoctor.Core.Observation;

/// <summary>Чтение журнала сеанса с номера — то, чем клиент переподключается и ничего не теряет.</summary>
public static class FactJournalReader
{
    /// <summary>
    /// Факты с номером больше <paramref name="after"/>, по порядку. Ноль — журнал целиком.
    /// </summary>
    /// <remarks>
    /// Последняя строка может быть оборвана: журнал читают, пока в него пишут, и наблюдатель может
    /// упасть посреди записи. Такая строка ещё не факт и пропускается. Нечитаемая строка в середине —
    /// журнал испорчен, об этом исключение с номером строки: молча отдать хвост с дырой хуже.
    /// </remarks>
    public static IEnumerable<Fact> ReadAfter(TextReader reader, long after)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentOutOfRangeException.ThrowIfNegative(after);
        return Read(reader, after);
    }

    private static IEnumerable<Fact> Read(TextReader reader, long after)
    {
        var lineNumber = 0;
        var line = reader.ReadLine();
        while (line is not null)
        {
            lineNumber++;
            var next = reader.ReadLine();
            if (line.Length > 0)
            {
                var fact = Parse(line);
                if (fact is null)
                {
                    if (next is null)
                    {
                        yield break;
                    }
                    throw new InvalidDataException($"журнал сеанса испорчен: строка {lineNumber} не факт");
                }
                if (fact.Number > after)
                {
                    yield return fact;
                }
            }
            line = next;
        }
    }

    private static Fact? Parse(string line)
    {
        try
        {
            var fact = JsonSerializer.Deserialize<Fact>(line, ObservationJson.Options);
            return fact is { Kind: not null, Session: not null } && fact.Data.ValueKind == JsonValueKind.Object ? fact : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
