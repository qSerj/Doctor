using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PsDoctor.Core.Observation;

/// <summary>
/// Разделы слепка окружения (Э6.3). Раздел — имя, а не тип: новый раздел, в том числе для другой программы, не требует
/// нового сравнения.
/// </summary>
public static class EnvironmentSections
{
    /// <summary>Сборка, выпуск и редакция Windows, кодовые страницы, язык интерфейса.</summary>
    public const string System = "system";

    /// <summary>Путь, версия и сборка ProShow, его настройка «Avoid using DirectShow».</summary>
    public const string ProShow = "proshow";

    /// <summary>Кодеки Video for Windows и ACM из <c>Drivers32</c>: ключ — имя значения, например <c>vidc.xvid</c>.</summary>
    public const string VideoForWindows = "vfw";

    /// <summary>Фильтры DirectShow: ключ — категория и CLSID фильтра.</summary>
    public const string DirectShow = "directshow";

    /// <summary>Переназначения <c>DirectShow\Preferred</c>: ключ — подтип медиа.</summary>
    public const string DirectShowPreferred = "directshow-preferred";

    /// <summary>Преобразования Media Foundation: ключ — категория и CLSID.</summary>
    public const string MediaFoundation = "media-foundation";

    /// <summary>Декодеры картинок WIC: ключ — CLSID.</summary>
    public const string ImagingComponent = "wic";

    /// <summary>Расширения кодеков из Store: ключ — имя пакета.</summary>
    public const string StoreCodecs = "store-codecs";

    /// <summary>Наборы кодеков и инструменты из списка программ: ключ — раздел удаления.</summary>
    public const string CodecProducts = "codec-products";
}

/// <summary>Файл, на который указывает запись слепка: библиотека кодека, фильтра, программа.</summary>
/// <param name="Exists">Файл найден. Запись в реестре на пропавший файл — тоже различие машин.</param>
/// <param name="Version">Версия файла; <c>null</c> — у файла её нет или его нет.</param>
/// <param name="Written">Время последней записи, UTC.</param>
public sealed record EnvironmentFile(string Path, bool Exists, string? Version, long? Size, DateTimeOffset? Written);

/// <summary>Одна запись слепка. Тождество записи — раздел, вид реестра и ключ.</summary>
/// <param name="View">Вид реестра, из которого прочитана запись: 32 или 64; <c>null</c> — не из реестра.</param>
/// <param name="Values">Поля записи строками: имя, merit, CLSID. Строки, а не числа, — слепок пишет, а не считает.</param>
public sealed record EnvironmentEntry(
    string Section,
    int? View,
    string Key,
    IReadOnlyDictionary<string, string?> Values,
    EnvironmentFile? File);

