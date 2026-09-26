using PsDoctor.Core.Model;

namespace PsDoctor.Core.Media;

/// <summary>Размер картинки в пикселях.</summary>
public readonly record struct PixelSize(int WidthPx, int HeightPx)
{
    /// <summary>
    /// Сколько байт займёт распакованная картинка: четыре байта на пиксель.
    /// Это и есть главное число продукта — оно бьёт в потолок адресного пространства,
    /// а не байты файла на диске.
    /// </summary>
    public long UnpackedBytes => (long)WidthPx * HeightPx * 4;

    /// <summary>Тот же размер, вписанный в потолок с сохранением пропорций. Увеличение не делается.</summary>
    public PixelSize CappedTo(int maxWidthPx, int maxHeightPx)
    {
        if (WidthPx <= 0 || HeightPx <= 0)
        {
            return this;
        }

        var scale = Math.Min(1.0, Math.Min((double)maxWidthPx / WidthPx, (double)maxHeightPx / HeightPx));

        return scale >= 1.0
            ? this
            : new PixelSize(Math.Max(1, (int)Math.Round(WidthPx * scale)), Math.Max(1, (int)Math.Round(HeightPx * scale)));
    }

    public override string ToString() => $"{WidthPx}×{HeightPx}";
}

/// <summary>
/// Чем кончился опрос медиафайла. Ошибка здесь — состояние, а не исключение:
/// доктор существует ради сломанных проектов, и падать на них ему нельзя.
/// </summary>
public enum MediaProbeStatus
{
    /// <summary>Не опрашивали. Для видео это честный ответ, а не «неизвестно»; причина — рядом.</summary>
    NotProbed,

    /// <summary>Размер картинки или параметры видео сняты.</summary>
    Ok,

    /// <summary>Файла по ссылке нет.</summary>
    NotFound,

    /// <summary>Сигнатура не опознана.</summary>
    UnknownFormat,

    /// <summary>Сигнатура своя, а заголовок оборван или числа в нём бессмысленны.</summary>
    BrokenHeader,

    /// <summary>Файл есть, но прочитать не дали.</summary>
    Unreadable,
}

/// <summary>
/// Параметры видео, снятые внешним опросом. Размер кадра здесь, а не в <see cref="MediaProbe.Size"/>:
/// правила о картинках и сумма распакованных пикселей считаются по неподвижным кадрам, и видео
/// в них попадать не должно.
/// </summary>
/// <param name="Codec">Кодек, как его называет опрос: <c>h264</c>, <c>mpeg4</c>, <c>mjpeg</c>.</param>
/// <param name="Profile">Профиль кодека, если он есть.</param>
/// <param name="Container">Контейнер, как его называет опрос; бывает списком через запятую.</param>
/// <param name="FrameRateMilliFps">Заявленная частота кадров потока. По ней сравниваются видео проекта.</param>
/// <param name="AverageFrameRateMilliFps">Средняя частота: число кадров, делённое на длительность.</param>
/// <param name="VariableFrameRate">
/// Признак переменной частоты: средняя расходится с заявленной больше чем на процент.
/// Это оценка по двум числам, а не разбор меток времени каждого кадра.
/// </param>
/// <param name="DurationMs">Длительность потока, а без неё — контейнера.</param>
/// <param name="BitRateBitsPerSecond">Битрейт потока, а без него — контейнера.</param>
public sealed record VideoParameters(
    string? Codec,
    string? Profile,
    string? Container,
    int? WidthPx,
    int? HeightPx,
    int? FrameRateMilliFps,
    int? AverageFrameRateMilliFps,
    bool? VariableFrameRate,
    int? DurationMs,
    long? BitRateBitsPerSecond);

/// <summary>Почему видео не опрошено. Строки устойчивы: по ним считается сводка пачки.</summary>
public static class NotProbedReasons
{
    /// <summary>Опросчика нет: ffprobe не найден и не назначен.</summary>
    public const string NoFfprobe = "noFfprobe";

    /// <summary>Опросчик запустился и ответил ошибкой или ответом, который не разобрать.</summary>
    public const string FfprobeFailed = "ffprobeFailed";

    /// <summary>Опросчик не уложился в отведённое время и снят.</summary>
    public const string FfprobeTimeout = "ffprobeTimeout";

    /// <summary>Контейнер видео, а видеопотока в нём нет: звук или одна обложка.</summary>
    public const string NoVideoStream = "noVideoStream";
}

/// <summary>
/// Кодеки, которые программа ест тяжело. Список — данные, и он пуст: какой кодек тяжёлый,
/// решают опыты Э3.1, а не догадка. Пока он пуст, признак в отчёте всегда ложен.
/// </summary>
public static class HeavyVideoCodecs
{
    public static IReadOnlySet<string> Names { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public static bool Contains(string? codec) => codec is not null && Names.Contains(codec);
}

/// <summary>Итог опроса одного медиафайла.</summary>
/// <param name="Status">Чем кончилось.</param>
/// <param name="Size">Размер, если он снят.</param>
/// <param name="FileBytes">Размер файла на диске, если файл найден.</param>
/// <param name="FormatId">Формат по сигнатуре, а не по расширению.</param>
/// <param name="ExtensionMatchesFormat">Совпало ли расширение с настоящим форматом. Расширение врёт регулярно.</param>
/// <param name="ResolvedBy">Как нашли файл: по ссылке как есть или перебором без учёта регистра.</param>
/// <param name="Video">Параметры видео, если оно измерено.</param>
/// <param name="NotProbedReason">Почему видео не опрошено; одна из <see cref="NotProbedReasons"/>.</param>
public sealed record MediaProbe(
    MediaProbeStatus Status,
    PixelSize? Size = null,
    long? FileBytes = null,
    string? FormatId = null,
    bool? ExtensionMatchesFormat = null,
    string? ResolvedBy = null,
    VideoParameters? Video = null,
    string? NotProbedReason = null)
{
    public static readonly MediaProbe NotProbed = new(MediaProbeStatus.NotProbed);

    public static readonly MediaProbe NotFound = new(MediaProbeStatus.NotFound);

    public bool HasSize => Status == MediaProbeStatus.Ok && Size is not null;
}

/// <summary>
/// Опрос медиафайла. Порт объявлен в ядре, чтение живёт в инфраструктуре:
/// в ядро не попадает ничего, что открывает файл.
/// </summary>
public interface IMediaProbe
{
    MediaProbe Probe(MediaReference reference);
}

/// <summary>
/// Готовая таблица опрошенного. Инвентарь принимает её, а не порт: тогда он остаётся
/// чистым и проверяется словарём, без единого файла на диске.
/// </summary>
/// <remarks>
/// Шов проходит там же, где решённое деление осмотра на дешёвую и дорогую ступени,
/// и потому переделывать его не придётся.
/// </remarks>
public sealed class MediaCatalog
{
    private readonly Dictionary<MediaReference, MediaProbe> _probes;

    public MediaCatalog(IReadOnlyDictionary<MediaReference, MediaProbe> probes)
    {
        ArgumentNullException.ThrowIfNull(probes);
        _probes = new Dictionary<MediaReference, MediaProbe>(probes);
    }

    public static MediaCatalog Empty { get; } = new(new Dictionary<MediaReference, MediaProbe>());

    public int Count => _probes.Count;

    public IEnumerable<KeyValuePair<MediaReference, MediaProbe>> All => _probes;

    /// <summary>Что известно про ссылку. Не опрашивали — так и сказано.</summary>
    public MediaProbe this[MediaReference reference] =>
        _probes.TryGetValue(reference, out var probe) ? probe : MediaProbe.NotProbed;
}
