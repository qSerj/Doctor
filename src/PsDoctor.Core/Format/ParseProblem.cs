namespace PsDoctor.Core.Format;

/// <summary>
/// Что в файле шоу отклонилось от наблюдавшегося формата.
/// Это не находка в проекте: находке нужен рецепт и кнопка, а здесь сигнал
/// «формат понят хуже, чем кажется». Разбор из-за проблемы не прерывается.
/// </summary>
public enum ParseProblemKind
{
    /// <summary>Непустая строка без знака равенства.</summary>
    NoSeparator,

    /// <summary>Пустая строка внутри файла.</summary>
    BlankLine,

    /// <summary>Ключ не разбирается на шаги.</summary>
    MalformedKey,

    /// <summary>Тот же ключ встретился второй раз. Побеждает последнее значение.</summary>
    DuplicateKey,

    /// <summary>Имя занято и как лист, и как узел. Побеждает узел, лист сохраняется.</summary>
    LeafNodeConflict,

    /// <summary>
    /// Индекс элемента вышел за пределы объявленного счётчика: файл противоречит сам себе.
    /// Обратное — счётчик больше набора — нормально: элемент, у которого все значения
    /// умолчальные, не записывается вовсе.
    /// </summary>
    CountMismatch,
}

public sealed record ParseProblem(int LineNumber, string? RawKey, ParseProblemKind Kind, string Detail)
{
    public override string ToString() =>
        $"строка {LineNumber}: {Kind} — {Detail}";
}
