using PsDoctor.Core.Media;
using PsDoctor.Core.Model;

namespace PsDoctor.Core.Inventory;

/// <summary>Слайд одной строкой.</summary>
public sealed record SlideLine(
    int Ordinal,
    int? TimeMs,
    int? TransId,
    int? TransTimeMs,
    int LayerCount,
    int TransitionLayerCount,
    int CaptionCount,
    string? TransitionName);

/// <summary>
/// Заменяемые места шаблона тремя числами, а не одним.
/// Одно общее число заставляло бы читателя гадать, входят в него подписи или нет.
/// </summary>
public sealed record ReplaceableCounts(int InSlides, int InTransitions, int InCaptions)
{
    /// <summary>Сумма по слоям. Подписи сюда не входят — они считаются отдельно.</summary>
    public int LayerTotal => InSlides + InTransitions;
}

public sealed record LayerCounts(
    int InSlides,
    int InTransitions,
    int WithMedia,
    int Video,
    int Masking,
    int Adjustment,
    int Gradient,
    ReplaceableCounts Replaceable)
{
    public int Total => InSlides + InTransitions;
}

public sealed record KeyframeCounts(
    int InSlides,
    int InTransitions,
    IReadOnlyDictionary<int, int> PerLayerHistogram,
    int? MaxZoomBp)
{
    public int Total => InSlides + InTransitions;
}

/// <summary>Один медиафайл: то, что о нём сказал файл шоу, и то, что сказал он сам.</summary>
public sealed record MediaLine(
    MediaReference Reference,
    string Extension,
    MediaProbeStatus Status,
    string? FormatId,
    bool? ExtensionMatchesFormat,
    string? ResolvedBy,
    int? WidthPx,
    int? HeightPx,
    long? FileBytes,
    long? UnpackedBytes,
    int ReferenceCount,
    IReadOnlyList<ObjectAddress> Addresses);

public sealed record MediaCounts(
    int UniqueCount,
    int ReferenceCount,
    IReadOnlyDictionary<string, int> ReferencesByExtension,
    int ProbedCount,
    int MissingCount,
    int UnknownFormatCount,
    int BrokenHeaderCount,
    int UnreadableCount,
    int NotProbedCount,
    long UnpackedBytes,
    long UnpackedBytesCapped,
    IReadOnlyList<MediaLine> Items);

public sealed record AudioLine(int Ordinal, MediaReference? File, int? LengthMs, int? StartTimeMs, int? EndTimeMs);

public sealed record FontUse(string FaceName, int RefCount);

/// <summary>
/// Инвентарь проекта: всё, что снято с файла шоу, плюс размеры, снятые с медиафайлов.
/// Строится из готового каталога опрошенного, а не из порта — поэтому остаётся чистым
/// и проверяется словарём, без единого файла на диске.
/// </summary>
public sealed class Inventory
{
    /// <summary>Потолок, с которым считается второе число: обычный кадр 1080p.</summary>
    public const int CapWidthPx = 1920;

    public const int CapHeightPx = 1080;

    private Inventory(
        Show show,
        IReadOnlyList<SlideLine> slides,
        LayerCounts layers,
        KeyframeCounts keyframes,
        MediaCounts media,
        IReadOnlyList<AudioLine> audio,
        IReadOnlyList<FontUse> fonts,
        IReadOnlyDictionary<int, int> modifiersByFunction)
    {
        Show = show;
        Slides = slides;
        Layers = layers;
        Keyframes = keyframes;
        Media = media;
        Audio = audio;
        Fonts = fonts;
        ModifiersByFunctionId = modifiersByFunction;
    }

    public Show Show { get; }

    public IReadOnlyList<SlideLine> Slides { get; }

    public LayerCounts Layers { get; }

    public KeyframeCounts Keyframes { get; }

    public MediaCounts Media { get; }

    public IReadOnlyList<AudioLine> Audio { get; }

    public IReadOnlyList<FontUse> Fonts { get; }

    public IReadOnlyDictionary<int, int> ModifiersByFunctionId { get; }

    public int TotalTimeMs => Show.TotalTimeMs;

    public int TotalTransTimeMs => Show.Slides.Sum(s => s.TransTimeMs ?? 0);

    public int TotalTransitionTimeMs => Show.Slides.Sum(s => s.Transition?.TimeMs ?? 0);

    public static Inventory Build(Show show, MediaCatalog media)
    {
        ArgumentNullException.ThrowIfNull(show);
        ArgumentNullException.ThrowIfNull(media);

        return new Inventory(
            show,
            BuildSlides(show),
            BuildLayerCounts(show),
            BuildKeyframeCounts(show),
            BuildMedia(show, media),
            BuildAudio(show),
            BuildFonts(show),
            BuildModifiers(show));
    }

    private static IReadOnlyList<SlideLine> BuildSlides(Show show) =>
        show.Slides
            .Select(slide => new SlideLine(
                slide.SlideOrdinal,
                slide.TimeMs,
                slide.TransId,
                slide.TransTimeMs,
                slide.DeclaredLayerCount,
                slide.Transition?.DeclaredLayerCount ?? 0,
                slide.DeclaredCaptionCount,
                slide.CustomTransitionName))
            .ToArray();

