using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PsDoctor.Core.Observation;
using static PsDoctor.Infrastructure.Observation.Win32Job;
using static PsDoctor.Infrastructure.Observation.Win32Windows;

namespace PsDoctor.Infrastructure.Observation;

/// <summary>
/// Взгляд App на ProShow без наблюдателя: процессы куста, главное окно и признак «не отвечает», счётчики процессора и
/// ввода-вывода. Только чтение: окну не шлётся ни одного сообщения, поэтому проба не виснет вместе с ProShow. Главное
/// окно узнаётся по тому же правилу, что в <see cref="WindowWatcher"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ProShowProbe
{
    private const uint ProcessTerminate = 0x0001;
    private const uint WaitObject0 = 0;

    /// <summary>Сколько ждать выхода завершённого процесса.</summary>
    private const uint TerminateWaitMs = 5000;

    public static ProShowSnapshot Read()
    {
        var processes = new List<ProShowProcess>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string name;
                try { name = process.ProcessName; }
                catch (InvalidOperationException) { continue; }
                if (ProShowImages.RoleOf(name) is { } role)
                {
                    processes.Add(Describe(role, process.Id));
                }
            }
        }
        if (processes.Count == 0)
        {
            return ProShowSnapshot.NotRunning;
        }

        var main = processes.Where(p => p.Role == ProShowRole.Main).Select(p => p.ProcessId).ToHashSet();
        var kin = processes.Select(p => p.ProcessId).ToHashSet();
        var mainHandle = IntPtr.Zero;
        var renderWindow = false;
        foreach (var hwnd in TopLevelWindows())
        {
            GetWindowThreadProcessId(hwnd, out var processId);
            if (!kin.Contains(processId) || !IsWindowVisible(hwnd))
            {
                continue;
            }
            var title = Text(hwnd);
            if (title == ProShowWindows.RenderingWindow)
            {
                renderWindow = true;
            }
            else if (mainHandle == IntPtr.Zero && main.Contains(processId) && title.Length > 0
                && GetWindow(hwnd, GwOwner) == IntPtr.Zero && !WindowWatcher.IsOwnerlessMessage(hwnd))
            {
                mainHandle = hwnd;
            }
        }

        var state = mainHandle == IntPtr.Zero ? ProShowState.NoWindow
            : IsHungAppWindow(mainHandle) ? ProShowState.Hung
            : ProShowState.Responding;
        // Рендер — только по окну: device-enc (ffmpeg) ProShow запускает и для декодирования звука и видео при
        // загрузке проекта — стенд 29.09.2026, e62-field-001-busy.
        return new ProShowSnapshot(state, renderWindow, processes);
    }

    /// <summary>
    /// Завершает процесс, если он всё тот же: номер и время создания сверяются по открытому описателю, им же процесс
    /// и завершается, поэтому номер, доставшийся другому процессу, не пострадает. Ждёт выхода до 5 с.
    /// </summary>
    public static bool Terminate(ProShowProcess target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.StartedUtc is not { } started)
        {
            return false;
        }
        var handle = OpenProcess(ProcessTerminate | ProcessQueryLimitedInformation | Synchronize, false, target.ProcessId);
        if (handle == IntPtr.Zero)
        {
            return false;
        }
        try
        {
            if (!GetProcessTimes(handle, out var created, out _, out _, out _) || DateTime.FromFileTimeUtc(created) != started)
            {
                return false;
            }
            return TerminateProcess(handle, 1) && WaitForSingleObject(handle, TerminateWaitMs) == WaitObject0;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static ProShowProcess Describe(ProShowRole role, int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero)
        {
            return new ProShowProcess(role, processId, null, TimeSpan.Zero, 0);
        }
        try
        {
            if (!GetProcessTimes(handle, out var created, out _, out var kernel, out var user))
            {
                return new ProShowProcess(role, processId, null, TimeSpan.Zero, 0);
            }
            var io = GetProcessIoCounters(handle, out var counters)
                ? (long)(counters.ReadTransferCount + counters.WriteTransferCount)
                : 0;
            return new ProShowProcess(role, processId, DateTime.FromFileTimeUtc(created), TimeSpan.FromTicks(kernel + user), io);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
}

/// <summary>Имена образов куста ProShow и их роли.</summary>
public static class ProShowImages
{
    /// <summary>
    /// Роль процесса по имени образа без расширения; не из куста — <c>null</c>. Те же образы, что
    /// <see cref="ProShowLauncher.ImageNames"/>, но кодировщик узнаётся по началу имени: так видно и <c>device-encp</c>.
    /// </summary>
    public static ProShowRole? RoleOf(string name) =>
        name.Equals("proshow", StringComparison.OrdinalIgnoreCase) ? ProShowRole.Main
        : name.Equals("fvideo", StringComparison.OrdinalIgnoreCase) ? ProShowRole.Decoder
        : name.StartsWith("device-enc", StringComparison.OrdinalIgnoreCase) ? ProShowRole.Encoder
        : null;
}
