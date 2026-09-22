using System.Text.Json;
using PsDoctor.Core.Observation;

namespace PsDoctor.Infrastructure.Observation;

/// <summary>Событие локальной истории попыток восстановления Doctor.</summary>
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

    public RepairHistoryEvent? PendingAttempt() => Read()
        .Where(item => item.Event == "attempt")
        .GroupJoin(Read().Where(item => item.Event == "feedback"),
            attempt => attempt.Attempt,
            feedback => feedback.Attempt,
            (attempt, feedback) => new { attempt, feedback })
        .Where(pair => !pair.feedback.Any())
        .OrderByDescending(pair => pair.attempt.Attempt)
        .Select(pair => pair.attempt)
        .FirstOrDefault();

    public int UnresolvedAttempts(string symptom)
    {
        var events = Read();
        var feedback = events.Where(item => item.Event == "feedback")
            .GroupBy(item => item.Attempt)
            .ToDictionary(group => group.Key, group => group.Last().Result);
        return events.Where(item => item.Event == "attempt"
                && item.Symptom == symptom
                && feedback.GetValueOrDefault(item.Attempt) == "unresolved")
            .Select(item => item.Attempt)
            .Distinct()
            .Count();
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
