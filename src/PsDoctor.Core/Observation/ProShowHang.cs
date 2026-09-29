namespace PsDoctor.Core.Observation;

/// <summary>Что видно у ProShow снаружи, без сообщений его окну.</summary>
public enum ProShowState
{
    /// <summary>Ни ProShow, ни его воркеров.</summary>
    NotRunning,

    /// <summary>Главное окно есть и отвечает.</summary>
    Responding,

    /// <summary>Главное окно есть, Windows считает его не отвечающим.</summary>
    Hung,

    /// <summary>Процессы ProShow живы, главного окна нет: запуск, недозакрытый ProShow или оставшийся воркер.</summary>
    NoWindow,
}

/// <summary>Чем процесс приходится ProShow. Имена образов знает проба в Infrastructure, здесь — только роли.</summary>
public enum ProShowRole
{
    /// <summary>Сам ProShow.</summary>
    Main,

    /// <summary>Воркер декодирования видео.</summary>
    Decoder,

    /// <summary>Воркер <c>device-enc</c> (ffmpeg): кодирует при рендере, декодирует звук и видео при загрузке.</summary>
    Encoder,
}

/// <param name="StartedUtc">Время создания; <c>null</c> — процесс не открылся на чтение, и завершать его нельзя.</param>
/// <param name="Cpu">Процессорное время с начала жизни процесса.</param>
/// <param name="IoBytes">Прочитано и записано байтов с начала жизни процесса.</param>
public sealed record ProShowProcess(ProShowRole Role, int ProcessId, DateTime? StartedUtc, TimeSpan Cpu, long IoBytes);

/// <summary>Один взгляд на куст ProShow.</summary>
/// <param name="Rendering">Идёт рендер: открыто окно рендера. Живой <c>device-enc</c> рендера не доказывает — он декодирует и при загрузке.</param>
public sealed record ProShowSnapshot(ProShowState State, bool Rendering, IReadOnlyList<ProShowProcess> Processes)
{
    public static ProShowSnapshot NotRunning { get; } = new(ProShowState.NotRunning, false, []);

    /// <summary>
    /// Что завершает кнопка «Завершить ProShow»: сам ProShow, а без него — оставшиеся воркеры декодирования.
    /// Воркер <c>device-enc</c> не завершается никогда: он может кодировать рендер. Процесс без времени создания не сверить — он не в списке.
    /// </summary>
    public IReadOnlyList<ProShowProcess> Targets
    {
        get
        {
            var known = Processes.Where(process => process.StartedUtc is not null).ToList();
            var main = known.Where(process => process.Role == ProShowRole.Main).ToList();
            return main.Count > 0 ? main : known.Where(process => process.Role == ProShowRole.Decoder).ToList();
        }
    }
}

/// <summary>Что мастер говорит про не отвечающий ProShow.</summary>
public enum HangAdvice
{
    /// <summary>ProShow отвечает или не запущен: совета нет.</summary>
    None,

    /// <summary>Ждать: не закрывать ProShow, иногда он оживает сам.</summary>
    Wait,

    /// <summary>ProShow не отвечает, но работает или рендерит: подождать. Кнопки завершения нет, сколько бы ни длилось.</summary>
    Busy,

    /// <summary>Можно предложить завершить ProShow — кнопкой монтажёра, с подтверждением.</summary>
    OfferTerminate,
}

/// <summary>
/// Следит за ProShow по снимкам: сколько длится нынешнее состояние и работает ли куст. Решения владельца 28.09.2026
/// (Э6.2, часть Б): первые 60 с — только «ждите»; завершить можно ProShow без окна дольше минуты или окно, которое
/// не отвечает не меньше 3 мин при кусте почти без процессора и ввода-вывода за последние 60 с; занятый и рендерящий
/// ProShow завершить не предлагается никогда. Часы монотонные и чужие: время снимка даёт вызывающий.
/// </summary>
public sealed class ProShowHangWatch
{
    /// <summary>Сколько окно не отвечает непрерывно, прежде чем статус скажет «ProShow не отвечает».</summary>
    public static readonly TimeSpan StatusAfter = TimeSpan.FromSeconds(30);

    /// <summary>Сколько с открытия окна ожидания мастер только просит ждать.</summary>
    public static readonly TimeSpan QuietStart = TimeSpan.FromSeconds(60);

    /// <summary>Сколько ProShow живёт без окна, прежде чем его можно завершить.</summary>
    public static readonly TimeSpan NoWindowLimit = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Сколько окно не отвечает, прежде чем его можно завершить. Назначено, не измерено: «не отвечает» бывает
    /// 16–20 с на нормальной загрузке и импорте; пересматривается по журналам дежурства.
    /// </summary>
    public static readonly TimeSpan HungLimit = TimeSpan.FromMinutes(3);

