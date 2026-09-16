using PsDoctor.Core.Media;
using PsDoctor.Core.Model;

namespace PsDoctor.Infrastructure;

/// <summary>
/// Опрос медиафайлов рядом с проектом: найти по ссылке, прочитать заголовок, вернуть размер.
/// </summary>
/// <remarks>
/// Ни одна неудача не бросает исключения: не нашли, не опознали, не дали прочитать —
/// всё это состояния, которые ложатся в отчёт числом. Сумма при этом считается по опрошенным,
/// а число неопрошенных стоит рядом: сумма без этого числа соврёт.
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

    private readonly string _projectDirectory;

    public FileMediaProbe(string projectDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        _projectDirectory = projectDirectory;
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
