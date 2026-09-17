namespace PsDoctor.Core.Scenarios;

/// <summary>
/// То, что исполнитель знает о программе. Сигналы выводятся из фактов сеанса снаружи ядра;
/// у каждого — время от старта сеанса, и других часов у исполнителя нет: таймауты считаются по ним.
/// </summary>
public abstract record ScenarioSignal(TimeSpan At);

/// <summary>Время идёт, а ничего не происходит. Без таких сигналов ожидание не истечёт никогда.</summary>
public sealed record TimeTick(TimeSpan At) : ScenarioSignal(At);

public sealed record DialogOpened(TimeSpan At, DialogInfo Dialog) : ScenarioSignal(At);

public sealed record DialogClosed(TimeSpan At, long Handle) : ScenarioSignal(At);

/// <summary>Заголовок главного окна; <c>null</c> — главного окна нет.</summary>
public sealed record TitleChanged(TimeSpan At, string? Title) : ScenarioSignal(At);

/// <summary>Замер куста: был ли он в покое за прошедший отрезок. Что считать покоем, решает тот, кто замеряет.</summary>
public sealed record ActivitySampled(TimeSpan At, bool Quiet) : ScenarioSignal(At);

public sealed record ProgramExited(TimeSpan At, int? ExitCode) : ScenarioSignal(At);

/// <param name="Handle">Хэндл окна диалога — по нему диалог узнаётся при закрытии.</param>
/// <param name="Texts">Тексты дочерних <c>Static</c>; у окон, где текст нарисован программой, пусто.</param>
/// <param name="Buttons">Тексты кнопок по порядку.</param>
/// <param name="Class">Класс окна: <c>#32770</c> — системный диалог, <c>AGDSDocParent</c> — окно самой программы.</param>
/// <param name="ProcessId">Процесс куста, которому принадлежит диалог.</param>
public sealed record DialogInfo(long Handle, string? Title, IReadOnlyList<string> Texts, IReadOnlyList<string> Buttons, string? Class = null, int? ProcessId = null);