/// <summary>
/// Слепок окружения машины (Э6.3): то, чем одна машина отличается от другой для программы, которая на ней глючит.
/// Нестойкого — свободной памяти и места — здесь нет, оно в факте <c>environment</c>.
/// </summary>
/// <param name="Id">Идентификатор — начало SHA-256 канонической формы записей, см. <see cref="ComputeId"/>.</param>
/// <param name="Taken">Когда снят; в идентификатор не входит.</param>
/// <param name="Seconds">Сколько шло снятие — это часть цены наблюдения; в идентификатор не входит.</param>
/// <param name="Entries">Записи в каноническом порядке: раздел, вид, ключ.</param>
public sealed record EnvironmentSnapshot(
    int Schema,
    string Id,
    DateTimeOffset Taken,
    double Seconds,
    IReadOnlyList<EnvironmentEntry> Entries)
{
    public const int CurrentSchema = 1;

    /// <summary>Шестнадцать шестнадцатеричных знаков, 64 бита: для слепков одной мастерской совпадение случайно не бывает.</summary>
    private const int IdLength = 16;

    /// <summary>
    /// Слепок из записей в любом порядке. Две записи с одним тождеством — ошибка читателя: сравнение не знало бы, какую
    /// из них сопоставлять.
    /// </summary>
    public static EnvironmentSnapshot Create(DateTimeOffset taken, double seconds, IEnumerable<EnvironmentEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var ordered = entries.OrderBy(entry => entry, EntryIdentity.Instance).ToList();
        for (var i = 1; i < ordered.Count; i++)
        {
            if (EntryIdentity.Instance.Compare(ordered[i - 1], ordered[i]) == 0)
            {
                var entry = ordered[i];
                throw new ArgumentException($"две записи с одним тождеством: {entry.Section}/{entry.View}/{entry.Key}", nameof(entries));
            }
        }
        return new EnvironmentSnapshot(CurrentSchema, ComputeId(ordered), taken, seconds, ordered);
    }

    /// <summary>
    /// Идентификатор по записям: не зависит от их порядка, от порядка полей и от времени снятия; меняется от любого поля
    /// любой записи. Время файла берётся в UTC — один и тот же миг в другом поясе не новая машина.
    /// </summary>
    public static string ComputeId(IEnumerable<EnvironmentEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema", CurrentSchema);
            writer.WriteStartArray("entries");
            foreach (var entry in entries.OrderBy(entry => entry, EntryIdentity.Instance))
            {
                WriteCanonical(writer, entry);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        var hash = SHA256.HashData(buffer.ToArray());
        return Convert.ToHexStringLower(hash)[..IdLength];
    }

    private static void WriteCanonical(Utf8JsonWriter writer, EnvironmentEntry entry)
    {
        writer.WriteStartObject();
        writer.WriteString("section", entry.Section);
        if (entry.View is { } view)
        {
            writer.WriteNumber("view", view);
        }
        else
        {
            writer.WriteNull("view");
        }
        writer.WriteString("key", entry.Key);
        writer.WriteStartObject("values");
        foreach (var (name, value) in entry.Values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            writer.WriteString(name, value);
        }
        writer.WriteEndObject();
        if (entry.File is { } file)
        {
            writer.WriteStartObject("file");
            writer.WriteString("path", file.Path);
            writer.WriteBoolean("exists", file.Exists);
            writer.WriteString("version", file.Version);
            if (file.Size is { } size)
            {
                writer.WriteNumber("size", size);
            }
            else
            {
                writer.WriteNull("size");
            }
            writer.WriteString("written", file.Written?.ToUniversalTime().ToString("O"));
            writer.WriteEndObject();
        }
        else
        {
            writer.WriteNull("file");
        }
        writer.WriteEndObject();
    }

    /// <summary>Канонический порядок и тождество записи: раздел, вид (без вида — первыми), ключ; ординально.</summary>
    internal sealed class EntryIdentity : IComparer<EnvironmentEntry>
    {
        public static EntryIdentity Instance { get; } = new();

        public int Compare(EnvironmentEntry? x, EnvironmentEntry? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }
            if (x is null)
            {
                return -1;
            }
            if (y is null)
            {
                return 1;
            }
            var bySection = string.CompareOrdinal(x.Section, y.Section);
            if (bySection != 0)
            {
                return bySection;
            }
            var byView = Nullable.Compare(x.View, y.View);
            return byView != 0 ? byView : string.CompareOrdinal(x.Key, y.Key);
        }
    }
}

/// <summary>Одно поле записи: было → стало. Поля файла называются <c>file.path</c>, <c>file.version</c> и так далее.</summary>
public sealed record EnvironmentFieldChange(string Name, string? Before, string? After);

/// <summary>Запись есть в обоих слепках, но отличается.</summary>
public sealed record EnvironmentEntryChange(string Section, int? View, string Key, IReadOnlyList<EnvironmentFieldChange> Fields);

/// <summary>
/// Разница двух слепков. Вердиктов нет — «этот фильтр вреден» говорят опыты, а не сравнение. Списки упорядочены
/// каноническим порядком записей.
/// </summary>
/// <param name="Added">Есть только во втором слепке.</param>
/// <param name="Removed">Есть только в первом.</param>
public sealed record EnvironmentDiff(
    string BeforeId,
    string AfterId,
    IReadOnlyList<EnvironmentEntry> Added,
    IReadOnlyList<EnvironmentEntry> Removed,
    IReadOnlyList<EnvironmentEntryChange> Changed)
{
    public bool IsEmpty => Added.Count == 0 && Removed.Count == 0 && Changed.Count == 0;
}

public static class EnvironmentComparison
{
    /// <summary>Разница слепков: записи сопоставляются по разделу, виду и ключу, порядок записей в слепках не важен.</summary>
    public static EnvironmentDiff Compare(EnvironmentSnapshot before, EnvironmentSnapshot after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var identity = EnvironmentSnapshot.EntryIdentity.Instance;
        var was = before.Entries.OrderBy(entry => entry, identity).ToList();
        var now = after.Entries.OrderBy(entry => entry, identity).ToList();
        var added = new List<EnvironmentEntry>();
        var removed = new List<EnvironmentEntry>();
        var changed = new List<EnvironmentEntryChange>();
        int i = 0, j = 0;
        while (i < was.Count || j < now.Count)
        {
            var order = i == was.Count ? 1 : j == now.Count ? -1 : identity.Compare(was[i], now[j]);
            if (order < 0)
            {
                removed.Add(was[i++]);
            }
            else if (order > 0)
            {
                added.Add(now[j++]);
            }
            else
            {
                var fields = CompareFields(was[i], now[j]);
                if (fields.Count > 0)
                {
                    changed.Add(new EnvironmentEntryChange(now[j].Section, now[j].View, now[j].Key, fields));
                }
                i++;
                j++;
            }
        }
        return new EnvironmentDiff(before.Id, after.Id, added, removed, changed);
    }