    private static LayerCounts BuildLayerCounts(Show show)
    {
        var all = show.AllLayers.ToList();

        return new LayerCounts(
            InSlides: show.Slides.Sum(s => s.DeclaredLayerCount),
            InTransitions: show.Slides.Sum(s => s.Transition?.DeclaredLayerCount ?? 0),
            WithMedia: all.Count(l => l.Image is not null),
            Video: all.Count(l => l.IsVideo == true),
            Masking: all.Count(l => l.IsMasking == true),
            Adjustment: all.Count(l => l.IsAdjustment == true),
            Gradient: all.Count(l => l.UsesGradient == true),
            Replaceable: new ReplaceableCounts(
                InSlides: all.Count(l => l.Scene == SceneKind.Slide && l.IsReplaceableTemplate == true),
                InTransitions: all.Count(l => l.Scene == SceneKind.Transition && l.IsReplaceableTemplate == true),
                InCaptions: show.Slides.SelectMany(s => s.Captions).Count(c => c.IsReplaceableTemplate == true)));
    }

    private static KeyframeCounts BuildKeyframeCounts(Show show)
    {
        var histogram = new SortedDictionary<int, int>();
        int? maxZoom = null;
        var inSlides = 0;
        var inTransitions = 0;

        foreach (var layer in show.AllLayers)
        {
            var count = layer.DeclaredKeyframeCount;

            if (layer.Scene == SceneKind.Slide)
            {
                inSlides += count;
            }
            else
            {
                inTransitions += count;
            }

            histogram[count] = histogram.GetValueOrDefault(count) + 1;

            if (layer.MaxZoomBp is { } zoom && (maxZoom is null || zoom > maxZoom))
            {
                maxZoom = zoom;
            }
        }

        return new KeyframeCounts(inSlides, inTransitions, histogram, maxZoom);
    }

    private static MediaCounts BuildMedia(Show show, MediaCatalog catalog)
    {
        var addresses = new Dictionary<MediaReference, List<ObjectAddress>>();
        var byExtension = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var referenceCount = 0;

        foreach (var layer in show.AllLayers)
        {
            if (layer.Image is not { } reference)
            {
                continue;
            }

            referenceCount++;
            byExtension[reference.Extension] = byExtension.GetValueOrDefault(reference.Extension) + 1;

            if (!addresses.TryGetValue(reference, out var list))
            {
                list = [];
                addresses.Add(reference, list);
            }

            list.Add(layer.Address);
        }

        var items = new List<MediaLine>();
        long unpacked = 0;
        long capped = 0;
        int probed = 0, missing = 0, unknown = 0, broken = 0, unreadable = 0, notProbed = 0;

        foreach (var (reference, list) in addresses.OrderBy(p => p.Key.Normalized, StringComparer.Ordinal))
        {
            var probe = catalog[reference];

            switch (probe.Status)
            {
                case MediaProbeStatus.Ok: probed++; break;
                case MediaProbeStatus.NotFound: missing++; break;
                case MediaProbeStatus.UnknownFormat: unknown++; break;
                case MediaProbeStatus.BrokenHeader: broken++; break;
                case MediaProbeStatus.Unreadable: unreadable++; break;
                default: notProbed++; break;
            }

            long? itemUnpacked = null;

            if (probe.Size is { } size)
            {
                // Сумма считается по уникальным файлам, а не по ссылкам: файл грузится один раз,
                // сколько бы слоёв на него ни ссылалось.
                itemUnpacked = size.UnpackedBytes;
                unpacked += size.UnpackedBytes;
                capped += size.CappedTo(CapWidthPx, CapHeightPx).UnpackedBytes;
            }

            items.Add(new MediaLine(
                reference,
                reference.Extension,
                probe.Status,
                probe.FormatId,
                probe.ExtensionMatchesFormat,
                probe.ResolvedBy,
                probe.Size?.WidthPx,
                probe.Size?.HeightPx,
                probe.FileBytes,
                itemUnpacked,
                list.Count,
                list));
        }

        return new MediaCounts(
            addresses.Count,
            referenceCount,
            byExtension,
            probed,
            missing,
            unknown,
            broken,
            unreadable,
            notProbed,
            unpacked,
            capped,
            items);
    }

    private static IReadOnlyList<AudioLine> BuildAudio(Show show) =>
        show.Sounds
            .Select(track => new AudioLine(track.Ordinal, track.File, track.LengthMs, track.StartTimeMs, track.EndTimeMs))
            .ToArray();

    private static IReadOnlyList<FontUse> BuildFonts(Show show)
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);

        foreach (var face in show.Slides.SelectMany(s => s.Captions).Select(c => c.FontFaceName)
                     .Concat(show.CaptionStyles.Select(s => s.FontFaceName)))
        {
            if (face is { Length: > 0 })
            {
                counts[face] = counts.GetValueOrDefault(face) + 1;
            }
        }

        return counts.Select(pair => new FontUse(pair.Key, pair.Value)).ToArray();
    }

    private static IReadOnlyDictionary<int, int> BuildModifiers(Show show)
    {
        var counts = new SortedDictionary<int, int>();

        foreach (var id in show.Modifiers.SelectMany(m => m.ActionFunctionIds))
        {
            counts[id] = counts.GetValueOrDefault(id) + 1;
        }

        return counts;
    }
}
