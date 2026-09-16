using PsDoctor.Core.Format;

namespace PsDoctor.Core.Model;

/// <summary>Разрешение вывода и ключ, из которого оно взято.</summary>
public sealed record OutputSize(int WidthPx, int HeightPx, string SourceKey);

/// <summary>Звуковая дорожка шоу.</summary>
public sealed class SoundTrack
{
    private readonly ShowNode _node;

    internal SoundTrack(ShowNode node, int ordinal)
    {
        _node = node;
        Ordinal = ordinal;
        File = node.Text("file") is { Length: > 0 } path ? new MediaReference(path) : null;
        Address = File is null ? ObjectAddress.ForShow() : ObjectAddress.ForSound(File);
    }

    public int Ordinal { get; }

    public ObjectAddress Address { get; }

    public MediaReference? File { get; }

    /// <summary>Длина дорожки целиком. Вход правила о музыке длиннее фильма.</summary>
    public int? LengthMs => _node.Int("length");

    public int? StartTimeMs => _node.Int("startTime");

    public int? EndTimeMs => _node.Int("endTime");
}

/// <summary>Стиль подписи из библиотеки шоу.</summary>
public sealed class CaptionStyle
{
    private readonly ShowNode _node;

    internal CaptionStyle(ShowNode node, int ordinal)
    {
        _node = node;
        Ordinal = ordinal;
    }

    public int Ordinal { get; }

    public string? StyleName => _node.Text("styleName");

    public int? StyleFlags => _node.Int("styleFlags");

    public string? FontFaceName => _node.Child("caption")?.Child("logFont")?.Text("lfFaceName");
}

/// <summary>Модификатор: маленькая машина выражений. Что значат его функции — не установлено.</summary>
public sealed class Modifier
{
    private readonly ShowNode _node;

    internal Modifier(ShowNode node, int ordinal)
    {
        _node = node;
        Ordinal = ordinal;
    }

    public int Ordinal { get; }

    public int? ModifierId => _node.Int("modifierId");

    public int DeclaredActionCount => _node.Count("nrOfActions");

    public IEnumerable<int> ActionFunctionIds =>
        _node.Items("action").Select(a => a.Int("functionId")).Where(id => id is not null).Select(id => id!.Value);
}

/// <summary>Шапка шоу.</summary>
public sealed class ShowHeader
{
    private readonly ShowNode _root;

    internal ShowHeader(ShowNode root) => _root = root;

    public string? ProshowMajorVersion => _root.Text("proshowMajorVersion");

    public string? ProshowVersion => _root.Text("proshowVersion");

    public string? Title => _root.Text("title");

    /// <summary>Путь, записанный автором проекта. Вход правила о чужом корне.</summary>
    public string? FileName => _root.Text("fileName");

    public string? MakeFileLocalFolder => _root.Text("makeFileLocalFolder");

    public int? ShowAspect => _root.Int("showAspect");

    public int? ShowSizeX => _root.Int("showSizeX");

    public int? ShowSizeY => _root.Int("showSizeY");

    public int? DisplaySizeXPx => _root.Int("displaySizeX");

    public int? DisplaySizeYPx => _root.Int("displaySizeY");

    public int? VideoSizeXPx => _root.Int("videoSizeX");

    public int? VideoSizeYPx => _root.Int("videoSizeY");

    public int? OutputImageSizeXPx => _root.Int("outputImageSizeX");

    public int? OutputImageSizeYPx => _root.Int("outputImageSizeY");

    public int? MaxDispWidthPx => _root.Int("maxDispWidth");

    public int? MaxDispHeightPx => _root.Int("maxDispHeight");

    public int? MaxRenderWidthPx => _root.Int("maxRenderWidth");

    public int? MaxRenderHeightPx => _root.Int("maxRenderHeight");

    /// <summary>Частота кадров вывода в тысячных: 29970 — это 29.97.</summary>
    public int? VideoFrameRateMilliFps => _root.Int("videoFrameRate");

    /// <summary>
    /// Разрешение, от которого считается нужный размер картинок.
    /// Каким ключом программа на самом деле задаёт масштаб на экране, не установлено,
    /// поэтому до опыта берётся **максимум** из трёх кандидатов, а высота — пара к победившему,
    /// а не отдельный максимум: иначе вышло бы разрешение, которого нет ни в одном ключе.
    /// </summary>
    /// <remarks>
    /// Довод — направление ошибки. Завысили: проект недолечен, лишние пиксели остались.
    /// Занизили: картинка уменьшена сильнее, чем можно, и на телевизоре мыло, которое видит заказчик.
    /// Первая ошибка стоит времени, вторая — работы.
    /// </remarks>
    public OutputSize? TargetSize
    {
        get
        {
            OutputSize? best = null;

            foreach (var (key, width, height) in Candidates())
            {
                if (width is not { } w || height is not { } h)
                {
                    continue;
                }

                if (best is null || w > best.WidthPx)
                {
                    best = new OutputSize(w, h, key);
                }
            }

            return best;
        }
    }

