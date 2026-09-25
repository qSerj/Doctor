namespace PsDoctor.Core.Scenarios;

/// <summary>
/// Шаг сценария. Набор закрыт: действия и ожидания знают только ProShow, универсальных шагов нет. Шаги
/// оператора — <c>say</c>, пауза и <c>wait confirm</c> — программу не трогают: руками работает человек.
/// </summary>
/// <param name="Line">Строка сценария, с единицы, — ею шаг называется в фактах.</param>
/// <param name="Text">Строка сценария как написана, без краевых пробелов.</param>
public abstract record ScenarioStep(int Line, string Text);

/// <summary>Действие: исполняет его подставленная реализация, исполнитель только ждёт итога.</summary>
public abstract record ActionStep(int Line, string Text) : ScenarioStep(Line, Text);

/// <summary><c>launch &lt;файл шоу&gt;</c> — запуск программы с проектом.</summary>
public sealed record LaunchStep(int Line, string Text, string ShowPath) : ActionStep(Line, Text);

/// <summary><c>close</c> — то же, что крестик главного окна.</summary>
public sealed record CloseStep(int Line, string Text) : ActionStep(Line, Text);

/// <summary><c>press &lt;текст кнопки&gt;</c> — кнопка текущего диалога.</summary>
public sealed record PressStep(int Line, string Text, string Button) : ActionStep(Line, Text);

/// <summary><c>render</c> — запуск рендера. Объявлен, способ не выяснен.</summary>
public sealed record RenderStep(int Line, string Text) : ActionStep(Line, Text);

/// <summary><c>say "&lt;текст&gt;"</c> — инструкция оператору. Выполняется сразу: сказать — не значит дождаться.</summary>
public sealed record SayStep(int Line, string Text, string Message) : ScenarioStep(Line, Text);

/// <summary>Ожидание на потоке фактов. Таймаут у каждого свой и обязателен.</summary>
public abstract record WaitStep(int Line, string Text, TimeSpan Timeout) : ScenarioStep(Line, Text);

/// <summary><c>wait title &lt;подстрока&gt; &lt;таймаут&gt;</c> — заголовок главного окна содержит подстроку.</summary>
public sealed record WaitTitleStep(int Line, string Text, string Substring, TimeSpan Timeout) : WaitStep(Line, Text, Timeout);

/// <summary><c>wait dialog &lt;таймаут&gt;</c> — диалог, ещё не засчитанный другому шагу.</summary>
public sealed record WaitDialogStep(int Line, string Text, TimeSpan Timeout) : WaitStep(Line, Text, Timeout);

/// <summary><c>wait exit &lt;таймаут&gt;</c> — программа завершилась.</summary>
public sealed record WaitExitStep(int Line, string Text, TimeSpan Timeout) : WaitStep(Line, Text, Timeout);

/// <summary><c>wait idle &lt;секунд покоя&gt; &lt;таймаут&gt;</c> — процессор и ввод-вывод куста в покое столько секунд подряд.</summary>
public sealed record WaitIdleStep(int Line, string Text, TimeSpan Quiet, TimeSpan Timeout) : WaitStep(Line, Text, Timeout);

/// <summary><c>wait render-done &lt;таймаут&gt;</c> — объявлен вместе с <c>render</c>.</summary>
public sealed record WaitRenderDoneStep(int Line, string Text, TimeSpan Timeout) : WaitStep(Line, Text, Timeout);

/// <summary><c>wait &lt;секунды&gt;</c> — пауза по часам, без условия: шаг выполнен, когда истёк таймаут.</summary>
public sealed record WaitPauseStep(int Line, string Text, TimeSpan Duration) : WaitStep(Line, Text, Duration);

/// <summary>
/// <c>wait confirm [&lt;таймаут&gt;]</c> — оператор подтвердил, что сделал сказанное. Без таймаута ждёт, пока
/// подтвердят, отменят сценарий или выйдет программа: сколько человеку делать сказанное, сценарий не знает.
/// </summary>
public sealed record WaitConfirmStep(int Line, string Text, TimeSpan Timeout) : WaitStep(Line, Text, Timeout)
{
    /// <summary>Таймаут шага без таймаута в тексте.</summary>
    public static readonly TimeSpan Unlimited = TimeSpan.MaxValue;
}

/// <summary>Сценарий — шаги по порядку, без переменных, ветвлений и циклов.</summary>
public sealed record Scenario(IReadOnlyList<ScenarioStep> Steps)
{
    /// <summary>Есть шаг, который трогает программу. Пассивный сеанс такой сценарий не принимает.</summary>
    public bool DrivesProgram => Steps.Any(step => step is ActionStep);
}
