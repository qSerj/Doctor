using System.Globalization;

namespace PsDoctor.Core.Format;

/// <summary>
/// Шаг пути ключа: имя и, если это элемент массива, его индекс.
/// Ключи файла шоу — плоские полные пути вида <c>cell[1].images[3].keyframes[0].zoomX</c>;
/// вложенности в самом файле нет, иерархия выводится из пути.
/// </summary>
public readonly record struct ShowPathStep(string Name, int? Index)
{
    public override string ToString() =>
        Index is null ? Name : Name + "[" + Index.Value.ToString(CultureInfo.InvariantCulture) + "]";
}

/// <summary>Разбор ключа на шаги. Имя шага — строго <c>[A-Za-z][A-Za-z0-9]*</c>, индекс — целое без знака.</summary>
public static class ShowKeyPath
{
    /// <summary>
    /// Разбирает ключ в <paramref name="into"/>. Список очищается перед разбором.
    /// Возвращает false и заполняет <paramref name="error"/>, когда ключ не той формы.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> key, List<ShowPathStep> into, out string? error)
    {
        ArgumentNullException.ThrowIfNull(into);

        into.Clear();
        error = null;

        if (key.IsEmpty)
        {
            error = "пустой ключ";
            return false;
        }

        var start = 0;
        while (true)
        {
            var dot = key[start..].IndexOf('.');
            var end = dot < 0 ? key.Length : start + dot;

            if (!TryParseStep(key[start..end], out var step, out error))
            {
                into.Clear();
                return false;
            }

            into.Add(step);

            if (dot < 0)
            {
                return true;
            }

            start = end + 1;
        }
    }

    private static bool TryParseStep(ReadOnlySpan<char> text, out ShowPathStep step, out string? error)
    {
        step = default;

        if (text.IsEmpty)
        {
            error = "пустой шаг пути";
            return false;
        }

        var bracket = text.IndexOf('[');
        var namePart = bracket < 0 ? text : text[..bracket];

        if (!IsName(namePart))
        {
            error = "имя шага не той формы: " + namePart.ToString();
            return false;
        }

        if (bracket < 0)
        {
            step = new ShowPathStep(namePart.ToString(), null);
            error = null;
            return true;
        }

        if (text[^1] != ']')
        {
            error = "не закрыта скобка индекса: " + text.ToString();
            return false;
        }

        var indexPart = text[(bracket + 1)..^1];
        if (indexPart.IsEmpty || !int.TryParse(indexPart, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
        {
            error = "индекс не целое число: " + text.ToString();
            return false;
        }

        step = new ShowPathStep(namePart.ToString(), index);
        error = null;
        return true;
    }

    private static bool IsName(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty || !char.IsAsciiLetter(text[0]))
        {
            return false;
        }

        foreach (var c in text)
        {
            if (!char.IsAsciiLetterOrDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
