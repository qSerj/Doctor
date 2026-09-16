using System.Globalization;
using PsDoctor.Core.Format;
using PsDoctor.Core.Media;
using PsDoctor.Core.Model;
using PsDoctor.Core.Rules;
using Учёт = PsDoctor.Core.Inventory.Inventory;

namespace PsDoctor.Core.Reporting;

/// <summary>
/// Сборка машинного отчёта. Живёт в ядре: это преобразование текста, оно не открывает файлов
/// и не спрашивает операционную систему.
/// </summary>
/// <remarks>
/// Про время ядро не знает буквально: и момент сборки, и версия доктора приходят параметрами
/// снаружи, а <c>DateTimeOffset.UtcNow</c> здесь не вызывается никогда.
/// </remarks>
public static class ReportBuilder
{
    /// <summary>
    /// Правило, по которому выбрано целевое разрешение. В отчёте оно словами затем, чтобы
    /// прогоны разных версий доктора можно было отличить, не читая его исходников.
    /// </summary>
    public const string TargetSizeRule = "max(displaySizeX, videoSizeX, outputImageSizeX), высота — пара к победившему";

    /// <summary>Отчёт о файле, который не оказался файлом шоу.</summary>
    public static Report NotAShowFile(
        string? path,
        long? fileBytes,
        string doctorVersion,
        DateTimeOffset producedAtUtc,
        IValueMasker masker)
    {
        ArgumentNullException.ThrowIfNull(masker);

        return new Report(
            Report.SchemaName,
            Report.CurrentSchemaVersion,
            doctorVersion,
            producedAtUtc,
            !masker.AllowsSamples,
            Report.UnitNames,
            new ReportSource(masker.Mask(MaskKind.Path, path), fileBytes, 0, 0, "cp1251", MagicMatched: false),
            new ReportParse(0, []),
            Header: null,
            Inventory: null,
            Dictionary: null,
            Findings: []);
    }

    public static Report Build(
        string? path,
        long? fileBytes,
        ParseResult parse,
        Учёт inventory,
        FormatDictionary dictionary,
        IReadOnlyList<Finding> findings,
        string doctorVersion,
        DateTimeOffset producedAtUtc,
        IValueMasker masker)
    {
        ArgumentNullException.ThrowIfNull(parse);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(dictionary);
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(masker);

        var document = parse.Document!;
        var show = inventory.Show;

        return new Report(
            Report.SchemaName,
            Report.CurrentSchemaVersion,
            doctorVersion,
            producedAtUtc,
            !masker.AllowsSamples,
            Report.UnitNames,
            new ReportSource(
                masker.Mask(MaskKind.Path, path),
                fileBytes,
                document.LineCount,
                document.KeyCount,
                "cp1251",
                parse.MagicMatched),
            BuildParse(parse, masker),
            BuildHeader(show.Header, masker),
            BuildInventory(inventory, masker),
            BuildDictionary(dictionary, masker),
            findings.Select(f => BuildFinding(f, masker)).ToArray());
    }

    private static ReportFinding BuildFinding(Finding finding, IValueMasker masker) =>
        new(
            // Идентификатор правила — наш собственный и обязан быть устойчивым:
            // по нему сравниваются прогоны, собирается статистика и пишутся тесты.
            masker.Keep(finding.RuleId)!,
            BuildAddress(finding.Address, masker),
            masker.Keep(finding.Level.ToString())!,
            masker.Keep(finding.Confidence.ToString())!,
            finding.PassedThreshold,
            finding.Numbers);

    private static ReportParse BuildParse(ParseResult parse, IValueMasker masker) =>
        new(
            parse.Problems.Count,
            parse.Problems
                .Select(p => new ReportProblem(
                    p.LineNumber,
                    masker.Keep(p.Kind.ToString())!,
                    // Ключ — имя из формата, но кривая строка могла принести что угодно, вплоть до пути.
                    masker.Mask(MaskKind.Text, p.RawKey),
                    masker.Mask(MaskKind.Text, p.Detail)!))
                .ToArray());

    private static ReportHeader BuildHeader(ShowHeader header, IValueMasker masker) =>
        new(
            // Версия программы — факт о чужом продукте, а не о клиентской работе.
            masker.Keep(header.ProshowMajorVersion),
            masker.Keep(header.ProshowVersion),
            masker.Mask(MaskKind.Project, header.Title),
            masker.Mask(MaskKind.Path, header.FileName),
            masker.Mask(MaskKind.Path, header.MakeFileLocalFolder),
            header.ShowAspect,
            header.ShowSizeX,
            header.ShowSizeY,
            header.VideoFrameRateMilliFps,
            header.TargetSize is { } size ? new ReportSize(size.WidthPx, size.HeightPx) : null,
            header.TargetSize?.SourceKey,
            TargetSizeRule,
            header.SizeCandidates()
                .Select(c => new ReportSizeCandidate(masker.Keep(c.Key)!, c.WidthPx, c.HeightPx))
                .ToArray());

