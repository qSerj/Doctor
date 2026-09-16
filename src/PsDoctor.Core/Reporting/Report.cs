namespace PsDoctor.Core.Reporting;

/// <summary>
/// Машинный отчёт доктора. Человеческих фраз здесь нет вовсе: слова живут в окне,
/// и две копии формулировок однажды разойдутся.
/// </summary>
/// <remarks>
/// Формат самоописывающийся: у каждого числа названа единица — суффиксом имени плюс
/// словарём <see cref="Units"/> в шапке, — а у отчёта есть версия схемы и версия доктора.
/// Прогоны сравниваются между разными версиями программы, и без версии схемы такое
/// сравнение однажды тихо соврёт.
/// <para>
/// Незаданное пишется как <c>null</c>, а поле не опускается: машинный читатель обязан
/// отличать «доктор такого поля не знает» от «проект этого не задал». Ловушка самого
/// формата файла шоу ровно в этом, и повторять её в своём формате было бы глупо.
/// </para>
/// </remarks>
public sealed record Report(
    string Schema,
    int SchemaVersion,
    string DoctorVersion,
    DateTimeOffset ProducedAtUtc,
    bool Anonymized,
    IReadOnlyDictionary<string, string> Units,
    ReportSource Source,
    ReportParse Parse,
    ReportHeader? Header,
    ReportInventory? Inventory,
    ReportDictionary? Dictionary,
    IReadOnlyList<ReportFinding> Findings)
{
    public const string SchemaName = "psdoctor.report";

    /// <summary>
    /// Версия схемы. Меняется, когда меняется смысл или состав полей, — иначе сравнение
    /// прогонов между версиями доктора однажды соврёт молча.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Что значат суффиксы имён. Названо один раз, а не при каждом числе.</summary>
    public static readonly IReadOnlyDictionary<string, string> UnitNames = new Dictionary<string, string>
    {
        ["Ms"] = "миллисекунды",
        ["Px"] = "пиксели",
        ["Bytes"] = "байты",
        ["Bp"] = "базисные пункты: 10000 — сто процентов",
        ["MilliFps"] = "тысячные кадра в секунду: 29970 — это 29.97",
        ["Count"] = "штуки",
    };
}

public sealed record ReportSource(
    string? Path,
    long? FileBytes,
    int LineCount,
    int KeyCount,
    string EncodingAssumed,
    bool MagicMatched);

public sealed record ReportParse(int ProblemCount, IReadOnlyList<ReportProblem> Problems);

public sealed record ReportProblem(int LineNumber, string Kind, string? Key, string Detail);

public sealed record ReportSize(int WidthPx, int HeightPx);

public sealed record ReportSizeCandidate(string Key, int? WidthPx, int? HeightPx);

public sealed record ReportHeader(
    string? ProshowMajorVersion,
    string? ProshowVersion,
    string? Title,
    string? FileName,
    string? MakeFileLocalFolder,
    int? ShowAspect,
    int? ShowSizeX,
    int? ShowSizeY,
    int? VideoFrameRateMilliFps,
    ReportSize? TargetSizePx,
    string? TargetSizeSource,
    string TargetSizeRule,
    IReadOnlyList<ReportSizeCandidate> SizeCandidates);

public sealed record ReportInventory(
    ReportSlides Slides,
    ReportLayers Layers,
    ReportKeyframes Keyframes,
    ReportMedia Media,
    ReportAudio Audio,
    ReportCaptions Captions,
    ReportModifiers Modifiers);

public sealed record ReportSlides(
    int DeclaredCount,
    int ParsedCount,
    int TotalTimeMs,
    int TotalTransTimeMs,
    int TotalTransitionTimeMs,
    IReadOnlyList<ReportSlide> Items);

public sealed record ReportSlide(
    int Ordinal,
    int? TimeMs,
    int? TransId,
    int? TransTimeMs,
    int LayerCount,
    int TransitionLayerCount,
    int CaptionCount,
    string? TransitionName);

/// <summary>
/// Заменяемые места тремя числами. Одно общее заставляло бы гадать, входят ли в него подписи.
/// </summary>
public sealed record ReportReplaceable(
    int InSlidesCount,
    int InTransitionsCount,
    int InCaptionsCount,
    int LayerTotalCount);

public sealed record ReportLayers(
    int InSlidesCount,
    int InTransitionsCount,
    int TotalCount,
    int WithMediaCount,
    int VideoCount,
    int MaskingCount,
    int AdjustmentCount,
    int GradientCount,
    ReportReplaceable ReplaceableTemplate);

public sealed record ReportKeyframes(
    int InSlidesCount,
    int InTransitionsCount,
    int TotalCount,
    IReadOnlyDictionary<string, int> PerLayerHistogram,
    int? MaxZoomBp);

public sealed record ReportMedia(
    int UniqueCount,
    int ReferenceCount,
    IReadOnlyDictionary<string, int> ReferencesByExtension,
    int ProbedCount,
    int MissingCount,
    int UnknownFormatCount,
    int BrokenHeaderCount,
    int UnreadableCount,
    int NotProbedCount,
    int LyingExtensionCount,
    long UnpackedBytes,
    long UnpackedBytesCappedTo1920x1080,
    long? VideoUnpackedBytes,
    string VideoProbeStatus,
    IReadOnlyList<ReportMediaItem> Items);

public sealed record ReportMediaItem(
    string Reference,
    string Extension,
    string Status,
    string? Format,
    bool? ExtensionMatchesFormat,
    string? ResolvedBy,
    int? WidthPx,
    int? HeightPx,
    long? FileBytes,
    long? UnpackedBytes,
    int ReferenceCount,
    IReadOnlyList<ReportAddress> Addresses);

public sealed record ReportAddress(
    string StableKey,
    string Kind,
    string Stability,
    int? SlideOrdinal,
    string? Scene,
    int? ObjectId,
    int? Ordinal,
    string? Name);

public sealed record ReportAudio(int TrackCount, int ShowDurationMs, IReadOnlyList<ReportAudioTrack> Tracks);

public sealed record ReportAudioTrack(int Ordinal, string? Reference, int? LengthMs, int? StartTimeMs, int? EndTimeMs);

public sealed record ReportCaptions(int StyleCount, int CaptionCount, IReadOnlyList<ReportFont> Fonts);

public sealed record ReportFont(string FaceName, int RefCount);

public sealed record ReportModifiers(int DeclaredCount, IReadOnlyDictionary<string, int> ByFunctionId);

public sealed record ReportDictionary(
    int ShapeCount,
    int UnknownShapeCount,
    IReadOnlyList<ReportKeyShape> Shapes);

public sealed record ReportKeyShape(
    string Shape,
    int Count,
    bool Known,
    string Kind,
    long? Min,
    long? Max,
    IReadOnlyList<string>? Samples);

/// <summary>
/// Находка: устойчивый идентификатор правила, объект, на котором сработало, и числа.
/// На Э1 список всегда пуст, но он есть с первого дня — потребитель, написанный сегодня,
/// не должен переписываться завтра.
/// </summary>
public sealed record ReportFinding(
    string RuleId,
    ReportAddress Address,
    string Level,
    string Confidence,
    bool PassedThreshold,
    IReadOnlyDictionary<string, long> Numbers);
