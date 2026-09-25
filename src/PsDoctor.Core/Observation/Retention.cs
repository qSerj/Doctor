namespace PsDoctor.Core.Observation;

/// <summary>Пределы хранения сеансов наблюдателя.</summary>
/// <param name="Age">Сколько живёт обычный сеанс.</param>
/// <param name="Bytes">Сколько байтов занимают все сеансы вместе.</param>
/// <param name="MarkedAge">Сколько живёт помеченный сеанс — с эпизодом или начатый мастером.</param>
public sealed record RetentionLimits(TimeSpan Age, long Bytes, TimeSpan MarkedAge);

/// <summary>Сеанс на диске, как его видит хранение: время открытия, вес всех его файлов, пометка.</summary>
public sealed record StoredSession(string Id, DateTime StartedUtc, long Bytes, bool Marked);

/// <summary>
/// Какие сеансы удалить. Срок и объём — что наступит раньше; старое уходит первым. Помеченный сеанс
/// живёт по своему сроку, а по объёму уходит только после всех обычных: иначе один день рендеров
/// вытеснил бы сеанс, ради которого инженер приедет.
/// </summary>
public static class Retention
{
    public static IReadOnlyList<string> Expired(IEnumerable<StoredSession> sessions, RetentionLimits limits, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(limits);

        var expired = new List<string>();
        var kept = new List<StoredSession>();
        foreach (var session in sessions.OrderBy(s => s.StartedUtc).ThenBy(s => s.Id, StringComparer.Ordinal))
        {
            var age = session.Marked ? limits.MarkedAge : limits.Age;
            if (utcNow - session.StartedUtc > age)
            {
                expired.Add(session.Id);
            }
            else
            {
                kept.Add(session);
            }
        }

        var total = kept.Sum(s => s.Bytes);
        foreach (var marked in new[] { false, true })
        {
            foreach (var session in kept.Where(s => s.Marked == marked))
            {
                if (total <= limits.Bytes)
                {
                    return expired;
                }
                expired.Add(session.Id);
                total -= session.Bytes;
            }
        }
        return expired;
    }

    /// <summary>Сеанс помечен, если его начал мастер или в нём есть эпизод детектора.</summary>
    public static bool IsMarked(IEnumerable<Fact> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        foreach (var fact in facts)
        {
            if (fact.Kind == ProgramFactKinds.Episode)
            {
                return true;
            }
            if (fact.Kind == ProgramFactKinds.SessionStarted
                && fact.Data.TryGetProperty("origin", out var origin)
                && origin.ValueKind == System.Text.Json.JsonValueKind.String
                && origin.GetString() == SessionOrigins.Wizard)
            {
                return true;
            }
        }
        return false;
    }
}
