using PsDoctor.Core.Format;

namespace PsDoctor.Core.Model;

public enum SceneKind
{
    Slide,
    Transition,
}

/// <summary>
/// Сцена — то, у чего есть свои слои со своими ключевыми кадрами.
/// Сцен две: слайд и переход. Переход — мини-слайд, а не эффект между слайдами,
/// и общий тип здесь не обобщение ради красоты, а устройство самого формата.
/// </summary>
public abstract class Scene
{
    private protected Scene(ShowNode node, int slideOrdinal, SceneKind kind)
    {
        Node = node;
        SlideOrdinal = slideOrdinal;
        Kind = kind;

        var items = node.Items("images");
        var layers = new Layer[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            layers[i] = new Layer(items[i], items[i].Index ?? i, slideOrdinal, kind);
        }

        Layers = layers;
    }

    private protected ShowNode Node { get; }

    public SceneKind Kind { get; }

    public int SlideOrdinal { get; }

    /// <summary>Сколько слоёв объявлено счётчиком. Считать надо по нему, а не по набору.</summary>
    public int DeclaredLayerCount => Node.Count("nrOfImages");

    public IReadOnlyList<Layer> Layers { get; }
}

/// <summary>Слайд.</summary>
public sealed class Slide : Scene
{
    internal Slide(ShowNode node, int ordinal)
        : base(node, ordinal, SceneKind.Slide)
    {
        Address = ObjectAddress.ForSlide(ordinal);

        if (node.Child("customTransition") is { } transition)
        {
            Transition = new SlideTransition(transition, ordinal, node.Text("customTransitionName"));
        }

        var items = node.Items("caption");
        var captions = new Caption[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            captions[i] = new Caption(items[i], items[i].Index ?? i, ordinal);
        }

        Captions = captions;

        Sound = node.Child("sound")?.Text("file") is { Length: > 0 } path ? new MediaReference(path) : null;
    }

    public ObjectAddress Address { get; }

    public int? TimeMs => Node.Int("time");

    public int? TransId => Node.Int("transId");

    public int? TransTimeMs => Node.Int("transTime");

    public bool? UsesCustomTransition => Node.Flag("useCustomTransition");

    public string? CustomTransitionName => Node.Text("customTransitionName");

    /// <summary>Переход слайда как сцена со своими слоями. Может отсутствовать.</summary>
    public SlideTransition? Transition { get; }

    public int DeclaredCaptionCount => Node.Count("captions");

    public IReadOnlyList<Caption> Captions { get; }

    /// <summary>Собственный звук слайда: у слайда есть подузел <c>sound</c>, а не элемент массива.</summary>
    public MediaReference? Sound { get; }
}

/// <summary>Переход слайда: та же сцена, те же слои, те же ключевые кадры.</summary>
public sealed class SlideTransition : Scene
{
    internal SlideTransition(ShowNode node, int slideOrdinal, string? name)
        : base(node, slideOrdinal, SceneKind.Transition) =>
        Name = name;

    public string? Name { get; }

    /// <summary>
    /// Собственная длительность перехода. В длительность фильма не входит:
    /// переход — общая зона, которой владеют оба соседних слайда.
    /// </summary>
    public int? TimeMs => Node.Int("time");

    public int? TransId => Node.Int("transId");

    /// <summary>Эскиз перехода — свой у каждой версии программы, встречается не всегда.</summary>
    public string? ThumbnailImage => Node.Text("thumbnailImage");
}
