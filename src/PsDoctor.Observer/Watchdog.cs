using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PsDoctor.Core.Observation;
using PsDoctor.Infrastructure.Installation;

namespace PsDoctor.Observer;

/// <summary>
/// Сторож установленного Doctor. Сам запускает наблюдатель дочерним процессом, опрашивает <c>/health</c> и
/// перезапускает его, если тот упал или не ответил несколько раз подряд. Наблюдатель не отдельная задача
/// планировщика, а ребёнок сторожа: обычному пользователю перезапускать чужую задачу не всегда позволено,
/// а свой процесс — всегда.
/// </summary>
/// <remarks>
/// Убивается только сам наблюдатель, не дерево: ProShow, запущенный наблюдателем, — его дочерний процесс,
/// и он обязан пережить перезапуск. Помощника ETW сторож не трогает — это процесс с правами администратора.
/// </remarks>
public sealed class Watchdog
{
    private const long LogLimit = 1024 * 1024;

    private readonly InstalledLayout layout;
    private readonly string observerPath;
    private readonly Func<DateTimeOffset> now;

    public Watchdog(InstalledLayout layout, string observerPath, Func<DateTimeOffset>? now = null)
    {
        this.layout = layout;
        this.observerPath = observerPath;
        this.now = now ?? (() => DateTimeOffset.Now);
    }

    private string PidFile => Path.Combine(Path.GetDirectoryName(layout.WatchdogLog)!, "observer.pid");

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(layout.WatchdogLog)!);
        var build = BuildInfo.Of(typeof(Watchdog).Assembly);
        var (settings, error) = layout.LoadSettings();
        if (settings is null)
        {
            // Без настроек наблюдателя нет: App скажет «позовите администратора», а причина — здесь.
            Write("settings-invalid", new { error });
            Console.Error.WriteLine(error);
            return 2;
        }
        var key = layout.EnsureKey();
        Write("watchdog-started", new { version = build.Version, commit = build.Commit, listen = settings.Listen });
        StopOrphan();

        using var http = new HttpClient { BaseAddress = settings.LocalUrl, Timeout = settings.Watchdog.Timeout };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);

        while (!cancellationToken.IsCancellationRequested)
        {
            using var observer = Start(settings);
            Write("observer-started", new { pid = observer.Id });
            var misses = 0;
            string reason;
            while (true)
            {
                try
                {
                    await Task.Delay(settings.Watchdog.Poll, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return 0;
                }
                if (observer.HasExited)
                {
                    reason = "exited";
                    break;
                }
                misses = await AnswersAsync(http, cancellationToken).ConfigureAwait(false) ? 0 : misses + 1;
                if (misses >= settings.Watchdog.Misses)
                {
                    reason = "no-health";
                    break;
                }
            }

            int? exitCode = null;
            if (observer.HasExited)
            {
                exitCode = observer.ExitCode;
            }
            else
            {
                try
                {
                    observer.Kill(entireProcessTree: false);
                    observer.WaitForExit(TimeSpan.FromSeconds(10));
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Процесс успел выйти сам между опросом и остановкой.
                }
            }
            Write("observer-restarted", new { reason, pid = observer.Id, exitCode, misses });
        }
        return 0;
    }

    private static async Task<bool> AnswersAsync(HttpClient http, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(ObserverRoutes.Health, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private Process Start(InstalledSettings settings)
    {
        var start = new ProcessStartInfo(observerPath) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in ObserverArguments(settings, layout))
        {
            start.ArgumentList.Add(argument);
        }
        var process = Process.Start(start) ?? throw new InvalidOperationException("наблюдатель не запустился");
        File.WriteAllText(PidFile, process.Id.ToString(CultureInfo.InvariantCulture));
        return process;
    }

    /// <summary>Ключи наблюдателя из настроек: адрес, ключ, каталог сеансов и пределы хранения.</summary>
    public static IReadOnlyList<string> ObserverArguments(InstalledSettings settings, InstalledLayout layout)
    {
        var arguments = new List<string> { "--listen", settings.Listen };
        if (settings.AllowRemote)
        {
            arguments.Add("--allow-remote");
        }
        arguments.AddRange([
            "--key-file", layout.KeyFile,
            "--data", layout.DataDirectory,
            "--keep-days", settings.Retention.Age.TotalDays.ToString(CultureInfo.InvariantCulture),
            "--keep-mb", (settings.Retention.Bytes / (1024.0 * 1024)).ToString(CultureInfo.InvariantCulture),
            "--keep-marked-days", settings.Retention.MarkedAge.TotalDays.ToString(CultureInfo.InvariantCulture),
        ]);
        return arguments;
    }

    /// <summary>
    /// Наблюдатель, оставшийся от прежнего сторожа, держит порт, и новый не поднимется. Он узнаётся по номеру
    /// из файла, пути exe и времени старта: чужой процесс с тем же номером не трогается.
    /// </summary>
    private void StopOrphan()
    {
        try
        {
            if (!File.Exists(PidFile) || !int.TryParse(File.ReadAllText(PidFile).Trim(), CultureInfo.InvariantCulture, out var pid))
            {
                return;
            }
            using var orphan = Process.GetProcessById(pid);
            // Номер после перезагрузки мог достаться помощнику ETW — тот же exe. Наш наблюдатель старше файла с номером.
            if (!string.Equals(orphan.MainModule?.FileName, observerPath, StringComparison.OrdinalIgnoreCase)
                || orphan.StartTime.ToUniversalTime() > File.GetLastWriteTimeUtc(PidFile))
            {
                return;
            }
            orphan.Kill(entireProcessTree: false);
            orphan.WaitForExit(TimeSpan.FromSeconds(10));
            Write("orphan-stopped", new { pid });
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException
            or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            // Процесса уже нет или он чужой — остановить нечего.
        }
    }

    /// <summary>Строка журнала сторожа: время, событие, данные. Слов для человека нет, как и в фактах наблюдателя.</summary>
    private void Write(string kind, object data)
    {
        try
        {
            var log = new FileInfo(layout.WatchdogLog);
            if (log.Exists && log.Length > LogLimit)
            {
                File.Move(log.FullName, log.FullName + ".old", overwrite: true);
            }
            var line = JsonSerializer.Serialize(new { time = now(), kind, data }, ObservationJson.Options);
            File.AppendAllText(layout.WatchdogLog, line + "\n", new UTF8Encoding(false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Журнал сторожа не повод перестать сторожить.
        }
    }
}
