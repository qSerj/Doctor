using PsDoctor.Core.Media;
using PsDoctor.Core.Model;
using Учёт = PsDoctor.Core.Inventory.Inventory;

namespace PsDoctor.Core.Rules;

/// <summary>Правило приёмки: одна карточка каталога — один класс.</summary>
public interface IAcceptanceRule
{
    /// <summary>Устойчивый идентификатор. По нему сравниваются прогоны и пишутся тесты.</summary>
    string Id { get; }

    ExecutionLevel Level { get; }

    RuleConfidence Confidence { get; }

    IEnumerable<Finding> Check(Учёт inventory, RuleContext context);
}

/// <summary>
/// Шесть правил, которые считаются по готовому инвентарю. Граница этапа проведена по
/// зависимостям: сюда входит то, для чего все числа уже сняты, и ни одной новой не заводится.
/// </summary>
public static class AcceptanceRules
{
    /// <summary>
    /// Зум по умолчанию, когда слой не несёт ни одного ключевого кадра с зумом.
    /// Умолчание объявлено здесь поимённо, а не додумано в модели: единица формата прямо
    /// говорит, что 10000 — это сто процентов, и отсутствие ключа означает обычный размер.
    /// </summary>
    public const int DefaultZoomBp = 10_000;

    public static IReadOnlyList<IAcceptanceRule> All { get; } =
    [
        new OversizedStills(),
        new ZoomExceedsPixels(),
        new OversizedVideo(),
        new UnresolvedPaths(),
        new ForeignRoot(),
        new AudioLongerThanShow(),
    ];

    /// <summary>
    /// Прогоняет все правила. Порядок устойчив между прогонами: список, который перетряхивается
    /// сам, сравнивать между версиями невозможно.
    /// </summary>
    public static IReadOnlyList<Finding> Run(Учёт inventory, RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(context);

        return All
            .SelectMany(rule => rule.Check(inventory, context))
            .OrderBy(f => f.RuleId, StringComparer.Ordinal)
            .ThenBy(f => f.Address.StableKey, StringComparer.Ordinal)
            .ToArray();
    }

    internal static int MaxZoomBp(IEnumerable<Layer> layers) =>
        layers.Select(l => l.MaxZoomBp ?? DefaultZoomBp).DefaultIfEmpty(DefaultZoomBp).Max();

    /// <summary>Размер, вписанный в заданную ширину с сохранением пропорций.</summary>
    internal static PixelSize ScaleToWidth(PixelSize size, int widthPx)
    {
        if (size.WidthPx <= 0 || widthPx >= size.WidthPx)
        {
            return size;
        }

        var height = (int)Math.Round((double)size.HeightPx * widthPx / size.WidthPx);
        return new PixelSize(Math.Max(1, widthPx), Math.Max(1, height));
    }

    /// <summary>Слои, сгруппированные по подключённому файлу.</summary>
    internal static Dictionary<MediaReference, List<Layer>> LayersByMedia(Учёт inventory)
    {
        var map = new Dictionary<MediaReference, List<Layer>>();

        foreach (var layer in inventory.Show.AllLayers)
        {
            if (layer.Image is not { } reference)
            {
                continue;
            }

            if (!map.TryGetValue(reference, out var list))
            {
                list = [];
                map.Add(reference, list);
            }

            list.Add(layer);
        }

        return map;
    }
}

/// <summary>
/// Негабаритные исходники: в файле пикселей больше, чем слою когда-либо понадобится.
/// Это главный рычаг продукта — распакованные пиксели бьют в потолок адресного пространства.
/// </summary>
public sealed class OversizedStills : IAcceptanceRule
{
    public string Id => "oversized-stills";

    public ExecutionLevel Level => ExecutionLevel.Self;

    public RuleConfidence Confidence => RuleConfidence.High;

    public IEnumerable<Finding> Check(Учёт inventory, RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(context);

        if (inventory.Show.Header.TargetSize is not { } output)
        {
            yield break;
        }

        var byMedia = AcceptanceRules.LayersByMedia(inventory);

        foreach (var item in inventory.Media.Items)
        {
            if (item.WidthPx is not { } width || item.HeightPx is not { } height)
            {
                continue;
            }

            if (!byMedia.TryGetValue(item.Reference, out var layers))
            {
                continue;
            }

            var zoom = AcceptanceRules.MaxZoomBp(layers);

            // Целевое разрешение = ширина вывода × максимальный зум × запас.
            var target = (int)Math.Round(output.WidthPx * (zoom / (double)AcceptanceRules.DefaultZoomBp) * context.Settings.SizeReserve);

            if (target <= 0 || width <= target)
            {
                continue;
            }

            var current = new PixelSize(width, height);
            var scaled = AcceptanceRules.ScaleToWidth(current, target);

            yield return new Finding(
                Id,
                ObjectAddress.ForMedia(item.Reference),
                Level,
                Confidence,
                PassedThreshold: false,
                new Dictionary<string, long>
                {
                    ["currentWidthPx"] = width,
                    ["currentHeightPx"] = height,
                    ["targetWidthPx"] = scaled.WidthPx,
                    ["targetHeightPx"] = scaled.HeightPx,
                    ["maxZoomBp"] = zoom,
                    ["outputWidthPx"] = output.WidthPx,
                    ["unpackedBytes"] = current.UnpackedBytes,
                    ["targetUnpackedBytes"] = scaled.UnpackedBytes,
                    ["savingBytes"] = current.UnpackedBytes - scaled.UnpackedBytes,
                    ["layerCount"] = layers.Count,
                });
        }
    }
}

