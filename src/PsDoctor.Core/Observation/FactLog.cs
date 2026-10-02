using System.Text.Json;

namespace PsDoctor.Core.Observation;

/// <summary>
/// Журнал живого сеанса в памяти поверх журнала на диске: поток отдаёт факты отсюда, не перечитывая файл.
/// Факт сначала записывается во внутренний писатель, и только потом становится виден читателям, —
/// поэтому клиент никогда не получит номер, которого нет в журнале на диске.
/// </summary>
/// <remarks>
/// Номера идут подряд с единицы, поэтому хвост после номера N — это всё, начиная с позиции N.
/// Журнал целиком в памяти. Сеанс дежурства у монтажёра — рабочий день: 30.09.2026 с секундными фактами о файлах это был
/// гигабайт, Windows сочла наблюдатель утекающим; со сводкой файлов по минутам (Э6.4) от того дня осталось бы около
/// 330 тысяч фактов из 2,17 миллиона.
/// Держать в памяти только хвост — следующий шаг, если день и после сводки окажется тяжёл.
/// Детекторы (Э4.4) видят каждый факт под тем же замком, что и номер: эпизод ложится в журнал сразу за фактом,
/// на котором узнан, и порядок их у всех читателей один.
/// </remarks>
public sealed class FactLog : IFactRecorder
{
    private readonly IFactRecorder inner;
    private readonly IReadOnlyList<RepeatedLaunchDetector> detectors;
    private readonly List<Fact> facts = [];
    private readonly Lock gate = new();
    private TaskCompletionSource changed = NewSignal();
    private bool completed;

    /// <param name="detectors">Детекторы эпизодов этого сеанса; у каждого сеанса свои экземпляры.</param>
    public FactLog(IFactRecorder inner, IReadOnlyList<RepeatedLaunchDetector>? detectors = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        this.inner = inner;
        this.detectors = detectors ?? [];
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
            fact = Append(kind, processId, data);
            foreach (var detector in detectors)
            {
                if (detector.Observe(fact) is { } episode)
                {
                    Append(ProgramFactKinds.Episode, episode.ParentProcessId, ObservationJson.ToElement(episode));
                }
            }
            wake = changed;
            changed = NewSignal();
        }
        wake.TrySetResult();
        return fact;
    }

    private Fact Append(string kind, int? processId, JsonElement data)
    {
        var fact = inner.Record(kind, processId, data);
        if (fact.Number != facts.Count + 1)
        {
            throw new InvalidOperationException($"номер факта {fact.Number} не следует за {facts.Count}");
        }
        facts.Add(fact);
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
