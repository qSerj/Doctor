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

    /// <summary>
    /// Путь, версия и сборка ProShow, его настройка «Avoid using DirectShow» из действующего <c>proshow.cfg</c> (ключ
    /// <c>program</c>) и каждый найденный <c>proshow.cfg</c> отдельно: <c>config/program</c> и <c>config/virtual-store</c>;
    /// ярлыки на программу — <c>shortcut/место/путь</c> с галкой «от имени администратора».
    /// </summary>
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

/// <summary>Что сравниваются: два состояния одной машины или две машины.</summary>
public enum EnvironmentComparisonMode
{
    /// <summary>До и после на одной машине: новое время записи файла — файл заменили.</summary>
    SameMachine,

    /// <summary>
    /// Две машины: время записи файла не сравнивается — оно говорит, когда файл поставили, а не какой он, и между
    /// машинами различается почти у каждого (Э6.3, критерий 6).
    /// </summary>
    Machines,
}

public static class EnvironmentComparison
{
    /// <summary>
    /// Поля, где лежит путь: Windows регистра в пути не различает, поэтому <c>C:\Windows</c> и <c>C:\WINDOWS</c> —
    /// один файл, а не изменение.
    /// </summary>
    private static readonly HashSet<string> PathFields = ["file.path", "inproc", "driver", "config"];

    private const string FileWritten = "file.written";

    /// <summary>Разница слепков: записи сопоставляются по разделу, виду и ключу, порядок записей в слепках не важен.</summary>
    public static EnvironmentDiff Compare(
        EnvironmentSnapshot before,
        EnvironmentSnapshot after,
        EnvironmentComparisonMode mode = EnvironmentComparisonMode.SameMachine)
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
                var fields = CompareFields(was[i], now[j], mode);
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

    private static List<EnvironmentFieldChange> CompareFields(EnvironmentEntry before, EnvironmentEntry after, EnvironmentComparisonMode mode)
    {
        var fields = new List<EnvironmentFieldChange>();
        foreach (var name in before.Values.Keys.Union(after.Values.Keys).Order(StringComparer.Ordinal))
        {
            var was = before.Values.GetValueOrDefault(name);
            var now = after.Values.GetValueOrDefault(name);
            if (!Same(name, was, now) || before.Values.ContainsKey(name) != after.Values.ContainsKey(name))
            {
                fields.Add(new EnvironmentFieldChange(name, was, now));
            }
        }
        var wasFile = FileFields(before.File);
        var nowFile = FileFields(after.File);
        foreach (var (name, value) in wasFile)
        {
            if (name == FileWritten && mode == EnvironmentComparisonMode.Machines)
            {
                continue;
            }
            var other = nowFile.First(pair => pair.Name == name).Value;
            if (!Same(name, value, other))
            {
                fields.Add(new EnvironmentFieldChange(name, value, other));
            }
        }
        return fields;
    }

