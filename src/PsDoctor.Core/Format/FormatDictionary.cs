using System.Globalization;
using System.Text;

namespace PsDoctor.Core.Format;

/// <summary>Чем оказались значения одной формы ключа.</summary>
public enum ValueKind
{
    /// <summary>Значений не было вовсе.</summary>
    None,

    /// <summary>Все значения — целые числа.</summary>
    Integer,

    /// <summary>Все значения пустые.</summary>
    Empty,

    /// <summary>Есть нечисловые значения.</summary>
    Text,

    /// <summary>Вперемешку числа и текст.</summary>
    Mixed,
}

/// <summary>
/// Форма ключа — полный путь с выброшенными индексами: <c>cell[].images[].keyframes[].zoomX</c>.
/// </summary>
/// <param name="Shape">Сама форма.</param>
/// <param name="Count">Сколько ключей этой формы встретилось.</param>
/// <param name="Known">Потребляет ли эту форму типизированный вид доктора.</param>
/// <param name="Kind">Чем оказались значения.</param>
/// <param name="Min">Наименьшее целое значение, если форма числовая.</param>
/// <param name="Max">Наибольшее целое значение, если форма числовая.</param>
/// <param name="Samples">Несколько образцов нечисловых значений. В обезличенный срез не попадают.</param>
public sealed record KeyShape(
    string Shape,
    int Count,
    bool Known,
    ValueKind Kind,
    long? Min,
    long? Max,
    IReadOnlyList<string> Samples);

/// <summary>
/// Словарь форм ключей разобранного файла. Это ответ на вопрос, что делать с ключами,
/// о которых доктор ничего не знает: считать и назвать.
/// </summary>
/// <remarks>
/// Незнакомый ключ — данные, а не ошибка разбора. Формат устойчив между версиями программы,
/// но не одинаков, и прогон пачки проектов через этот словарь сам показывает,
/// какие функции программы вообще используются — без единой строки особого кода.
/// </remarks>
public sealed class FormatDictionary
{
    private const int MaxSamples = 3;

    private FormatDictionary(IReadOnlyList<KeyShape> shapes)
    {
        Shapes = shapes;
        UnknownShapeCount = shapes.Count(s => !s.Known);
    }

    public IReadOnlyList<KeyShape> Shapes { get; }

    public int UnknownShapeCount { get; }

    public static FormatDictionary From(ShowDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var accumulators = new Dictionary<string, Accumulator>(StringComparer.Ordinal);
        Walk(document.Root, new StringBuilder(), accumulators);

        var shapes = accumulators
            .Select(pair => pair.Value.ToShape(pair.Key))
            .OrderBy(shape => shape.Shape, StringComparer.Ordinal)
            .ToArray();

        return new FormatDictionary(shapes);
    }

    private static void Walk(ShowNode node, StringBuilder prefix, Dictionary<string, Accumulator> into)
    {
        foreach (var (name, value) in node.Scalars)
        {
            Add(into, Compose(prefix, name), value);
        }

        foreach (var (name, child) in node.Children)
        {
            var mark = Push(prefix, name, indexed: false);
            Walk(child, prefix, into);
            prefix.Length = mark;
        }

        foreach (var (name, array) in node.Arrays)
        {
            var mark = Push(prefix, name, indexed: true);

            foreach (var item in array.Items)
            {
                if (item.OwnValue is { } own)
                {
                    Add(into, prefix.ToString(), own);
                }

                Walk(item, prefix, into);
            }

            prefix.Length = mark;
        }
    }

    private static int Push(StringBuilder prefix, string name, bool indexed)
    {
        var mark = prefix.Length;

        if (prefix.Length > 0)
        {
            prefix.Append('.');
        }

        prefix.Append(name);

        if (indexed)
        {
            prefix.Append("[]");
        }

        return mark;
    }

    private static string Compose(StringBuilder prefix, string name) =>
        prefix.Length == 0 ? name : prefix + "." + name;

    private static void Add(Dictionary<string, Accumulator> into, string shape, ShowValue value)
    {
        if (!into.TryGetValue(shape, out var accumulator))
        {
            accumulator = new Accumulator();
            into.Add(shape, accumulator);
        }

        accumulator.Add(value);
    }

    private sealed class Accumulator
    {
        private readonly List<string> _samples = [];
        private int _count;
        private int _integers;
        private int _empties;
        private int _texts;
        private long _min = long.MaxValue;
        private long _max = long.MinValue;

        public void Add(ShowValue value)
        {
            _count++;

            if (value.Raw.Length == 0)
            {
                _empties++;
                return;
            }

            if (value.AsLong() is { } number)
            {
                _integers++;
                _min = Math.Min(_min, number);
                _max = Math.Max(_max, number);
                return;
            }

            _texts++;

            if (_samples.Count < MaxSamples && !_samples.Contains(value.Raw, StringComparer.Ordinal))
            {
                _samples.Add(value.Raw);
            }
        }