    /// <summary>
    /// Все кандидаты на ширину вывода, включая тех, кого решение не касается.
    /// Они в отчёте затем, чтобы открытый вопрос закрылся данными пачки, а не рассуждением.
    /// </summary>
    public IEnumerable<(string Key, int? WidthPx, int? HeightPx)> SizeCandidates()
    {
        foreach (var candidate in Candidates())
        {
            yield return candidate;
        }

        yield return ("maxDisp", MaxDispWidthPx, MaxDispHeightPx);
        yield return ("maxRender", MaxRenderWidthPx, MaxRenderHeightPx);
    }

    private IEnumerable<(string Key, int? WidthPx, int? HeightPx)> Candidates()
    {
        yield return ("displaySize", DisplaySizeXPx, DisplaySizeYPx);
        yield return ("videoSize", VideoSizeXPx, VideoSizeYPx);
        yield return ("outputImageSize", OutputImageSizeXPx, OutputImageSizeYPx);
    }
}

/// <summary>Типизированный вид разобранного файла шоу. Дерево остаётся источником истины.</summary>
public sealed class Show
{
    private Show(ShowDocument document)
    {
        Document = document;
        Header = new ShowHeader(document.Root);

        Slides = Build(document.Root.Items("cell"), (node, i) => new Slide(node, node.Index ?? i));
        Sounds = Build(document.Root.Items("sound"), (node, i) => new SoundTrack(node, node.Index ?? i));
        CaptionStyles = Build(document.Root.Items("captionStyle"), (node, i) => new CaptionStyle(node, node.Index ?? i));
        Modifiers = Build(document.Root.Items("modifier"), (node, i) => new Modifier(node, node.Index ?? i));

        WeakenCollidingAddresses();
    }

    public ShowDocument Document { get; }

    public ShowHeader Header { get; }

    public IReadOnlyList<Slide> Slides { get; }

    public IReadOnlyList<SoundTrack> Sounds { get; }

    public IReadOnlyList<CaptionStyle> CaptionStyles { get; }

    public IReadOnlyList<Modifier> Modifiers { get; }

    public int DeclaredSlideCount => Document.Root.Count("cells");

    public int DeclaredSoundCount => Document.Root.Count("sounds");

    public int DeclaredCaptionStyleCount => Document.Root.Count("nrOfCaptionStyles");

    public int DeclaredModifierCount => Document.Root.Count("modifierCount");

    /// <summary>Все сцены: слайды и их переходы, в порядке слайдов.</summary>
    public IEnumerable<Scene> Scenes
    {
        get
        {
            foreach (var slide in Slides)
            {
                yield return slide;

                if (slide.Transition is { } transition)
                {
                    yield return transition;
                }
            }
        }
    }

    /// <summary>Все слои проекта — и в слайдах, и внутри переходов.</summary>
    public IEnumerable<Layer> AllLayers => Scenes.SelectMany(scene => scene.Layers);

    /// <summary>Длительность фильма: сумма длительностей слайдов. Переходы сюда не входят.</summary>
    public int TotalTimeMs => Slides.Sum(slide => slide.TimeMs ?? 0);

    public static Show From(ShowDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new Show(document);
    }

    private static IReadOnlyList<T> Build<T>(IReadOnlyList<ShowNode> nodes, Func<ShowNode, int, T> make)
    {
        var built = new T[nodes.Count];
        for (var i = 0; i < nodes.Count; i++)
        {
            built[i] = make(nodes[i], i);
        }

        return built;
    }

    /// <summary>
    /// Два слоя дали один ключ — значит адрес их не различает, и делать вид, что различает, нельзя.
    /// Оба помечаются слабыми, и в ключ добавляется порядковый номер.
    /// </summary>
    private void WeakenCollidingAddresses()
    {
        var byKey = new Dictionary<string, List<Layer>>(StringComparer.Ordinal);

        foreach (var layer in AllLayers)
        {
            if (!byKey.TryGetValue(layer.Address.StableKey, out var bucket))
            {
                bucket = [];
                byKey.Add(layer.Address.StableKey, bucket);
            }

            bucket.Add(layer);
        }

        foreach (var bucket in byKey.Values.Where(b => b.Count > 1))
        {
            foreach (var layer in bucket)
            {
                layer.Address = layer.Address.AsWeakened();
            }
        }
    }
}