    private static ReportInventory BuildInventory(Учёт inventory, IValueMasker masker)
    {
        var show = inventory.Show;

        return new ReportInventory(
            new ReportSlides(
                show.DeclaredSlideCount,
                show.Slides.Count,
                inventory.TotalTimeMs,
                inventory.TotalTransTimeMs,
                inventory.TotalTransitionTimeMs,
                inventory.Slides
                    .Select(s => new ReportSlide(
                        s.Ordinal,
                        s.TimeMs,
                        s.TransId,
                        s.TransTimeMs,
                        s.LayerCount,
                        s.TransitionLayerCount,
                        s.CaptionCount,
                        // Имя перехода — почти всегда имя купленного шаблона, но ручное имя тоже бывает.
                        masker.Mask(MaskKind.Name, s.TransitionName)))
                    .ToArray()),
            new ReportLayers(
                inventory.Layers.InSlides,
                inventory.Layers.InTransitions,
                inventory.Layers.Total,
                inventory.Layers.WithMedia,
                inventory.Layers.Video,
                inventory.Layers.Masking,
                inventory.Layers.Adjustment,
                inventory.Layers.Gradient,
                new ReportReplaceable(
                    inventory.Layers.Replaceable.InSlides,
                    inventory.Layers.Replaceable.InTransitions,
                    inventory.Layers.Replaceable.InCaptions,
                    inventory.Layers.Replaceable.LayerTotal)),
            new ReportKeyframes(
                inventory.Keyframes.InSlides,
                inventory.Keyframes.InTransitions,
                inventory.Keyframes.Total,
                inventory.Keyframes.PerLayerHistogram.ToDictionary(
                    p => p.Key.ToString(CultureInfo.InvariantCulture),
                    p => p.Value),
                inventory.Keyframes.MaxZoomBp),
            BuildMedia(inventory, masker),
            new ReportAudio(
                show.DeclaredSoundCount,
                inventory.TotalTimeMs,
                inventory.Audio
                    .Select(t => new ReportAudioTrack(
                        t.Ordinal,
                        masker.Mask(MaskKind.Media, t.File?.Raw),
                        t.LengthMs,
                        t.StartTimeMs,
                        t.EndTimeMs))
                    .ToArray()),
            new ReportCaptions(
                show.DeclaredCaptionStyleCount,
                show.Slides.Sum(s => s.DeclaredCaptionCount),
                inventory.Fonts
                    // Имя гарнитуры — факт о системе, а не о клиентской работе, и оно нужно для вывода.
                    .Select(f => new ReportFont(masker.Keep(f.FaceName)!, f.RefCount))
                    .ToArray()),
            new ReportModifiers(
                show.DeclaredModifierCount,
                inventory.ModifiersByFunctionId.ToDictionary(
                    p => p.Key.ToString(CultureInfo.InvariantCulture),
                    p => p.Value)));
    }

    private static ReportMedia BuildMedia(Учёт inventory, IValueMasker masker)
    {
        var media = inventory.Media;

        return new ReportMedia(
            media.UniqueCount,
            media.ReferenceCount,
            media.ReferencesByExtension.ToDictionary(p => masker.Keep(p.Key)!, p => p.Value),
            media.ProbedCount,
            media.MissingCount,
            media.UnknownFormatCount,
            media.BrokenHeaderCount,
            media.UnreadableCount,
            media.NotProbedCount,
            media.Items.Count(i => i.ExtensionMatchesFormat == false),
            media.UnpackedBytes,
            media.UnpackedBytesCapped,

            // Видео в сумму не входит, и это сказано полем, а не умолчанием.
            VideoUnpackedBytes: null,
            VideoProbeStatus: "notProbed",
            media.Items
                .Select(i => new ReportMediaItem(
                    masker.Mask(MaskKind.Media, i.Reference.Raw)!,
                    masker.Keep(i.Extension)!,
                    i.Status.ToString(),
                    masker.Keep(i.FormatId),
                    i.ExtensionMatchesFormat,
                    masker.Keep(i.ResolvedBy),
                    i.WidthPx,
                    i.HeightPx,
                    i.FileBytes,
                    i.UnpackedBytes,
                    i.ReferenceCount,
                    i.Addresses.Select(a => BuildAddress(a, masker)).ToArray()))
                .ToArray());
    }

    private static ReportAddress BuildAddress(ObjectAddress address, IValueMasker masker)
    {
        // Ключ адреса несёт ссылку на файл, значит в обезличенном срезе он строится из псевдонима.
        // Устойчивым и сравнимым между прогонами он при этом остаётся.
        var key = address.Media is { } media
            ? address.StableKey.Replace(media.Normalized, masker.Mask(MaskKind.Media, media.Normalized), StringComparison.Ordinal)
            : address.StableKey;

        return new ReportAddress(
            key,
            address.Kind.ToString(),
            address.Stability.ToString(),
            address.SlideOrdinal,
            address.Scene?.ToString(),
            address.ObjectId,
            address.Ordinal,
            masker.Mask(MaskKind.Name, address.Name));
    }

    private static ReportDictionary BuildDictionary(FormatDictionary dictionary, IValueMasker masker) =>
        new(
            dictionary.Shapes.Count,
            dictionary.UnknownShapeCount,
            dictionary.Shapes
                .Select(s => new ReportKeyShape(
                    // Форма ключа — имя из чужого формата, клиентских данных в ней нет.
                    masker.Keep(s.Shape)!,
                    s.Count,
                    s.Known,
                    s.Kind.ToString(),
                    s.Min,
                    s.Max,

                    // Образец значения — самый вероятный канал утечки пути или имени.
                    masker.AllowsSamples ? s.Samples : null))
                .ToArray());
}
