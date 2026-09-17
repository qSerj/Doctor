using System.Text.Json;

namespace PsDoctor.Core.Observation;

/// <summary>
/// Журнал живого сеанса в памяти поверх журнала на диске: поток отдаёт факты отсюда, не перечитывая файл.
/// Факт сначала записывается во внутренний писатель, и только потом становится виден читателям, —
/// поэтому клиент никогда не получит номер, которого нет в журнале на диске.
/// </summary>
/// <remarks>
/// Номера идут подряд с единицы, поэтому хвост после номера N — это всё, начиная с позиции N.
/// Сеанс под наблюдением — это часы рендера, по фактам в секунду на процесс: в памяти это единицы мегабайт.
/// </remarks>
public sealed class FactLog : IFactRecorder
{
    private readonly IFactRecorder inner;
    private readonly List<Fact> facts = [];
    private readonly Lock gate = new();
    private TaskCompletionSource changed = NewSignal();
    private bool completed;

    public FactLog(IFactRecorder inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        this.inner = inner;
    }

    public long LastNumber
    {
        get
        {
            lock (gate)
            {
                return facts.Count;
            }
        }
    }

    /// <summary>Сеанс закрыт: новых фактов не будет.</summary>
    public bool IsCompleted
    {
        get
        {
            lock (gate)
            {
                return completed;
            }
        }
    }

    public Fact Record(string kind, int? processId, JsonElement data)
    {
        TaskCompletionSource wake;
        Fact fact;
        lock (gate)
        {
            if (completed)
            {
                throw new InvalidOperationException("сеанс закрыт, факт записать некуда");
            }
            fact = inner.Record(kind, processId, data);
            if (fact.Number != facts.Count + 1)
            {
                throw new InvalidOperationException($"номер факта {fact.Number} не следует за {facts.Count}");
            }
            facts.Add(fact);
            wake = changed;
            changed = NewSignal();
        }
        wake.TrySetResult();
        return fact;
    }

    /// <summary>Факты с номером больше <paramref name="after"/>, по порядку.</summary>
    public IReadOnlyList<Fact> After(long after)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(after);
        lock (gate)
        {
            return after >= facts.Count ? [] : facts.GetRange((int)after, facts.Count - (int)after);
        }
    }

    /// <summary>
    /// Задача, которая завершится, когда появится факт с номером больше <paramref name="after"/>
    /// или журнал закроется. Часов у журнала нет: сколько ждать, решает вызывающий.
    /// </summary>
    public Task WhenAfter(long after)
    {
        lock (gate)
        {
            return completed || facts.Count > after ? Task.CompletedTask : changed.Task;
        }
    }

    public void Complete()
    {
        TaskCompletionSource wake;
        lock (gate)
        {
            if (completed)
            {
                return;
            }
            completed = true;
            wake = changed;
        }
        wake.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