/// <summary>
/// Сторожевое правило: слой тянут крупнее, чем есть в подключённом файле.
/// </summary>
/// <remarks>
/// Закрывает опасность, которую иначе создаёт само лечение: уменьшили по вчерашнему зуму,
/// сегодня подняли зум — на телевизоре артефакты. Проверяется у всех слоёв с растровым файлом,
/// включая слои внутри переходов; заливки и градиенты пропускаются — у них нет исходного размера.
/// <para>
/// Запас здесь не участвует: вопрос не «сколько взять с запасом», а «хватает ли вообще».
/// </para>
/// </remarks>
public sealed class ZoomExceedsPixels : IAcceptanceRule
{
    public string Id => "zoom-exceeds-pixels";

    public ExecutionLevel Level => ExecutionLevel.Self;

    public RuleConfidence Confidence => RuleConfidence.High;

    public IEnumerable<Finding> Check(Учёт inventory, RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(context);

        if (inventory.Show.Header.TargetSize is not { } output)
        {
            yield break;
        }

        var sizes = inventory.Media.Items
            .Where(i => i.WidthPx is not null)
            .ToDictionary(i => i.Reference, i => new PixelSize(i.WidthPx!.Value, i.HeightPx ?? 0));

        foreach (var layer in inventory.Show.AllLayers)
        {
            if (layer.Image is not { } reference || !sizes.TryGetValue(reference, out var actual))
            {
                continue;
            }

            var zoom = layer.MaxZoomBp ?? AcceptanceRules.DefaultZoomBp;
            var needed = (int)Math.Round(output.WidthPx * (zoom / (double)AcceptanceRules.DefaultZoomBp));

            if (needed <= actual.WidthPx)
            {
                continue;
            }

            yield return new Finding(
                Id,
                layer.Address,
                Level,
                Confidence,
                PassedThreshold: false,
                new Dictionary<string, long>
                {
                    ["neededWidthPx"] = needed,
                    ["actualWidthPx"] = actual.WidthPx,
                    ["shortfallPx"] = needed - actual.WidthPx,
                    ["maxZoomBp"] = zoom,
                    ["outputWidthPx"] = output.WidthPx,
                });
        }
    }
}

/// <summary>
/// Видео подключено ради куска: файл длиннее того, что из него реально доезжает до экрана.
/// </summary>
public sealed class OversizedVideo : IAcceptanceRule
{
    public string Id => "oversized-video";

    public ExecutionLevel Level => ExecutionLevel.Self;

    public RuleConfidence Confidence => RuleConfidence.High;

    public IEnumerable<Finding> Check(Учёт inventory, RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(context);

        var bytes = inventory.Media.Items
            .Where(i => i.FileBytes is not null)
            .ToDictionary(i => i.Reference, i => i.FileBytes!.Value);

        var slideTime = inventory.Show.Slides.ToDictionary(s => s.SlideOrdinal, s => s.TimeMs);

        foreach (var layer in inventory.Show.AllLayers)
        {
            if (layer.Image is not { } reference || layer.Video is not { LengthMs: > 0 } video)
            {
                continue;
            }

            // Обрезка записана не всегда; там, где её нет, объём всё равно ограничен длительностью слайда.
            var used = video.UsedMs ?? slideTime.GetValueOrDefault(layer.SlideOrdinal);

            if (used is not > 0 || used >= video.LengthMs)
            {
                continue;
            }

            var numbers = new Dictionary<string, long>
            {
                ["videoLengthMs"] = video.LengthMs.Value,
                ["usedMs"] = used.Value,
                ["wastedMs"] = video.LengthMs.Value - used.Value,
            };

            if (bytes.TryGetValue(reference, out var fileBytes))
            {
                numbers["fileBytes"] = fileBytes;
            }

            if (slideTime.GetValueOrDefault(layer.SlideOrdinal) is { } time)
            {
                numbers["slideTimeMs"] = time;
            }

            yield return new Finding(Id, layer.Address, Level, Confidence, PassedThreshold: false, numbers);
        }
    }
}

