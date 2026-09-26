using PsDoctor.Core.Media;
using PsDoctor.Core.Model;

namespace PsDoctor.Infrastructure;

/// <summary>
/// Опрос медиафайлов рядом с проектом: найти по ссылке, прочитать заголовок, вернуть размер,
/// а для видео — спросить внешний опросчик.
/// </summary>
/// <remarks>
/// Ни одна неудача не бросает исключения: не нашли, не опознали, не дали прочитать —
/// всё это состояния, которые ложатся в отчёт числом. Сумма при этом считается по опрошенным,
/// а число неопрошенных стоит рядом: сумма без этого числа соврёт.
/// <para>
/// Видео измеряется, только если опросчик передан. Без него видео остаётся «не опрашивали»
/// с причиной — так же, как было до Э1.1.
/// </para>
/// </remarks>
public sealed class FileMediaProbe : IMediaProbe
{
    /// <summary>
    /// Расширения, которые считаются согласными с опознанным форматом.
    /// Проверка нужна не ради придирки: расширение врёт регулярно, и несовпадение — это факт.
    /// </summary>
    private static readonly Dictionary<string, string[]> MatchingExtensions = new(StringComparer.Ordinal)
    {
        ["png"] = ["png"],
        ["jpeg"] = ["jpg", "jpeg", "jpe"],
        ["psd"] = ["psd"],
        ["psb"] = ["psb"],
        ["gif"] = ["gif"],
        ["bmp"] = ["bmp"],

        // mov и mp4 — один и тот же контейнер ISO, и различать их по сигнатуре смысла нет.
        ["mp4"] = ["mp4", "m4v", "mov", "qt"],
        ["avi"] = ["avi"],
    };

    /// <summary>
    /// Расширения видео, которых читалки заголовков не опознают, а опросчик возьмёт:
    /// съёмка камер, DVD, Windows Media. Файл с таким расширением и неопознанной сигнатурой
    /// отдаётся опросчику; всё прочее неопознанное так и остаётся неопознанным.
    /// </summary>
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.Ordinal)
    {
        "mp4", "m4v", "mov", "qt", "avi", "mts", "m2ts", "ts", "mpg", "mpeg", "vob",
        "wmv", "asf", "mkv", "webm", "flv", "3gp", "mod", "tod", "dv",
    };

    private readonly string _projectDirectory;

    private readonly FfprobeVideoReader? _video;

    public FileMediaProbe(string projectDirectory, FfprobeVideoReader? video = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        _projectDirectory = projectDirectory;
        _video = video;
    }

    public MediaProbe Probe(MediaReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);

        var resolved = Resolve(reference);
        if (resolved is null)
        {
            return MediaProbe.NotFound;
        }

        var (path, resolvedBy) = resolved.Value;

        try
        {
            var bytes = new FileInfo(path).Length;

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096);

            var reading = MediaHeaderReader.Read(stream);

            if (reading.Status == MediaProbeStatus.NotProbed
                || (reading.Status == MediaProbeStatus.UnknownFormat && VideoExtensions.Contains(reference.Extension)))
            {
                stream.Dispose();
                return ProbeVideo(path, reading, bytes, reference, resolvedBy);
            }

            return new MediaProbe(
                reading.Status,
                reading.Size,
                bytes,
                reading.FormatId,
                Matches(reading.FormatId, reference.Extension),
                resolvedBy);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new MediaProbe(MediaProbeStatus.Unreadable, ResolvedBy: resolvedBy);
        }
    }

    /// <summary>
    /// Видео: опознанное по сигнатуре или с видеорасширением. Измеренное получает статус «снято»
    /// без размера картинки — кадр лежит в параметрах видео. Неизмеренное опознанное остаётся
    /// «не опрашивали» с причиной, неопознанное — неопознанным.
    /// </summary>
    private MediaProbe ProbeVideo(string path, HeaderReading reading, long bytes, MediaReference reference, string resolvedBy)
    {
        var matches = Matches(reading.FormatId, reference.Extension);

        var video = _video is null
            ? VideoReading.Failed(NotProbedReasons.NoFfprobe)
            : _video.Read(path);

        if (video.Video is { } parameters)
        {
            return new MediaProbe(MediaProbeStatus.Ok, null, bytes, reading.FormatId, matches, resolvedBy, parameters);
        }

        return new MediaProbe(
            reading.Status,
            null,
            bytes,
            reading.FormatId,
            matches,
            resolvedBy,
            NotProbedReason: reading.Status == MediaProbeStatus.NotProbed ? video.NotProbedReason : null);
    }

    /// <summary>Опрашивает все ссылки разом и складывает готовый каталог для инвентаря.</summary>
    public MediaCatalog ProbeAll(IEnumerable<MediaReference> references)
    {
        ArgumentNullException.ThrowIfNull(references);

        var probes = new Dictionary<MediaReference, MediaProbe>();
        foreach (var reference in references)
        {
            if (!probes.ContainsKey(reference))
            {
                probes.Add(reference, Probe(reference));
            }
        }

        return new MediaCatalog(probes);
    }

    private static bool? Matches(string? formatId, string extension)
    {
        if (formatId is null)
        {
            return null;
        }

        return MatchingExtensions.TryGetValue(formatId, out var allowed)
            && allowed.Contains(extension, StringComparer.Ordinal);
    }

    private (string Path, string ResolvedBy)? Resolve(MediaReference reference)
    {
        var relative = reference.Raw.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        var direct = Path.Combine(_projectDirectory, relative);

        if (File.Exists(direct))
        {
            return (direct, "direct");
        }

        // Одна попытка без учёта регистра. Это ровно тот класс сбоя, ради которого доктор затевался:
        // проект приехал с чужой машины, где регистр имени был другим.
        var directory = Path.GetDirectoryName(direct);
        var name = Path.GetFileName(direct);

        if (directory is null || name.Length == 0 || !Directory.Exists(directory))
        {
            return null;
        }

        foreach (var candidate in Directory.EnumerateFiles(directory))
        {
            if (string.Equals(Path.GetFileName(candidate), name, StringComparison.OrdinalIgnoreCase))
            {
                return (candidate, "caseInsensitive");
            }
        }

        return null;
    }
}
