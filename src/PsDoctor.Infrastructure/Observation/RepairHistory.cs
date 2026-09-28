using System.Text.Json;
using PsDoctor.Core.Observation;

namespace PsDoctor.Infrastructure.Observation;

/// <summary>
/// Событие локальной истории попыток восстановления Doctor: <c>attempt</c>, <c>result</c>, <c>feedback</c> и <c>incident</c> —
/// метка «Решить проблему», которую не принял наблюдатель (номер попытки 0, на счёт попыток не влияет).
/// </summary>
public sealed record RepairHistoryEvent(
    DateTimeOffset TimeUtc,
    string Event,
    int Attempt,
    string Symptom,
    string? ProjectPath = null,
    string? Result = null);

/// <summary>Небольшой append-only журнал действий мастера в профиле пользователя.</summary>
public sealed class RepairHistory
{
    private static readonly JsonSerializerOptions JsonOptions = new(ObservationJson.Options)
    {
        WriteIndented = false,
    };

    private readonly string path;

    public RepairHistory(string? localAppData = null)
    {
        var root = localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        path = Path.Combine(root, "PsDoctor", "repair-history.jsonl");
    }

    public IReadOnlyList<RepairHistoryEvent> Read()
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var events = new List<RepairHistoryEvent>();
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (line.Length == 0)
                {
                    continue;
                }
                try
                {
                    if (JsonSerializer.Deserialize<RepairHistoryEvent>(line, JsonOptions) is { } item)
                    {
                        events.Add(item);
                    }
                }
                catch (JsonException)
                {
                    // Повреждённая строка не должна скрыть предыдущие записи журнала.
                }
            }
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
        return events;
    }

    public int NextAttempt() => Read().Select(item => item.Attempt).DefaultIfEmpty().Max() + 1;

    /// <summary>
    /// Неудачи считаются только за этот срок: без него пара старых отказов навсегда убирала бы быстрое восстановление
    /// (решение владельца 28.09.2026, Э6.2).
    /// </summary>
    public static readonly TimeSpan FailureWindow = TimeSpan.FromDays(14);

    /// <summary>
    /// Про попытку старше этого срока «помогло ли» не спрашивается: ответ был бы уже не о ней, а ответы сместились
    /// бы к «нет». Такая попытка получает ответ <c>unknown</c>, см. <see cref="ExpireFeedback"/>.
    /// </summary>
    public static readonly TimeSpan FeedbackWindow = TimeSpan.FromDays(2);

    /// <summary>Последняя попытка без ответа, если она не старше <see cref="FeedbackWindow"/>.</summary>
    public RepairHistoryEvent? PendingAttempt(DateTimeOffset now) => Unanswered(Read())
        .Where(attempt => attempt.TimeUtc >= now - FeedbackWindow)
        .OrderByDescending(attempt => attempt.Attempt)
        .FirstOrDefault();

    /// <summary>Попыткам без ответа старше <see cref="FeedbackWindow"/> дописывает ответ <c>unknown</c>; возвращает их число.</summary>
    public int ExpireFeedback(DateTimeOffset now)
    {
        var stale = Unanswered(Read()).Where(attempt => attempt.TimeUtc < now - FeedbackWindow).ToList();
        foreach (var attempt in stale)
        {
            TryAppend(new RepairHistoryEvent(now, "feedback", attempt.Attempt, attempt.Symptom, attempt.ProjectPath, "unknown"));
        }
        return stale.Count;
    }

    /// <summary>Подтверждённые неудачи симптома за последние <see cref="FailureWindow"/>.</summary>
    public int UnresolvedAttempts(string symptom, DateTimeOffset now)
    {
        var events = Read();
        var feedback = events.Where(item => item.Event == "feedback")
            .GroupBy(item => item.Attempt)
            .ToDictionary(group => group.Key, group => group.Last().Result);
        return events.Where(item => item.Event == "attempt"
                && item.Symptom == symptom
                && item.TimeUtc >= now - FailureWindow
                && feedback.GetValueOrDefault(item.Attempt) == "unresolved")
            .Select(item => item.Attempt)
            .Distinct()
            .Count();
    }

    private static IEnumerable<RepairHistoryEvent> Unanswered(IReadOnlyList<RepairHistoryEvent> events)
    {
        var answered = events.Where(item => item.Event == "feedback").Select(item => item.Attempt).ToHashSet();
        return events.Where(item => item.Event == "attempt" && !answered.Contains(item.Attempt));
    }

    public bool TryAppend(RepairHistoryEvent item)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, JsonSerializer.Serialize(item, JsonOptions) + Environment.NewLine);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
