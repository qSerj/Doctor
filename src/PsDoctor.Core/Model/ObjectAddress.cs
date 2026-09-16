using System.Globalization;

namespace PsDoctor.Core.Model;

public enum AddressKind
{
    Show,
    Slide,
    Layer,
    Caption,
    Sound,
    Media,
}

/// <summary>
/// Насколько адресу можно верить.
/// </summary>
public enum AddressStability
{
    /// <summary>Адрес построен на <c>objectId</c>, который в своей сцене уникален.</summary>
    Strong,

    /// <summary>
    /// <c>objectId</c> отсутствует или совпал с чужим, и в ключ пришлось добавить порядковый номер.
    /// Такой адрес перестановку слоёв не переживёт, и об этом сказано, а не умолчано.
    /// </summary>
    Weak,
}

/// <summary>
/// Адрес объекта внутри проекта. Единица находки — правило плюс объект, значит у объекта
/// обязан быть адрес, иначе пункты неразличимы и отказ «не лечить» нечему приписать.
/// </summary>
/// <remarks>
/// Адрес обязан пережить правку проекта. Поэтому порядковый номер слоя в <see cref="StableKey"/>
/// не входит: переставили слои — находка та же. А ссылка на файл входит: подменили файл — находка новая.
/// <para>
/// Порядковый номер слайда в ключ входит, и это названный компромисс. Отпечаток слайда по набору
/// его слоёв пережил бы перестановку слайдов, но ломался бы от добавления одного слоя — и тогда
/// все находки слайда разом стали бы новыми. Вставка слайда в середину случается реже.
/// </para>
/// </remarks>
public sealed record ObjectAddress(
    AddressKind Kind,
    string StableKey,
    AddressStability Stability,
    int? SlideOrdinal = null,
    SceneKind? Scene = null,
    int? ObjectId = null,
    int? Ordinal = null,
    string? Name = null,
    MediaReference? Media = null)
{
    public static ObjectAddress ForShow() =>
        new(AddressKind.Show, "show", AddressStability.Strong);

    public static ObjectAddress ForSlide(int slideOrdinal) =>
        new(
            AddressKind.Slide,
            "slide:" + slideOrdinal.ToString(CultureInfo.InvariantCulture),
            AddressStability.Strong,
            SlideOrdinal: slideOrdinal,
            Ordinal: slideOrdinal);

    public static ObjectAddress ForMedia(MediaReference media)
    {
        ArgumentNullException.ThrowIfNull(media);
        return new(AddressKind.Media, "media:" + media.Normalized, AddressStability.Strong, Media: media);
    }

    public static ObjectAddress ForSound(MediaReference media)
    {
        ArgumentNullException.ThrowIfNull(media);
        return new(AddressKind.Sound, "sound:" + media.Normalized, AddressStability.Strong, Media: media);
    }

    public static ObjectAddress ForLayer(
        int slideOrdinal,
        SceneKind scene,
        int ordinal,
        int? objectId,
        string? name,
        MediaReference? media)
    {
        var sceneName = scene == SceneKind.Slide ? "slide" : "transition";
        var mediaPart = media?.Normalized ?? "-";
        var stable = objectId is not null;

        var identity = stable
            ? objectId!.Value.ToString(CultureInfo.InvariantCulture)
            : "#" + ordinal.ToString(CultureInfo.InvariantCulture);

        var key = string.Concat(
            "layer:",
            slideOrdinal.ToString(CultureInfo.InvariantCulture),
            "/",
            sceneName,
            "/",
            identity,
            "/",
            mediaPart);

        return new(
            AddressKind.Layer,
            key,
            stable ? AddressStability.Strong : AddressStability.Weak,
            SlideOrdinal: slideOrdinal,
            Scene: scene,
            ObjectId: objectId,
            Ordinal: ordinal,
            Name: name,
            Media: media);
    }

    public static ObjectAddress ForCaption(int slideOrdinal, int ordinal, int? internalId)
    {
        var stable = internalId is not null;
        var identity = stable
            ? internalId!.Value.ToString(CultureInfo.InvariantCulture)
            : "#" + ordinal.ToString(CultureInfo.InvariantCulture);

        return new(
            AddressKind.Caption,
            "caption:" + slideOrdinal.ToString(CultureInfo.InvariantCulture) + "/" + identity,
            stable ? AddressStability.Strong : AddressStability.Weak,
            SlideOrdinal: slideOrdinal,
            Ordinal: ordinal);
    }

    /// <summary>
    /// Тот же адрес, но помеченный слабым и с порядковым номером в ключе.
    /// Применяется, когда два объекта дали один ключ: молча оставить их неразличимыми нельзя.
    /// </summary>
    public ObjectAddress AsWeakened() =>
        Stability == AddressStability.Weak
            ? this
            : this with
            {
                StableKey = StableKey + "#" + (Ordinal?.ToString(CultureInfo.InvariantCulture) ?? "?"),
                Stability = AddressStability.Weak,
            };

    public override string ToString() => StableKey;
}
