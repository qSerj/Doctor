using System.Globalization;

namespace PsDoctor.Core.Scenarios;

public enum ScenarioErrorKind
{
    /// <summary>В сценарии нет ни одного шага.</summary>
    Empty,

    /// <summary>Шаг, которого наблюдатель не знает.</summary>
    UnknownStep,

    /// <summary>У шага не хватает аргумента.</summary>
    MissingArgument,

    /// <summary>У шага лишний аргумент.</summary>
    ExtraArgument,

    /// <summary>Секунды — не целое положительное число.</summary>
    BadSeconds,

    /// <summary>Кавычка открыта и не закрыта.</summary>
    UnterminatedQuote,
}

/// <param name="Token">Слово, на котором разбор споткнулся, если оно есть.</param>
public sealed record ScenarioError(int Line, ScenarioErrorKind Kind, string? Token);

/// <summary>Итог разбора. Сценарий есть только тогда, когда ошибок нет: частичный сценарий не выполняется.</summary>
public sealed record ScenarioParseResult(Scenario? Scenario, IReadOnlyList<ScenarioError> Errors);

/// <summary>
/// Разбор текста сценария: строка на шаг, слова через пробел, аргумент с пробелами — в двойных кавычках.
/// Пустые строки и строки с <c>#</c> в начале пропускаются: файл опыта описывает себя сам. Разбор не бросает
/// исключений на содержимое и собирает все ошибки сразу.
/// </summary>
public static class ScenarioParser
{
    public static ScenarioParseResult Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var steps = new List<ScenarioStep>();
        var errors = new List<ScenarioError>();
        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = index + 1;
            var source = lines[index].Trim();
            if (source.Length == 0 || source[0] == '#')
            {
                continue;
            }

            var words = Split(source);
            if (words is null)
            {
                errors.Add(new ScenarioError(line, ScenarioErrorKind.UnterminatedQuote, null));
                continue;
            }

            var step = ParseStep(line, source, words, out var error);
            if (step is not null)
            {
                steps.Add(step);
            }
            else
            {
                errors.Add(error!);
            }
        }

        if (steps.Count == 0 && errors.Count == 0)
        {
            errors.Add(new ScenarioError(0, ScenarioErrorKind.Empty, null));
        }

        return errors.Count == 0
            ? new ScenarioParseResult(new Scenario(steps), errors)
            : new ScenarioParseResult(null, errors);
    }

    private static ScenarioStep? ParseStep(int line, string text, List<string> words, out ScenarioError? error)
    {
        error = null;
        var arguments = new Arguments(line, words);
        ScenarioStep? step = words[0] switch
        {
            "launch" => arguments.Text(1) is { } path ? new LaunchStep(line, text, path) : null,
            "close" => new CloseStep(line, text),
            "press" => arguments.Text(1) is { } button ? new PressStep(line, text, button) : null,
            "render" => new RenderStep(line, text),
            "say" => arguments.Text(1) is { } message ? new SayStep(line, text, message) : null,
            "wait" => ParseWait(line, text, words, arguments),
            _ => null,
        };

        error = arguments.Error;
        if (step is null && error is null)
        {
            error = new ScenarioError(line, ScenarioErrorKind.UnknownStep, words[0] == "wait" && words.Count > 1 ? $"wait {words[1]}" : words[0]);
        }
        if (step is not null && error is null && words.Count > arguments.Used)
        {
            error = new ScenarioError(line, ScenarioErrorKind.ExtraArgument, words[arguments.Used]);
        }
        return error is null ? step : null;
    }

    private static WaitStep? ParseWait(int line, string text, List<string> words, Arguments arguments)
    {
        if (words.Count < 2)
        {
            arguments.Fail(ScenarioErrorKind.MissingArgument, "wait");
            return null;
        }

        // Пауза: вместо вида ожидания сразу секунды.
        if (words[1].Length > 0 && char.IsAsciiDigit(words[1][0]))
        {
            return arguments.Seconds(1) is { } duration ? new WaitPauseStep(line, text, duration) : null;
        }

        return words[1] switch
        {
            "title" => arguments.Text(2) is { } substring && arguments.Seconds(3) is { } timeout
                ? new WaitTitleStep(line, text, substring, timeout) : null,
            "dialog" => arguments.Seconds(2) is { } timeout ? new WaitDialogStep(line, text, timeout) : null,
            "exit" => arguments.Seconds(2) is { } timeout ? new WaitExitStep(line, text, timeout) : null,
            "idle" => arguments.Seconds(2) is { } quiet && arguments.Seconds(3) is { } timeout
                ? new WaitIdleStep(line, text, quiet, timeout) : null,
            "render-done" => arguments.Seconds(2) is { } timeout ? new WaitRenderDoneStep(line, text, timeout) : null,
            "confirm" => words.Count < 3
                ? new WaitConfirmStep(line, text, WaitConfirmStep.Unlimited)
                : arguments.Seconds(2) is { } timeout ? new WaitConfirmStep(line, text, timeout) : null,
            _ => null,
        };
    }

    /// <summary>Слова строки; <c>null</c> — если кавычка не закрыта.</summary>
    private static List<string>? Split(string source)
    {
        var words = new List<string>();
        var position = 0;
        while (position < source.Length)
        {
            if (char.IsWhiteSpace(source[position]))
            {
                position++;
                continue;
            }

            if (source[position] == '"')
            {
                var end = source.IndexOf('"', position + 1);
                if (end < 0)
                {
                    return null;
                }
                words.Add(source[(position + 1)..end]);
                position = end + 1;
                continue;
            }

            var start = position;
            while (position < source.Length && !char.IsWhiteSpace(source[position]))
            {
                position++;
            }
            words.Add(source[start..position]);
        }
        return words;
    }

    /// <summary>Аргументы шага по позициям; первая ошибка запоминается, остальные не нужны.</summary>
    private sealed class Arguments(int line, List<string> words)
    {
        public ScenarioError? Error { get; private set; }

        /// <summary>Сколько слов строки занял шаг, включая имя.</summary>
        public int Used { get; private set; } = words[0] == "wait" ? 2 : 1;

        public string? Text(int position)
        {
            if (position >= words.Count || words[position].Length == 0)
            {
                Fail(ScenarioErrorKind.MissingArgument, null);
                return null;
            }
            Used = Math.Max(Used, position + 1);
            return words[position];
        }

        public TimeSpan? Seconds(int position)
        {
            if (Text(position) is not { } word)
            {
                return null;
            }
            if (!int.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0)
            {
                Fail(ScenarioErrorKind.BadSeconds, word);
                return null;
            }
            return TimeSpan.FromSeconds(seconds);
        }

        public void Fail(ScenarioErrorKind kind, string? token) =>
            Error ??= new ScenarioError(line, kind, token);
    }
}