        public KeyShape ToShape(string shape)
        {
            var kind = (_integers, _texts, _empties) switch
            {
                (0, 0, 0) => ValueKind.None,
                (> 0, 0, 0) => ValueKind.Integer,
                (0, 0, > 0) => ValueKind.Empty,
                (0, > 0, _) => ValueKind.Text,
                _ => ValueKind.Mixed,
            };

            return new KeyShape(
                shape,
                _count,
                ShowKnownKeys.Contains(shape),
                kind,
                _integers > 0 ? _min : null,
                _integers > 0 ? _max : null,
                _samples);
        }
    }
}

/// <summary>
/// Формы ключей, которые типизированный вид доктора действительно читает.
/// Список ведётся руками и растёт по мере того, как ключ получает смысл;
/// всё остальное остаётся данными словаря и в поведении доктора не участвует.
/// </summary>
public static class ShowKnownKeys
{
    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        // Шапка шоу
        "proshowMajorVersion",
        "proshowVersion",
        "title",
        "fileName",
        "makeFileLocalFolder",
        "showAspect",
        "showSizeX",
        "showSizeY",
        "displaySizeX",
        "displaySizeY",
        "videoSizeX",
        "videoSizeY",
        "outputImageSizeX",
        "outputImageSizeY",
        "maxDispWidth",
        "maxDispHeight",
        "maxRenderWidth",
        "maxRenderHeight",
        "videoFrameRate",

        // Счётчики верхнего уровня
        "cells",
        "sounds",
        "nrOfCaptionStyles",
        "modifierCount",

        // Слайд
        "cell[].time",
        "cell[].transId",
        "cell[].transTime",
        "cell[].useCustomTransition",
        "cell[].customTransitionName",
        "cell[].nrOfImages",
        "cell[].captions",
        "cell[].sound.file",

        // Переход как сцена
        "cell[].customTransition.nrOfImages",
        "cell[].customTransition.time",
        "cell[].customTransition.transId",
        "cell[].customTransition.thumbnailImage",

        // Слой — и в слайде, и в переходе
        "cell[].images[].image",
        "cell[].images[].name",
        "cell[].images[].objectId",
        "cell[].images[].isVideo",
        "cell[].images[].isMaskingLayer",
        "cell[].images[].isAdjustmentLayer",
        "cell[].images[].useGradient",
        "cell[].images[].replaceableTemplate",
        "cell[].images[].nrOfKeyframes",
        "cell[].images[].videoStartTime",
        "cell[].images[].videoEndTime",
        "cell[].images[].videoLength",
        "cell[].images[].videoSpeed",
        "cell[].images[].useCropping",
        "cell[].images[].loopVideo",
        "cell[].customTransition.images[].image",
        "cell[].customTransition.images[].name",
        "cell[].customTransition.images[].objectId",
        "cell[].customTransition.images[].isVideo",
        "cell[].customTransition.images[].isMaskingLayer",
        "cell[].customTransition.images[].isAdjustmentLayer",
        "cell[].customTransition.images[].useGradient",
        "cell[].customTransition.images[].replaceableTemplate",
        "cell[].customTransition.images[].nrOfKeyframes",

        // Ключевой кадр
        "cell[].images[].keyframes[].timestamp",
        "cell[].images[].keyframes[].segmentTimestamp",
        "cell[].images[].keyframes[].timeSegment",
        "cell[].images[].keyframes[].zoomX",
        "cell[].images[].keyframes[].zoomY",
        "cell[].images[].keyframes[].offsetX",
        "cell[].images[].keyframes[].offsetY",
        "cell[].images[].keyframes[].rotation",
        "cell[].images[].keyframes[].attributeMask",
        "cell[].customTransition.images[].keyframes[].timestamp",
        "cell[].customTransition.images[].keyframes[].segmentTimestamp",
        "cell[].customTransition.images[].keyframes[].timeSegment",
        "cell[].customTransition.images[].keyframes[].zoomX",
        "cell[].customTransition.images[].keyframes[].zoomY",
        "cell[].customTransition.images[].keyframes[].offsetX",
        "cell[].customTransition.images[].keyframes[].offsetY",
        "cell[].customTransition.images[].keyframes[].rotation",
        "cell[].customTransition.images[].keyframes[].attributeMask",

        // Подписи и шрифты
        "cell[].caption[].text",
        "cell[].caption[].replaceableTemplate",
        "cell[].caption[].nrOfKeyframes",
        "cell[].caption[].logFont.lfFaceName",
        "captionStyle[].styleName",
        "captionStyle[].styleFlags",
        "captionStyle[].caption.logFont.lfFaceName",

        // Звук
        "sound[].file",
        "sound[].length",
        "sound[].startTime",
        "sound[].endTime",

        // Модификаторы
        "modifier[].modifierId",
        "modifier[].nrOfActions",
        "modifier[].action[].functionId",
    };

    public static bool Contains(string shape) => Known.Contains(shape);

    public static IReadOnlyCollection<string> All => Known;
}
