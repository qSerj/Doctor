namespace PsDoctor.Core.Model;

/// <summary>
/// Ссылка на медиафайл, как она записана в файле шоу, и её нормализованный вид.
/// Ссылки относительные (<c>image/что-то.png</c>), разделитель — косая черта.
/// </summary>
/// <remarks>
/// Сравниваются ссылки по нормализованному виду: регистр и вид разделителя в этом формате
/// ничего не значат, а вот считать один и тот же файл дважды нельзя — он грузится один раз,
/// сколько бы слоёв на него ни ссылалось.
/// </remarks>
public sealed class MediaReference : IEquatable<MediaReference>
{
    public MediaReference(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        Raw = raw;

        var normalized = raw.Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        Normalized = normalized.ToLowerInvariant();

        var dot = Normalized.LastIndexOf('.');
        var slash = Normalized.LastIndexOf('/');
        Extension = dot > slash && dot >= 0 ? Normalized[(dot + 1)..] : string.Empty;
    }

    /// <summary>Ссылка дословно, как в файле.</summary>
    public string Raw { get; }

    /// <summary>Приведённый вид: косая черта, нижний регистр, без ведущего «./».</summary>
    public string Normalized { get; }

    /// <summary>Расширение в нижнем регистре, без точки. Пустая строка, если расширения нет.</summary>
    public string Extension { get; }

    public bool Equals(MediaReference? other) =>
        other is not null && string.Equals(Normalized, other.Normalized, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as MediaReference);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Normalized);

    public override string ToString() => Raw;
}
