using System.Text.Json;

namespace PsDoctor.Core.Observation;

/// <summary>
/// Известное повторение: процесс с образом <paramref name="Image"/> запускается одним родителем не меньше
/// <paramref name="Launches"/> раз за <paramref name="Window"/>. Параметры — данные: следующий такой паттерн — новый
/// экземпляр, а не новый код.
/// </summary>
/// <param name="Name">Устойчивое имя паттерна: по нему App подбирает фразу и кнопку.</param>
/// <param name="Image">Имя файла образа без пути; регистр не важен.</param>
public sealed record RepeatedLaunchPattern(string Name, string Image, TimeSpan Window, int Launches)
{
    /// <summary>
    /// ProShow со снятой галкой «Avoid using DirectShow» и без QuickTime запускает <c>qtime.exe</c> около раза в секунду,
    /// пока шоу открыто (журнал реверсинга, 30.09.2026). Пять журналов стенда: в цикле 80 запусков за 60 с, без него —
    /// не больше 5. Порог между ними предварительный, по одному стенду (Э4.4).
    /// </summary>
    public static RepeatedLaunchPattern QuickTimeLoop { get; } = new("qtime-loop", "qtime.exe", TimeSpan.FromSeconds(60), 20);
}

/// <summary>Данные факта <c>episode</c> от <see cref="RepeatedLaunchDetector"/>.</summary>
/// <param name="ParentProcessId">Кто запускает; <c>null</c> — ETW родителя не назвал.</param>
/// <param name="Launches">Запусков в окне, когда эпизод узнан: ровно порог.</param>
/// <param name="FirstLaunch">Время сеанса первого запуска в этом окне.</param>
public sealed record EpisodeFound(string Pattern, string Image, int? ParentProcessId, int Launches, TimeSpan Window, TimeSpan FirstLaunch);

/// <summary>
/// Детектор повторного запуска по фактам <c>etw-process-started</c>. Время — <see cref="Fact.Elapsed"/>: факты ETW
/// приходят пачками, и пачка только сжимает запуски, но не добавляет их, поэтому порог по числу от этого не врёт.
/// Один эпизод на родителя за сеанс: повторение узнаётся один раз, его конец не пишется. Экземпляр — на один сеанс.
/// </summary>
public sealed class RepeatedLaunchDetector
{
    // Родитель без номера считается отдельным родителем с номером 0: это System Idle, он ничего не запускает.
    private const int UnknownParent = 0;

    private readonly RepeatedLaunchPattern pattern;
    private readonly Dictionary<int, Queue<TimeSpan>> launches = [];
    private readonly HashSet<int> found = [];

    public RepeatedLaunchDetector(RepeatedLaunchPattern pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentOutOfRangeException.ThrowIfLessThan(pattern.Launches, 2);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(pattern.Window, TimeSpan.Zero);
        this.pattern = pattern;
    }

    /// <summary>Факты по порядку журнала. Эпизод — когда порог достигнут впервые для этого родителя, иначе <c>null</c>.</summary>
    public EpisodeFound? Observe(Fact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        if (fact.Kind != ProgramFactKinds.EtwProcessStarted || !IsPatternImage(fact.Data))
        {
            return null;
        }
        var parent = ParentOf(fact.Data);
        var key = parent ?? UnknownParent;
        if (found.Contains(key))
        {
            return null;
        }
        if (!launches.TryGetValue(key, out var times))
        {
            times = new Queue<TimeSpan>();
            launches.Add(key, times);
        }
        times.Enqueue(fact.Elapsed);
        while (times.Peek() < fact.Elapsed - pattern.Window)
        {
            times.Dequeue();
        }
        if (times.Count < pattern.Launches)
        {
            return null;
        }
        found.Add(key);
        launches.Remove(key);
        return new EpisodeFound(pattern.Name, pattern.Image, parent, pattern.Launches, pattern.Window, times.Peek());
    }

    private bool IsPatternImage(JsonElement data)
    {
        if (!data.TryGetProperty("image", out var image) || image.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        // Путь бывает виндовым и на Linux, где Path обратную косую черту разделителем не считает.
        var name = image.GetString()!;
        name = name[(name.LastIndexOfAny(['\\', '/']) + 1)..];
        return string.Equals(name, pattern.Image, StringComparison.OrdinalIgnoreCase);
    }

    private static int? ParentOf(JsonElement data) =>
        data.TryGetProperty("parentProcessId", out var parent)
        && parent.ValueKind == JsonValueKind.Number
        && parent.TryGetInt32(out var id)
            ? id
            : null;
}
