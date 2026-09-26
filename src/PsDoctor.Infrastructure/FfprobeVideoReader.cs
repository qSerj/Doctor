using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using PsDoctor.Core.Media;

namespace PsDoctor.Infrastructure;

/// <summary>Итог опроса видео: параметры либо причина, по которой их нет.</summary>
public readonly record struct VideoReading(VideoParameters? Video, string? NotProbedReason)
{
    public static VideoReading Failed(string reason) => new(null, reason);
}

/// <summary>
/// Опрос видео через <c>ffprobe</c>: кодек, профиль, частота кадров, кадр, длительность, битрейт.
/// </summary>
/// <remarks>
/// Своего разбора контейнеров в доктор не будет: форматов десятки, и каждый — отдельная читалка
/// с отдельными ошибками. Опросчик внешний, числа отдаются ядру готовыми.
/// <para>
/// Ни одна неудача не бросает исключения: нет опросчика, не запустился, не уложился в срок,
/// ответил ерундой — всё это причина «не опрашивали», которая ложится в отчёт рядом с файлом.
/// </para>
/// </remarks>
public sealed class FfprobeVideoReader
{
    /// <summary>Сколько ждать одного ответа. Заголовок читается за доли секунды; минуты значат зависание.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Насколько средняя частота может разойтись с заявленной, прежде чем видео назовётся переменным.
    /// Процент взят с запасом на округление длительности: у ровного ролика расхождение — сотые доли.
    /// </summary>
    private const double VariableRateTolerance = 0.01;

    /// <summary>
    /// Заявленная частота, начиная с которой при неизвестной средней она считается шкалой времени
    /// контейнера. Найдено на хранилище 26.09.2026: четыре ролика wmv3 в asf с «1000 кадров/с».
    /// </summary>
    private const int TimeBaseRateMilliFps = 1_000_000;

    private readonly TimeSpan _timeout;

    public FfprobeVideoReader(string executablePath, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ExecutablePath = executablePath;
        _timeout = timeout ?? DefaultTimeout;
    }

    public string ExecutablePath { get; }

    private static string ExecutableName => OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";

    /// <summary>
    /// Находит опросчик: назначенный путь, если он задан, иначе рядом с доктором, иначе в <c>PATH</c>.
    /// Назначенный и отсутствующий не подменяется найденным где-то ещё — это разные события,
    /// и вызывающий должен о нём сказать.
    /// </summary>
    public static FfprobeVideoReader? Locate(string? assignedPath = null)
    {
        if (!string.IsNullOrWhiteSpace(assignedPath))
        {
            return File.Exists(assignedPath) ? new FfprobeVideoReader(Path.GetFullPath(assignedPath)) : null;
        }

        var beside = Path.Combine(AppContext.BaseDirectory, ExecutableName);
        if (File.Exists(beside))
        {
            return new FfprobeVideoReader(beside);
        }

        var searchPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in searchPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;
            try
            {
                candidate = Path.Combine(directory.Trim('"'), ExecutableName);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (File.Exists(candidate))
            {
                return new FfprobeVideoReader(candidate);
            }
        }

        return null;
    }

    public VideoReading Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var start = new ProcessStartInfo(ExecutablePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // Полный путь: относительный, начинающийся с минуса, опросчик принял бы за ключ.
        foreach (var argument in new[]
                 {
                     "-v", "error",
                     "-print_format", "json",
                     "-show_format",
                     "-show_streams",
                     "-select_streams", "v",
                     Path.GetFullPath(path),
                 })
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return VideoReading.Failed(NotProbedReasons.FfprobeFailed);
            }

