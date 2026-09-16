using System.Globalization;

namespace PsDoctor.Core.Format;

/// <summary>
/// Значение ключа, как оно записано в файле, и номер строки, на которой оно стоит.
/// Значение хранится сырым: пустая строка и <c>""</c> — разные состояния файла, и склеивать их нельзя.
/// Номер строки понадобится на Э5, где доктор правит файл шоу на месте.
/// </summary>
public readonly record struct ShowValue(string Raw, int LineNumber)
{
    /// <summary>Текст со снятыми обрамляющими кавычками. Применяется только к текстовым ключам.</summary>
    public string AsText() =>
        Raw.Length >= 2 && Raw[0] == '"' && Raw[^1] == '"' ? Raw[1..^1] : Raw;

    public int? AsInt() =>
        int.TryParse(Raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v : null;

    public long? AsLong() =>
        long.TryParse(Raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>Признак: 1 — да, 0 — нет. Всё остальное — не признак, и это не притворяется нулём.</summary>
    public bool? AsFlag() => Raw switch
    {
        "1" => true,
        "0" => false,
        _ => null,
    };

    public override string ToString() => Raw;
}
