using PsDoctor.Core.Observation;

namespace PsDoctor.Observer;

/// <summary>
/// Дежурство (Э6.2, часть В): увидел ProShow — подключился с <c>origin: watch</c>, монтажёру ничего не показывает. Раз в
/// <see cref="Interval"/>, если живого сеанса и сценария нет, а ProShow запущен, — пассивное подключение. Отказ
/// подключения повторяется не раньше чем через <see cref="RetryAfter"/> и фактов не пишет; последний отказ виден в
/// <c>/health</c>. Сеанс дежурства кончается с выходом программы, как любой пассивный.
/// </summary>
public sealed class WatchDuty : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    public static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(30);

    private readonly ObservationService service;
    private readonly Func<TimeSpan> clock;
    private readonly Func<DateTime> utcNow;
    private readonly Lock gate = new();
    private Timer? timer;
    private TimeSpan? retryAt;
    private string? lastRefusal;
    private DateTime? lastRefusalUtc;
    private int ticking;

    /// <param name="clock">Монотонные часы паузы после отказа; по умолчанию — системный счётчик.</param>
    public WatchDuty(ObservationService service, Func<TimeSpan>? clock = null, Func<DateTime>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        this.service = service;
        this.clock = clock ?? (() => TimeSpan.FromMilliseconds(Environment.TickCount64));
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public WatchStatus Status
    {
        get
        {
            lock (gate)
            {
                return new WatchStatus(true, lastRefusal, lastRefusalUtc);
            }
        }
    }

    public void Start() => timer ??= new Timer(_ => Tick(), null, Interval, Interval);

    /// <summary>Один шаг дежурства. Шаг, начатый, пока идёт прежний, ничего не делает: подключение длится до секунд.</summary>
    public void Tick()
    {
        if (Interlocked.Exchange(ref ticking, 1) == 1)
        {
            return;
        }
        try
        {
            lock (gate)
            {
                if (retryAt is { } at && clock() < at)
                {
                    return;
                }
            }
            // Та же проверка, что у /health: ProShow запущен мимо наблюдателя, сеанса и сценария нет.
            var activity = service.Activity();
            if (activity.Program != ProgramStates.Unobserved || activity.Scenario)
            {
                return;
            }
            var (accepted, _, error) = service.Attach(SessionOrigins.Watch);
            // Другой клиент успел первым — запись идёт, отказом это не считается.
            if (accepted is not null || error?.Error == ObserverErrors.ProgramRunning)
            {
                return;
            }
            lock (gate)
            {
                lastRefusal = error?.Error;
                lastRefusalUtc = utcNow();
                retryAt = clock() + RetryAfter;
            }
        }
        catch (Exception)
        {
            // Шаг идёт из таймера: необработанное исключение уронило бы наблюдатель. Журнал сеанса не открылся — например,
            // кончилось место, — или сорвалось подключение: следующая попытка после паузы, как после отказа.
            lock (gate)
            {
                lastRefusal = ObserverErrors.AttachFailed;
                lastRefusalUtc = utcNow();
                retryAt = clock() + RetryAfter;
            }
        }
        finally
        {
            Volatile.Write(ref ticking, 0);
        }
    }

    public void Dispose()
    {
        timer?.Dispose();
        timer = null;
    }
}
