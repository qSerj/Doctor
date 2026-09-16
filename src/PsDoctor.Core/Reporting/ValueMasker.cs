using System.Security.Cryptography;
using System.Text;

namespace PsDoctor.Core.Reporting;

/// <summary>Что за строка идёт в отчёт. От вида зависит приставка псевдонима.</summary>
public enum MaskKind
{
    /// <summary>Путь к файлу или каталогу.</summary>
    Path,

    /// <summary>Ссылка на медиафайл.</summary>
    Media,

    /// <summary>Имя проекта или заголовок шоу.</summary>
    Project,

    /// <summary>Имя слоя, перехода, стиля.</summary>
    Name,

    /// <summary>Произвольный текст: подпись, заметка, описание.</summary>
    Text,
}

/// <summary>
/// Через это проходит каждая строка, попадающая в отчёт.
/// </summary>
/// <remarks>
/// Устройство — **белый список**: маскируется всё, а <see cref="Keep"/> вызывается явно там,
/// где значение доказуемо безопасно. Чёрный список однажды пропустит поле, и ценой этого
/// будут клиентские данные в чужих руках; вызов <see cref="Keep"/> читается как решение,
/// а не как недосмотр.
/// </remarks>
public interface IValueMasker
{
    /// <summary>Скрыть значение, сохранив его различимость внутри пачки.</summary>
    string? Mask(MaskKind kind, string? value);

    /// <summary>Оставить как есть. Вызывается только для доказуемо безопасного.</summary>
    string? Keep(string? value);

    /// <summary>Отдаются ли образцы значений словаря форм. В обезличенном срезе — нет.</summary>
    bool AllowsSamples { get; }
}

/// <summary>Ничего не скрывает. Обычный отчёт, остающийся на машине.</summary>
public sealed class PassThroughMasker : IValueMasker
{
    public static readonly PassThroughMasker Instance = new();

    public bool AllowsSamples => true;

    public string? Mask(MaskKind kind, string? value) => value;

    public string? Keep(string? value) => value;
}

/// <summary>
/// Заменяет строки устойчивыми псевдонимами. Внутри одной пачки один и тот же вход даёт
/// один и тот же псевдоним, поэтому обобщать по нему можно: «в проектах, где сработало это
/// правило, встречается вот такое сочетание» остаётся выводимым.
/// </summary>
public sealed class AliasMasker : IValueMasker
{
    private readonly Dictionary<(MaskKind Kind, string Value), string> _cache = [];
    private readonly byte[] _salt;

    public AliasMasker(string salt)
    {
        ArgumentNullException.ThrowIfNull(salt);
        _salt = Encoding.UTF8.GetBytes(salt);
    }

    public bool AllowsSamples => false;

    public string? Mask(MaskKind kind, string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value.Length == 0)
        {
            return string.Empty;
        }

        if (_cache.TryGetValue((kind, value), out var alias))
        {
            return alias;
        }

        alias = Prefix(kind) + "-" + Digest(value);
        _cache.Add((kind, value), alias);
        return alias;
    }

    public string? Keep(string? value) => value;

    private static string Prefix(MaskKind kind) => kind switch
    {
        MaskKind.Path => "path",
        MaskKind.Media => "media",
        MaskKind.Project => "project",
        MaskKind.Name => "name",
        _ => "text",
    };

    private string Digest(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var buffer = new byte[_salt.Length + bytes.Length];
        _salt.CopyTo(buffer, 0);
        bytes.CopyTo(buffer, _salt.Length);

        return Convert.ToHexStringLower(SHA256.HashData(buffer))[..8];
    }
}
