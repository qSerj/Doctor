using PsDoctor.Core.Model;

namespace PsDoctor.Core.Rules;

/// <summary>Кто выполняет лечение. Записано в карточке правила заранее и не выбирается на лету.</summary>
public enum ExecutionLevel
{
    /// <summary>Действия нет вовсе: правило только сообщает.</summary>
    None,

    /// <summary>Доктор делает сам, головным инструментом без интерфейса.</summary>
    Self,

    /// <summary>Внешняя программа вызывается как исполнитель.</summary>
    Delegate,

    /// <summary>Доктор открывает файл в редакторе и перепроверяет, когда файл сохранён.</summary>
    OpenAndLeave,
}

/// <summary>Насколько правилу верят. Берётся из карточки в каталоге правил.</summary>
public enum RuleConfidence
{
    High,
    Medium,
    Hypothesis,
}

/// <summary>
/// Находка: правило плюс конкретный объект. Не правило на проект.
/// </summary>
/// <remarks>
/// Человеческих фраз здесь нет и не будет: слова живут в окне, а две копии формулировок
/// однажды разойдутся. Наружу идут устойчивый идентификатор правила, адрес объекта и числа,
/// на которых правило сработало.
/// </remarks>
/// <param name="PassedThreshold">
/// Прошла ли находка порог. Порогов пока нет ни у одного правила, поэтому здесь всегда ложь:
/// правило считается и пишется, но кода возврата не меняет. Это решённое поведение, а не недоделка.
/// </param>
public sealed record Finding(
    string RuleId,
    ObjectAddress Address,
    ExecutionLevel Level,
    RuleConfidence Confidence,
    bool PassedThreshold,
    IReadOnlyDictionary<string, long> Numbers);

/// <summary>
/// Настройки, от которых зависят правила. Множитель запаса — настройка, а не константа в коде:
/// он не проверен на живом материале и будет меняться.
/// </summary>
/// <param name="SizeReserve">
/// Запас на будущее при уменьшении картинки. Полтора, а не два: множитель линейный, по пикселям
/// он возводится в квадрат, и двойной запас обнулял бы лечение для самого частого класса файлов.
/// </param>
public sealed record RuleSettings(double SizeReserve = 1.5)
{
    public static readonly RuleSettings Default = new();
}

/// <summary>
/// То, что правилам нужно знать сверх инвентаря.
/// </summary>
/// <param name="ProjectDirectory">
/// Каталог, в котором проект лежит на самом деле. Нужен одному правилу — о чужом корне, —
/// и приходит строкой: ядро её сравнивает, но ничего по ней не открывает.
/// </param>
public sealed record RuleContext(RuleSettings Settings, string? ProjectDirectory = null)
{
    public static readonly RuleContext Bare = new(RuleSettings.Default);
}