    /// <summary>За какой отрезок судить, работает ли куст.</summary>
    public static readonly TimeSpan ActivitySpan = TimeSpan.FromSeconds(60);

    /// <summary>
    /// «Почти ноль» процессора за <see cref="ActivitySpan"/> на весь куст — меньше 1,7 % одного ядра. Назначено, не
    /// измерено: приостановленный процесс даёт ровно ноль, загрузка проекта — секунды; пересматривается по журналам.
    /// </summary>
    public static readonly TimeSpan IdleCpu = TimeSpan.FromSeconds(1);

    /// <summary>«Почти ноль» чтения и записи за <see cref="ActivitySpan"/> на весь куст. Назначено, как <see cref="IdleCpu"/>.</summary>
    public const long IdleIoBytes = 1024 * 1024;

    private readonly List<(TimeSpan At, ProShowSnapshot Snapshot)> samples = [];
    private TimeSpan since;

    /// <summary>Последний снимок; до первого — «не запущен».</summary>
    public ProShowSnapshot Last { get; private set; } = ProShowSnapshot.NotRunning;

    /// <summary>Время снимка по монотонным часам; снимки идут по возрастанию времени.</summary>
    public void Observe(TimeSpan at, ProShowSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (samples.Count == 0 || snapshot.State != Last.State)
        {
            since = at;
        }
        Last = snapshot;
        samples.Add((at, snapshot));
        // Хранится один снимок не позже начала отрезка активности — от него и считается прирост.
        while (samples.Count > 1 && samples[1].At <= at - ActivitySpan)
        {
            samples.RemoveAt(0);
        }
    }

    /// <summary>Сколько длится нынешнее состояние.</summary>
    public TimeSpan Duration(TimeSpan now) => samples.Count == 0 ? TimeSpan.Zero : now - since;

    /// <summary>
    /// До какой длительности нынешнего состояния мастер просит ждать, если ProShow так и будет молчать: предел
    /// состояния, но не раньше конца первых <see cref="QuietStart"/> окна. Срок пишется монтажёру — таймер без конца
    /// ничего не обещает (замечание владельца 29.09.2026).
    /// </summary>
    /// <param name="opened">Когда открыто окно ожидания.</param>
    public TimeSpan WaitLimit(TimeSpan opened)
    {
        var limit = Last.State == ProShowState.NoWindow ? NoWindowLimit : HungLimit;
        var quiet = opened + QuietStart - since;
        return quiet > limit ? quiet : limit;
    }

    /// <summary>Главное окно не отвечает непрерывно не меньше <see cref="StatusAfter"/>.</summary>
    public bool NotResponding(TimeSpan now) => Last.State == ProShowState.Hung && Duration(now) >= StatusAfter;

    /// <summary>
    /// Куст тратил процессор или читал и писал за последние <see cref="ActivitySpan"/>; <c>null</c> — снимков на весь
    /// отрезок ещё нет. Процесс сверяется по номеру и времени создания; новый процесс — это работа.
    /// </summary>
    public bool? Active()
    {
        if (samples.Count < 2 || samples[0].At > samples[^1].At - ActivitySpan)
        {
            return null;
        }
        var then = samples[0].Snapshot.Processes.ToDictionary(process => (process.ProcessId, process.StartedUtc));
        var cpu = TimeSpan.Zero;
        var io = 0L;
        foreach (var process in Last.Processes)
        {
            if (then.TryGetValue((process.ProcessId, process.StartedUtc), out var before))
            {
                cpu += process.Cpu - before.Cpu;
                io += process.IoBytes - before.IoBytes;
            }
            else
            {
                return true;
            }
        }
        return cpu >= IdleCpu || io >= IdleIoBytes;
    }

    /// <param name="now">Сейчас по тем же часам, что у снимков.</param>
    /// <param name="opened">Когда открыто окно ожидания.</param>
    public HangAdvice Advice(TimeSpan now, TimeSpan opened)
    {
        if (Last.State is ProShowState.NotRunning or ProShowState.Responding)
        {
            return HangAdvice.None;
        }
        if (now - opened < QuietStart)
        {
            return HangAdvice.Wait;
        }
        if (Last.Rendering)
        {
            return HangAdvice.Busy;
        }
        if (Last.State == ProShowState.NoWindow)
        {
            return Duration(now) >= NoWindowLimit && Last.Targets.Count > 0 ? HangAdvice.OfferTerminate : HangAdvice.Wait;
        }
        return Active() switch
        {
            true => HangAdvice.Busy,
            false when Duration(now) >= HungLimit && Last.Targets.Count > 0 => HangAdvice.OfferTerminate,
            _ => HangAdvice.Wait,
        };
    }
}
