namespace PsDoctor.Core.Format;

/// <summary>
/// Узел разобранного файла шоу: скаляры, дочерние узлы без индекса и массивы.
/// Узел массива может нести и собственное значение — в файле встречается
/// индексированный лист вида <c>cell[0].mappedCaption[0]=…</c>.
/// </summary>
public sealed class ShowNode
{
    private readonly Dictionary<string, ShowValue> _scalars = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ShowNode> _children = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ShowArray> _arrays = new(StringComparer.Ordinal);

    internal ShowNode(string name, int? index)
    {
        Name = name;
        Index = index;
    }

    /// <summary>Имя узла. У корня — пустая строка.</summary>
    public string Name { get; }

    /// <summary>Индекс, если узел — элемент массива.</summary>
    public int? Index { get; }

    /// <summary>Собственное значение узла массива, если оно есть.</summary>
    public ShowValue? OwnValue { get; internal set; }

    public IReadOnlyDictionary<string, ShowValue> Scalars => _scalars;

    public IReadOnlyDictionary<string, ShowNode> Children => _children;

    public IReadOnlyDictionary<string, ShowArray> Arrays => _arrays;

    public ShowValue? Scalar(string name) =>
        _scalars.TryGetValue(name, out var v) ? v : null;

    public ShowNode? Child(string name) =>
        _children.TryGetValue(name, out var n) ? n : null;

    public ShowArray? Array(string name) =>
        _arrays.TryGetValue(name, out var a) ? a : null;

    public int? Int(string name) => Scalar(name)?.AsInt();

    public long? Long(string name) => Scalar(name)?.AsLong();

    public bool? Flag(string name) => Scalar(name)?.AsFlag();

    public string? Text(string name) => Scalar(name)?.AsText();

    /// <summary>
    /// Значение счётчика. Отсутствие ключа-счётчика означает ноль, и это единственное
    /// умолчание формата, доказанное на материале: у слайда без слоёв нет ни счётчика, ни элементов.
    /// </summary>
    public int Count(string counterName) => Int(counterName) ?? 0;

    /// <summary>Элементы массива, или пустой список, если массива нет вовсе.</summary>
    public IReadOnlyList<ShowNode> Items(string arrayName) =>
        Array(arrayName)?.Items ?? System.Array.Empty<ShowNode>();

    internal bool TrySetScalar(string name, ShowValue value, out ShowValue previous)
    {
        if (_scalars.TryGetValue(name, out previous))
        {
            _scalars[name] = value;
            return false;
        }

        _scalars.Add(name, value);
        previous = default;
        return true;
    }

    internal bool HasScalar(string name) => _scalars.ContainsKey(name);

    internal ShowNode GetOrAddChild(string name)
    {
        if (_children.TryGetValue(name, out var existing))
        {
            return existing;
        }

        var created = new ShowNode(name, null);
        _children.Add(name, created);
        return created;
    }

    internal ShowArray GetOrAddArray(string name)
    {
        if (_arrays.TryGetValue(name, out var existing))
        {
            return existing;
        }

        var created = new ShowArray(name);
        _arrays.Add(name, created);
        return created;
    }
}

/// <summary>
/// Массив узлов, упорядоченный по индексу. Индексы сохраняются как есть и не сдвигаются:
/// молча выровнять разреженный массив значило бы соврать в адресе находки.
/// </summary>
public sealed class ShowArray
{
    private readonly SortedDictionary<int, ShowNode> _items = new();
    private IReadOnlyList<ShowNode>? _cache;

    internal ShowArray(string name) => Name = name;

    public string Name { get; }

    public IReadOnlyList<ShowNode> Items => _cache ??= _items.Values.ToArray();

    /// <summary>Индексы идут подряд от нуля, без дыр.</summary>
    public bool IsDense
    {
        get
        {
            var expected = 0;
            foreach (var index in _items.Keys)
            {
                if (index != expected++)
                {
                    return false;
                }
            }

            return true;
        }
    }

    public ShowNode? At(int index) => _items.TryGetValue(index, out var n) ? n : null;

    internal ShowNode GetOrAdd(int index)
    {
        if (_items.TryGetValue(index, out var existing))
        {
            return existing;
        }

        var created = new ShowNode(Name, index);
        _items.Add(index, created);
        _cache = null;
        return created;
    }
}
