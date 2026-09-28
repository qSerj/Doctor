using System.Runtime.Versioning;

namespace PsDoctor.App;

/// <summary>
/// Один Doctor на пользователя. Второй запуск (ярлык, двойной щелчок) не открывает ещё одно окно, а будит
/// первый — тот показывает своё. Имена живут в сеансе пользователя: у другого пользователя свой Doctor.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class SingleInstance
{
    private const string MutexName = @"Local\PsDoctor.App";
    private const string ShowEventName = @"Local\PsDoctor.App.Show";

    /// <summary>Первый ли это Doctor. Мьютекс держится до конца процесса вызывающим.</summary>
    public static Mutex Claim(out bool first) => new(true, MutexName, out first);

    /// <summary>Просит первый Doctor показать окно.</summary>
    public static void WakeFirst()
    {
        // Именованных событий вне Windows нет; под Linux окно смотрят глазами, второй экземпляр просто выходит.
        if (!OperatingSystem.IsWindows()) return;
        if (EventWaitHandle.TryOpenExisting(ShowEventName, out var wake))
        {
            using (wake)
            {
                wake.Set();
            }
        }
    }

    /// <summary>Ждёт просьб показать окно в фоновом потоке; <paramref name="show"/> вызывается на нём же.</summary>
    public static void Listen(Action show)
    {
        if (!OperatingSystem.IsWindows()) return;
        var wake = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        new Thread(() =>
        {
            while (wake.WaitOne())
            {
                show();
            }
        }) { IsBackground = true, Name = "psdoctor-single-instance" }.Start();
    }
}