    private static bool Same(string name, string? before, string? after) =>
        string.Equals(before, after, PathFields.Contains(name) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

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

/// <summary>
/// Файл настроек ProShow <c>proshow.cfg</c>: два байта заголовка, дальше строки через ноль — имя настройки, её значение.
/// Сплошь парами файл не читается: внутри встречаются списки, поэтому значение ищется по имени целиком — следующая строка
/// за ним (журнал реверсинга, 30.09.2026).
/// </summary>
public static class ProShowConfig
{
    public const string FileName = "proshow.cfg";

    /// <summary>Галка «Avoid using DirectShow when possible»: <c>1</c> — видео через встроенный FFmpeg, <c>0</c> — через QuickTime.</summary>
    public const string DShowUseFfmpeg = "prefDShowUseFFMPEG";

    private const int HeaderLength = 2;

    /// <summary>Метка совместимости «Запускать от имени администратора» в <c>AppCompatFlags\Layers</c>.</summary>
    public const string RunAsAdminLayer = "RUNASADMIN";

    /// <summary>
    /// Уводит ли Windows запись программы в Program Files в VirtualStore пользователя. Без UAC виртуализации нет ни у кого;
    /// у повышенного процесса — тоже. Живой процесс говорит сам за себя; токен не открылся — неизвестно. Программа не
    /// запущена — судим по метке <see cref="RunAsAdminLayer"/> и галкам ярлыков (<see cref="ShellLink"/>); запуск правой
    /// кнопкой «от имени администратора» не виден ничему, кроме живого процесса.
    /// </summary>
    /// <param name="enableLua">Значение <c>EnableLUA</c>; <c>0</c> — UAC выключен. Нет значения — UAC по умолчанию включён.</param>
    /// <param name="running">Процесс программы найден.</param>
    /// <param name="elevated">Его токен повышен; <c>null</c> — не открылся или процессов несколько.</param>
    /// <param name="runAsAdmin">Метка совместимости или галка хотя бы одного ярлыка на программу.</param>
    public static bool? Virtualized(int? enableLua, bool running, bool? elevated, bool runAsAdmin) =>
        enableLua == 0 ? false : running ? !elevated : !runAsAdmin;

    /// <summary>
    /// Файл, который программа читает. Копии в VirtualStore нет — выбора нет: файл рядом с программой или ничего.
    /// Копия есть — она действует только у виртуализованного процесса; повышенный её не видит и читает файл рядом с собой,
    /// даже устаревший, а нет его — работает без настроек. Виртуализация неизвестна — неизвестен и файл (<c>null</c>):
    /// оба лежат в слепке отдельными записями, и выбор делает инженер.
    /// </summary>
    public static string? Effective(string? programFile, string? virtualStoreFile, bool? virtualized) =>
        virtualStoreFile is null
            ? programFile
            : virtualized switch
            {
                true => virtualStoreFile,
                false => programFile,
                null => null,
            };

    /// <summary>Данные значения <c>AppCompatFlags\Layers</c>: метки через пробел, первой бывает <c>~</c>.</summary>
    public static bool HasRunAsAdmin(string? layers) =>
        layers is not null
        && layers.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(RunAsAdminLayer, StringComparer.OrdinalIgnoreCase);

    /// <summary>Значение настройки; нет её или файл оборван на ней — <c>null</c>. Значения — байты как есть, по Latin-1.</summary>
    public static string? Value(ReadOnlySpan<byte> content, string name) =>
        Find(content, name) is (int start, int length) ? Encoding.Latin1.GetString(content.Slice(start, length)) : null;

    /// <summary>
    /// Файл с другим значением настройки той же длины — остальные байты не тронуты, файл не перестраивается: правится
    /// только то, что разобрано (рецепт Э4.4, <c>0</c> → <c>1</c>). Нет настройки или длина другая — <c>null</c>.
    /// </summary>
    public static byte[]? WithValue(ReadOnlySpan<byte> content, string name, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var bytes = Encoding.Latin1.GetBytes(value);
        if (Find(content, name) is not (int start, int length) || length != bytes.Length || Array.IndexOf(bytes, (byte)0) >= 0)
        {
            return null;
        }
        var changed = content.ToArray();
        bytes.CopyTo(changed, start);
        return changed;
    }

    /// <summary>Где лежит значение настройки: строка за именем, найденным целиком.</summary>
    private static (int Start, int Length)? Find(ReadOnlySpan<byte> content, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var key = Encoding.ASCII.GetBytes(name);
        var position = Math.Min(HeaderLength, content.Length);
        var matched = false;
        while (position < content.Length)
        {
            var end = content[position..].IndexOf((byte)0);
            if (end < 0)
            {
                return null;
            }
            if (matched)
            {
                return (position, end);
            }
            matched = content.Slice(position, end).SequenceEqual(key);
            position += end + 1;
        }
        return null;
    }
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