    private static List<EnvironmentFieldChange> CompareFields(EnvironmentEntry before, EnvironmentEntry after)
    {
        var fields = new List<EnvironmentFieldChange>();
        foreach (var name in before.Values.Keys.Union(after.Values.Keys).Order(StringComparer.Ordinal))
        {
            var was = before.Values.GetValueOrDefault(name);
            var now = after.Values.GetValueOrDefault(name);
            if (was != now || before.Values.ContainsKey(name) != after.Values.ContainsKey(name))
            {
                fields.Add(new EnvironmentFieldChange(name, was, now));
            }
        }
        var wasFile = FileFields(before.File);
        var nowFile = FileFields(after.File);
        foreach (var (name, value) in wasFile)
        {
            var other = nowFile.First(pair => pair.Name == name).Value;
            if (value != other)
            {
                fields.Add(new EnvironmentFieldChange(name, value, other));
            }
        }
        return fields;
    }

    /// <summary>Поля файла строками; нет файла — все <c>null</c>, и появление файла видно по каждому полю.</summary>
    private static (string Name, string? Value)[] FileFields(EnvironmentFile? file) =>
    [
        ("file.path", file?.Path),
        ("file.exists", file is null ? null : file.Exists ? "true" : "false"),
        ("file.version", file?.Version),
        ("file.size", file?.Size?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        ("file.written", file?.Written?.ToUniversalTime().ToString("O")),
    ];
}

/// <summary>Какие программы из списка удаления попадают в раздел <see cref="EnvironmentSections.CodecProducts"/>.</summary>
public static class CodecProducts
{
    /// <summary>
    /// Образцы имён — данные: наборы кодеков и то, что ставит свои декодеры. Пополняются по мере того, как опыты
    /// находят новых подозреваемых.
    /// </summary>
    public static IReadOnlyList<string> NamePatterns { get; } =
    [
        "K-Lite",
        "LAV Filters",
        "ffdshow",
        "Haali",
        "QuickTime",
        "CCCP",
        "Shark007",
        "Media Feature Pack",
    ];

    /// <summary>Имя программы содержит один из образцов без учёта регистра.</summary>
    public static bool Matches(string displayName)
    {
        ArgumentNullException.ThrowIfNull(displayName);
        return NamePatterns.Any(pattern => displayName.Contains(pattern, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Разбор значения <c>FilterData</c> фильтра DirectShow: сериализованная <c>REGFILTER2</c>.</summary>
public static class DirectShowFilterData
{
    /// <summary>
    /// Merit — второе двойное слово: первое — версия структуры. Короче восьми байт — <c>null</c>: запись испорчена или
    /// фильтр зарегистрирован без данных.
    /// </summary>
    public static uint? Merit(byte[]? data) =>
        data is { Length: >= 8 } ? BitConverter.ToUInt32(data, 4) : null;
}

/// <summary>Полное имя пакета Store: <c>Имя_Версия_Архитектура_Ресурс_Издатель</c>.</summary>
public sealed record StorePackage(string Name, string Version, string Architecture, string FullName)
{
    /// <summary>
    /// Образцы имён пакетов с декодерами — данные: расширения видео, картинок и звука от Microsoft и производителей.
    /// </summary>
    public static IReadOnlyList<string> CodecPatterns { get; } =
    [
        "VideoExtension",
        "ImageExtension",
        "WebMediaExtensions",
        "AudioExtension",
    ];

    /// <summary>Имя пакета без подчёркиваний, частей пять; иначе — не имя пакета, <c>null</c>.</summary>
    public static StorePackage? Parse(string fullName)
    {
        ArgumentNullException.ThrowIfNull(fullName);
        var parts = fullName.Split('_');
        return parts.Length == 5 && parts[0].Length > 0 && parts[1].Length > 0
            ? new StorePackage(parts[0], parts[1], parts[2], fullName)
            : null;
    }

    public bool IsCodec => CodecPatterns.Any(pattern => Name.Contains(pattern, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Слепок в файле: с отступами, чтобы его можно было прочесть глазами, по тем же правилам JSON, что журнал. Одно место
/// для наблюдателя, CLI и Workbench.
/// </summary>
public static class EnvironmentSnapshotJson
{
    private static readonly JsonSerializerOptions Indented = new(ObservationJson.Options) { WriteIndented = true };

    public static string Serialize(EnvironmentSnapshot snapshot) => JsonSerializer.Serialize(snapshot, Indented);

    /// <exception cref="JsonException">Не слепок.</exception>
    public static EnvironmentSnapshot Deserialize(string json) =>
        JsonSerializer.Deserialize<EnvironmentSnapshot>(json, ObservationJson.Options)
        ?? throw new JsonException("пустой слепок");
}
