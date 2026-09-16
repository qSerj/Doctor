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
    /// <summary>Не опрашивали. Для видео это честный ответ, а не «неизвестно».</summary>
    NotProbed,

    /// <summary>Размер снят.</summary>
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

/// <summary>Итог опроса одного медиафайла.</summary>
/// <param name="Status">Чем кончилось.</param>
/// <param name="Size">Размер, если он снят.</param>
/// <param name="FileBytes">Размер файла на диске, если файл найден.</param>
/// <param name="FormatId">Формат по сигнатуре, а не по расширению.</param>
/// <param name="ExtensionMatchesFormat">Совпало ли расширение с настоящим форматом. Расширение врёт регулярно.</param>
/// <param name="ResolvedBy">Как нашли файл: по ссылке как есть или перебором без учёта регистра.</param>
public sealed record MediaProbe(
    MediaProbeStatus Status,
    PixelSize? Size = null,
    long? FileBytes = null,
    string? FormatId = null,
    bool? ExtensionMatchesFormat = null,
    string? ResolvedBy = null)
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
