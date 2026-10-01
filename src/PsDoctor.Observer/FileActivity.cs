using System.Numerics;

namespace PsDoctor.Observer;

/// <summary>
/// Файловые операции куста, сведённые по минутам: одна запись на тройку «минута, образ процесса, файл» (Э6.4). Прежняя
/// секундная запись на каждый процесс дала у монтажёра за 8,5 ч 2 011 412 фактов и 940 МБ журнала, 98 % из них — «открыл
/// и не читал»: ProShow весь день открывает файлы проекта, а сотни одинаковых воркеров рендера открывают одни и те же
/// библиотеки. Минута и образ вместо секунды и номера процесса дают в 11,8 раза меньше (журнал 30.09.2026). Время внутри
/// минуты не теряется: первая и последняя операция и число секунд, в которые файл трогали. Каждая операция с точным
/// временем остаётся в сырье ETW. Образ сравнивается так же, как у загрузок модулей (<see cref="ModuleLoads"/>).
/// Не потокобезопасен.
/// </summary>
public sealed class FileActivity
{
    /// <summary>
    /// Запись минуты уходит, когда минута кончилась и прошло ещё столько: события доходят из буферов ETW с задержкой.
    /// Опоздавшее событие откроет вторую запись той же минуты — она не теряется, а лишь дробит сводку.
    /// </summary>
    public static readonly TimeSpan Late = TimeSpan.FromSeconds(5);

    private readonly Dictionary<(long Minute, string Image, string File), Bucket> buckets = new(new KeyComparer());

    /// <param name="operation"><c>open</c>, <c>read</c>, <c>write</c> или <c>sharing-violation</c>.</param>
    public void Add(DateTime timeUtc, int processId, string? image, string? file, string operation, long bytes)
    {
        var minute = timeUtc.Ticks / TimeSpan.TicksPerMinute;
        var key = (minute, ModuleLoads.Key(image, processId), file ?? "");
        if (!buckets.TryGetValue(key, out var bucket))
        {
            buckets[key] = bucket = new Bucket(minute, processId, image, file, timeUtc);
        }
        bucket.Add(timeUtc, processId, operation, bytes);
    }

    /// <summary>Записи минут, кончившихся не позже <c>nowUtc − Late</c>; отданные забываются.</summary>
    public IReadOnlyList<EtwSummary> Due(DateTime nowUtc)
    {
        var limit = (nowUtc - Late).Ticks / TimeSpan.TicksPerMinute;
        var due = buckets.Where(pair => pair.Key.Minute < limit).OrderBy(pair => pair.Value.FirstUtc).ToList();
        foreach (var pair in due)
        {
            buckets.Remove(pair.Key);
        }
        return [.. due.Select(pair => pair.Value.ToSummary())];
    }

    /// <summary>Все записи, кончилась минута или нет: запись сеанса останавливается.</summary>
    public IReadOnlyList<EtwSummary> All()
    {
        var all = buckets.Values.OrderBy(bucket => bucket.FirstUtc).Select(bucket => bucket.ToSummary()).ToList();
        buckets.Clear();
        return all;
    }

    private sealed class Bucket(long minute, int processId, string? image, string? file, DateTime firstUtc)
    {
        private readonly HashSet<int> processes = [];
        private ulong seconds;
        private int opens;
        private int reads;
        private long readBytes;
        private int writes;
        private long writeBytes;
        private int sharingViolations;

        public DateTime FirstUtc { get; private set; } = firstUtc;

        private DateTime LastUtc { get; set; } = firstUtc;

        public void Add(DateTime timeUtc, int pid, string operation, long bytes)
        {
            processes.Add(pid);
            // Секунда внутри минуты — бит маски: события разных процессоров приходят не по порядку, а маска считает точно.
            seconds |= 1UL << (int)(timeUtc.Ticks / TimeSpan.TicksPerSecond - minute * 60);
            if (timeUtc < FirstUtc) FirstUtc = timeUtc;
            if (timeUtc > LastUtc) LastUtc = timeUtc;
            switch (operation)
            {
                case "open": opens++; break;
                case "read": reads++; readBytes += bytes; break;
                case "write": writes++; writeBytes += bytes; break;
                case "sharing-violation": sharingViolations++; break;
            }
        }

        public EtwSummary ToSummary() => new(FirstUtc, processId, file, opens, reads, readBytes, writes, writeBytes,
            sharingViolations, Image: image, LastUtc: LastUtc, Seconds: BitOperations.PopCount(seconds), Processes: processes.Count);
    }

    private sealed class KeyComparer : IEqualityComparer<(long Minute, string Image, string File)>
    {
        public bool Equals((long Minute, string Image, string File) x, (long Minute, string Image, string File) y) =>
            x.Minute == y.Minute && StringComparer.OrdinalIgnoreCase.Equals(x.Image, y.Image)
            && StringComparer.OrdinalIgnoreCase.Equals(x.File, y.File);

        public int GetHashCode((long Minute, string Image, string File) value) => HashCode.Combine(value.Minute,
            StringComparer.OrdinalIgnoreCase.GetHashCode(value.Image), StringComparer.OrdinalIgnoreCase.GetHashCode(value.File));
    }
}