/// <summary>
/// Ссылка не разрешается: проект ищет файл, которого по этому пути нет.
/// </summary>
/// <remarks>
/// Идентификатор достался от кириллических путей, и это стоит пояснить. Сам по себе сбой кодировки
/// у доктора больше не проявляется: он читает файл шоу в CP1251 и находит то, что другие теряют.
/// Остаётся то, ради чего правило и заводилось, — ссылка, за которой ничего нет.
/// Признак «путь не из латиницы» идёт числом рядом, потому что именно такие ссылки теряются чаще.
/// </remarks>
public sealed class UnresolvedPaths : IAcceptanceRule
{
    public string Id => "cp1251-paths";

    public ExecutionLevel Level => ExecutionLevel.Self;

    public RuleConfidence Confidence => RuleConfidence.High;

    public IEnumerable<Finding> Check(Учёт inventory, RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(context);

        foreach (var item in inventory.Media.Items)
        {
            if (item.Status is not MediaProbeStatus.NotFound)
            {
                continue;
            }

            yield return new Finding(
                Id,
                ObjectAddress.ForMedia(item.Reference),
                Level,
                Confidence,
                PassedThreshold: false,
                new Dictionary<string, long>
                {
                    ["referenceCount"] = item.ReferenceCount,
                    ["nonAsciiPath"] = item.Reference.Raw.Any(c => c > 127) ? 1 : 0,
                });
        }
    }
}

/// <summary>
/// Чужой корень: в шапке записан путь с другой машины.
/// </summary>
/// <remarks>
/// Сравнивается каталог, а не файл: имя файла шоу переживает переезд, а каталог — нет.
/// Без известного настоящего каталога правило молчит: срабатывать на каждом абсолютном пути
/// значило бы жаловаться и на проект, лежащий там, где его создали.
/// </remarks>
public sealed class ForeignRoot : IAcceptanceRule
{
    public string Id => "foreign-root";

    public ExecutionLevel Level => ExecutionLevel.Self;

    public RuleConfidence Confidence => RuleConfidence.High;

    public IEnumerable<Finding> Check(Учёт inventory, RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(context);

        if (context.ProjectDirectory is not { Length: > 0 } actual)
        {
            yield break;
        }

        var recorded = DirectoryOf(inventory.Show.Header.FileName);
        if (recorded is null)
        {
            yield break;
        }

        if (SameDirectory(recorded, actual))
        {
            yield break;
        }

        yield return new Finding(
            Id,
            ObjectAddress.ForShow(),
            Level,
            Confidence,
            PassedThreshold: false,
            new Dictionary<string, long>
            {
                ["recordedPathIsAbsolute"] = recorded.Contains(':') || recorded.StartsWith("//", StringComparison.Ordinal) ? 1 : 0,
            });
    }

    internal static string? DirectoryOf(string? path)
    {
        if (path is not { Length: > 0 })
        {
            return null;
        }

        var normalized = path.Replace('\\', '/');

        // Программа пишет в шапку путь вида «My Computer/C:/…» — приставка к делу не относится.
        const string prefix = "My Computer/";
        if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[prefix.Length..];
        }

        var slash = normalized.LastIndexOf('/');
        return slash <= 0 ? null : normalized[..slash];
    }

    internal static bool SameDirectory(string recorded, string actual)
    {
        static string Tidy(string value) =>
            value.Replace('\\', '/').TrimEnd('/');

        return string.Equals(Tidy(recorded), Tidy(actual), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Музыка длиннее фильма: дорожка несёт больше, чем фильм способен проиграть.</summary>
public sealed class AudioLongerThanShow : IAcceptanceRule
{
    public string Id => "audio-longer-than-show";

    public ExecutionLevel Level => ExecutionLevel.Self;

    public RuleConfidence Confidence => RuleConfidence.High;

    public IEnumerable<Finding> Check(Учёт inventory, RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(context);

        var show = inventory.TotalTimeMs;
        if (show <= 0)
        {
            yield break;
        }

        foreach (var track in inventory.Audio)
        {
            if (track.File is not { } file || track.LengthMs is not { } length || length <= show)
            {
                continue;
            }

            var numbers = new Dictionary<string, long>
            {
                ["lengthMs"] = length,
                ["showDurationMs"] = show,
                ["excessMs"] = length - show,
            };

            // Начало и конец записаны не всегда; когда записаны, видно, сколько дорожки реально звучит.
            if (track.StartTimeMs is { } start && track.EndTimeMs is { } end && end >= start)
            {
                numbers["usedMs"] = end - start;
            }

            yield return new Finding(Id, ObjectAddress.ForSound(file), Level, Confidence, PassedThreshold: false, numbers);
        }
    }
}
