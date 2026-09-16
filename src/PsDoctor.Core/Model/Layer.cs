using PsDoctor.Core.Format;

namespace PsDoctor.Core.Model;

/// <summary>
/// Ключевой кадр. Единицы масштабированные и целые, и разворачивать их здесь не надо:
/// суффикс имени несёт единицу, а целые числа складываются точно.
/// </summary>
public sealed class Keyframe
{
    private readonly ShowNode _node;

    internal Keyframe(ShowNode node, int ordinal)
    {
        _node = node;
        Ordinal = ordinal;
    }

    public int Ordinal { get; }

    public int? TimestampMs => _node.Int("timestamp");

    public int? SegmentTimestampMs => _node.Int("segmentTimestamp");

    /// <summary>Фаза слайда: 1, 2 или 3. Слой движется сквозь переход, и это её и различает.</summary>
    public int? TimeSegment => _node.Int("timeSegment");

    /// <summary>Зум по горизонтали в базисных пунктах: 10000 — сто процентов.</summary>
    public int? ZoomXBp => _node.Int("zoomX");

    public int? ZoomYBp => _node.Int("zoomY");

    public int? OffsetX => _node.Int("offsetX");

    public int? OffsetY => _node.Int("offsetY");

    public int? RotationBp => _node.Int("rotation");

    public int? AttributeMask => _node.Int("attributeMask");
}

/// <summary>Обрезка подключённого видео, как она записана у слоя.</summary>
public sealed record VideoTrim(int? StartTimeMs, int? EndTimeMs, int? LengthMs, int? SpeedPercent, bool? Loops)
{
    /// <summary>Сколько видео реально используется, если обе границы записаны.</summary>
    public int? UsedMs => StartTimeMs is { } start && EndTimeMs is { } end && end >= start ? end - start : null;
}

/// <summary>
/// Слой сцены. Один и тот же тип и для слоёв слайда, и для слоёв перехода:
/// движок у программы один, отдельной машины переходов не существует,
/// и арифметика по слоям поэтому пишется один раз.
/// </summary>
public sealed class Layer
{
    private readonly ShowNode _node;

    internal Layer(ShowNode node, int ordinal, int slideOrdinal, SceneKind scene)
    {
        _node = node;
        Ordinal = ordinal;
        SlideOrdinal = slideOrdinal;
        Scene = scene;

        Image = node.Text("image") is { Length: > 0 } path ? new MediaReference(path) : null;

        var keyframes = node.Items("keyframes");
        var built = new Keyframe[keyframes.Count];
        for (var i = 0; i < keyframes.Count; i++)
        {
            built[i] = new Keyframe(keyframes[i], keyframes[i].Index ?? i);
        }

        Keyframes = built;

        Address = ObjectAddress.ForLayer(slideOrdinal, scene, ordinal, node.Int("objectId"), node.Text("name"), Image);
    }

    public int Ordinal { get; }

    public int SlideOrdinal { get; }

    public SceneKind Scene { get; }

    public ObjectAddress Address { get; internal set; }

    public int? ObjectId => _node.Int("objectId");

    public string? Name => _node.Text("name");

    /// <summary>Подключённый файл. У заливок, градиентов и пустых слоёв его нет.</summary>
    public MediaReference? Image { get; }

    /// <summary>
    /// Признаки нарочно необязательные: отсутствие ключа означает умолчание, а не «нет».
    /// Считать «слоёв-видео» надо по <c>IsVideo == true</c>, то есть по объявленному,
    /// а не по додуманному.
    /// </summary>
    public bool? IsVideo => _node.Flag("isVideo");

    public bool? IsMasking => _node.Flag("isMaskingLayer");

    public bool? IsAdjustment => _node.Flag("isAdjustmentLayer");

    public bool? UsesGradient => _node.Flag("useGradient");

    /// <summary>Заменяемое место купленного шаблона, а не ручная работа.</summary>
    public bool? IsReplaceableTemplate => _node.Flag("replaceableTemplate");

    public int DeclaredKeyframeCount => _node.Count("nrOfKeyframes");

    public IReadOnlyList<Keyframe> Keyframes { get; }

    public VideoTrim? Video =>
        IsVideo == true || _node.HasAnyVideoKey()
            ? new VideoTrim(
                _node.Int("videoStartTime"),
                _node.Int("videoEndTime"),
                _node.Int("videoLength"),
                _node.Int("videoSpeed"),
                _node.Flag("loopVideo"))
            : null;

    /// <summary>
    /// Наибольший зум, которого слой когда-либо достигает, в базисных пунктах.
    /// Это вход обоих точных лечений: и уменьшения картинки, и сторожевого правила.
    /// </summary>
    public int? MaxZoomBp
    {
        get
        {
            int? max = null;
            foreach (var keyframe in Keyframes)
            {
                foreach (var zoom in new[] { keyframe.ZoomXBp, keyframe.ZoomYBp })
                {
                    if (zoom is { } value && (max is null || value > max))
                    {
                        max = value;
                    }
                }
            }

            return max;
        }
    }
}

/// <summary>Подпись слайда.</summary>
public sealed class Caption
{
    private readonly ShowNode _node;

    internal Caption(ShowNode node, int ordinal, int slideOrdinal)
    {
        _node = node;
        Ordinal = ordinal;
        SlideOrdinal = slideOrdinal;
        Address = ObjectAddress.ForCaption(slideOrdinal, ordinal, node.Int("internalId"));
    }

    public int Ordinal { get; }

    public int SlideOrdinal { get; }

    public ObjectAddress Address { get; }

    public string? Text => _node.Text("text");

    public bool? IsReplaceableTemplate => _node.Flag("replaceableTemplate");

    /// <summary>Имя гарнитуры. Вход правила о недостающих шрифтах.</summary>
    public string? FontFaceName => _node.Child("logFont")?.Text("lfFaceName");
}

internal static class LayerNodeExtensions
{
    private static readonly string[] VideoKeys =
        ["videoStartTime", "videoEndTime", "videoLength", "videoSpeed", "loopVideo", "useCropping"];

    public static bool HasAnyVideoKey(this ShowNode node)
    {
        foreach (var key in VideoKeys)
        {
            if (node.Scalars.ContainsKey(key))
            {
                return true;
            }
        }

        return false;
    }
}