            // Оба потока читаются сразу: иначе заполненный канал ошибок останавливает опросчик.
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(_timeout))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Успел завершиться сам между проверкой и снятием.
                }

                return VideoReading.Failed(NotProbedReasons.FfprobeTimeout);
            }

            Task.WaitAll(output, errors);

            return process.ExitCode == 0
                ? ParseOutput(output.Result)
                : VideoReading.Failed(NotProbedReasons.FfprobeFailed);
        }
        catch (Exception e) when (e is Win32Exception or IOException or InvalidOperationException)
        {
            return VideoReading.Failed(NotProbedReasons.FfprobeFailed);
        }
    }

    /// <summary>
    /// Разбор ответа <c>ffprobe -print_format json -show_format -show_streams -select_streams v</c>.
    /// Отдельно от запуска, чтобы проверяться строкой, без опросчика на машине.
    /// </summary>
    public static VideoReading ParseOutput(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return VideoReading.Failed(NotProbedReasons.FfprobeFailed);
            }

            var format = root.TryGetProperty("format", out var f) && f.ValueKind == JsonValueKind.Object ? f : (JsonElement?)null;
            var stream = FirstVideoStream(root);

            if (stream is not { } video)
            {
                return VideoReading.Failed(NotProbedReasons.NoVideoStream);
            }

            var average = Rate(video, "avg_frame_rate");
            var rate = Rate(video, "r_frame_rate");

            // Без средней и с тысячей кадров и выше заявлена не частота, а шкала времени контейнера:
            // так опросчик отвечает на wmv в asf. Сравнивать по ней нельзя.
            if (average is null && rate >= TimeBaseRateMilliFps)
            {
                rate = null;
            }

            bool? variable = rate is > 0 && average is > 0
                ? Math.Abs(average.Value - rate.Value) > rate.Value * VariableRateTolerance
                : null;

            var seconds = Number(video, "duration") ?? (format is { } fd ? Number(fd, "duration") : null);
            var bitRate = Number(video, "bit_rate") ?? (format is { } fb ? Number(fb, "bit_rate") : null);

            return new VideoReading(
                new VideoParameters(
                    Text(video, "codec_name"),
                    Text(video, "profile"),
                    format is { } fc ? Text(fc, "format_name") : null,
                    Integer(video, "width"),
                    Integer(video, "height"),
                    rate,
                    average,
                    variable,
                    seconds is >= 0 and < int.MaxValue / 1000.0 ? (int)Math.Round(seconds.Value * 1000) : null,
                    bitRate is > 0 and < 1e18 ? (long)bitRate.Value : null),
                null);
        }
        catch (JsonException)
        {
            return VideoReading.Failed(NotProbedReasons.FfprobeFailed);
        }
    }

    /// <summary>
    /// Первый настоящий видеопоток. Обложка в звуковом файле тоже числится видеопотоком,
    /// но роликом не является.
    /// </summary>
    private static JsonElement? FirstVideoStream(JsonElement root)
    {
        if (!root.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var stream in streams.EnumerateArray())
        {
            if (stream.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (Text(stream, "codec_type") is { } type && type != "video")
            {
                continue;
            }

            if (stream.TryGetProperty("disposition", out var disposition)
                && disposition.ValueKind == JsonValueKind.Object
                && Integer(disposition, "attached_pic") == 1)
            {
                continue;
            }

            return stream;
        }

        return null;
    }

    /// <summary>Частота вида «30000/1001» в тысячных кадра. «0/0» значит «не знаю».</summary>
    private static int? Rate(JsonElement element, string name)
    {
        if (Text(element, name) is not { } value)
        {
            return null;
        }

        var slash = value.IndexOf('/', StringComparison.Ordinal);
        var numeratorText = slash < 0 ? value : value[..slash];
        var denominatorText = slash < 0 ? "1" : value[(slash + 1)..];

        if (!double.TryParse(numeratorText, NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            || !double.TryParse(denominatorText, NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator)
            || numerator <= 0
            || denominator <= 0)
        {
            return null;
        }

        var milli = Math.Round(numerator * 1000 / denominator);
        return milli is > 0 and < int.MaxValue ? (int)milli : null;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    /// <summary>Число, которое опросчик отдаёт то числом, то строкой, а при незнании — «N/A».</summary>
    private static double? Number(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    private static int? Integer(JsonElement element, string name) =>
        Number(element, name) is { } value && value is >= -2147483648.0 and <= 2147483647.0 ? (int)value : null;
}
