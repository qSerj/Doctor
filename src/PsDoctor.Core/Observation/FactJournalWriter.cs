using System.Text.Json;

namespace PsDoctor.Core.Observation;

/// <summary>
/// Писатель журнала сеанса: JSON Lines, строка на факт. Номера выдаёт он же, под замком,
/// поэтому факты из разных потоков ложатся в журнал в порядке номеров.
/// </summary>
/// <remarks>
/// Писатель не знает ни про файл, ни про часы: ему дают поток текста и функцию, отвечающую,
/// сколько прошло от старта сеанса. Каждая строка сбрасывается сразу — журнал читают,
/// пока сеанс идёт, и строка, застрявшая в буфере, для читателя не существует.
/// </remarks>
public sealed class FactJournalWriter : IFactRecorder
{
    private readonly TextWriter writer;
    private readonly Func<TimeSpan> elapsed;
    private readonly Lock gate = new();

    /// <param name="lastNumber">Последний номер уже записанного журнала, если он дописывается; иначе ноль.</param>
    public FactJournalWriter(TextWriter writer, string session, Func<TimeSpan> elapsed, long lastNumber = 0)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentException.ThrowIfNullOrEmpty(session);
        ArgumentNullException.ThrowIfNull(elapsed);
        ArgumentOutOfRangeException.ThrowIfNegative(lastNumber);

        this.writer = writer;
        this.elapsed = elapsed;
        Session = session;
        LastNumber = lastNumber;
    }

    public string Session { get; }

    public long LastNumber { get; private set; }

    public Fact Record(string kind, int? processId, JsonElement data)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        if (data.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException($"данные факта должны быть объектом JSON, а не {data.ValueKind}", nameof(data));
        }

        lock (gate)
        {
            var fact = new Fact(LastNumber + 1, elapsed(), Session, processId, kind, data.Clone());
            writer.WriteLine(JsonSerializer.Serialize(fact, ObservationJson.Options));
            writer.Flush();
            LastNumber = fact.Number;
            return fact;
        }
    }
}
