using System.Globalization;
using System.Text;

namespace PsDoctor.Core.Format;

/// <summary>Разобранный файл шоу: дерево, построенное из плоских ключей.</summary>
public sealed class ShowDocument
{
    internal ShowDocument(ShowNode root, int lineCount, int keyCount)
    {
        Root = root;
        LineCount = lineCount;
        KeyCount = keyCount;
    }

    public ShowNode Root { get; }

    /// <summary>Строк в файле, включая строку сигнатуры.</summary>
    public int LineCount { get; }

    /// <summary>Ключей, легших в дерево.</summary>
    public int KeyCount { get; }
}

/// <summary>Итог разбора. Документ есть только тогда, когда совпала сигнатура.</summary>
public sealed class ParseResult
{
    internal ParseResult(bool magicMatched, string firstLine, ShowDocument? document, IReadOnlyList<ParseProblem> problems)
    {
        MagicMatched = magicMatched;
        FirstLine = firstLine;
        Document = document;
        Problems = problems;
    }

    public bool MagicMatched { get; }

    public string FirstLine { get; }

    public ShowDocument? Document { get; }

    public IReadOnlyList<ParseProblem> Problems { get; }
}

/// <summary>
/// Разбор файла шоу. Ядро получает текст, а не путь: дверь к файлу остаётся одна,
/// и разбор работает над текстом, а не над файловой системой.
/// </summary>
/// <remarks>
/// Парсер не бросает исключений на содержимое. Весь смысл продукта — разбирать сломанные проекты;
/// парсер, падающий на кривой строке, бесполезен ровно там, где нужен.
/// </remarks>
public static class ShowFileParser
{
    /// <summary>
    /// Имя массива — имя его счётчика. Таблица выведена из двух настоящих проектов, а не придумана:
    /// счётчик всегда лежит соседом массива в том же узле. У <c>modifiers</c> счётчика нет вовсе.
    /// </summary>
    private static readonly Dictionary<string, string> Counters = new(StringComparer.Ordinal)
    {
        ["cell"] = "cells",
        ["sound"] = "sounds",
        ["captionStyle"] = "nrOfCaptionStyles",
        ["modifier"] = "modifierCount",
        ["images"] = "nrOfImages",
        ["caption"] = "captions",
        ["mappedCaption"] = "mappedCaptions",
        ["keyframes"] = "nrOfKeyframes",
        ["colormap"] = "colorMapSize",
        ["action"] = "nrOfActions",
        ["parameter"] = "nrOfParameters",
        ["point"] = "nrOfPoints",
    };

    public static ParseResult Parse(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var problems = new List<ParseProblem>();

        var firstLine = reader.ReadLine();
        if (firstLine is null || !ShowFile.LooksLikeShowFile(firstLine))
        {
            return new ParseResult(false, firstLine ?? string.Empty, null, problems);
        }

        var root = new ShowNode(string.Empty, null);
        var steps = new List<ShowPathStep>(8);
        var lineNumber = 1;
        var keyCount = 0;

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;

            if (line.Length == 0)
            {
                problems.Add(new ParseProblem(lineNumber, null, ParseProblemKind.BlankLine, "пустая строка"));
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator < 0)
            {
                problems.Add(new ParseProblem(lineNumber, line, ParseProblemKind.NoSeparator, "в строке нет знака равенства"));
                continue;
            }

            var key = line.AsSpan(0, separator);
            var raw = line[(separator + 1)..];

            if (!ShowKeyPath.TryParse(key, steps, out var error))
            {
                problems.Add(new ParseProblem(lineNumber, key.ToString(), ParseProblemKind.MalformedKey, error ?? "ключ не разбирается"));
                continue;
            }

            Place(root, steps, new ShowValue(raw, lineNumber), problems);
            keyCount++;
        }

        VerifyCounters(root, problems);

        return new ParseResult(true, firstLine, new ShowDocument(root, lineNumber, keyCount), problems);
    }

    private static void Place(ShowNode root, List<ShowPathStep> steps, ShowValue value, List<ParseProblem> problems)
    {
        var current = root;

        for (var i = 0; i < steps.Count - 1; i++)
        {
            var step = steps[i];

            if (step.Index is null)
            {
                if (current.HasScalar(step.Name))
                {
                    problems.Add(new ParseProblem(
                        value.LineNumber,
                        Describe(steps),
                        ParseProblemKind.LeafNodeConflict,
                        $"имя «{step.Name}» занято значением и узлом одновременно"));
                }

                current = current.GetOrAddChild(step.Name);
            }
            else
            {
                current = current.GetOrAddArray(step.Name).GetOrAdd(step.Index.Value);
            }
        }

        var last = steps[^1];

        if (last.Index is null)
        {
            if (current.Children.ContainsKey(last.Name) || current.Arrays.ContainsKey(last.Name))
            {
                problems.Add(new ParseProblem(
                    value.LineNumber,
                    Describe(steps),
                    ParseProblemKind.LeafNodeConflict,
                    $"имя «{last.Name}» занято узлом и значением одновременно"));
            }

            if (!current.TrySetScalar(last.Name, value, out var previous))
            {
                problems.Add(new ParseProblem(
                    value.LineNumber,
                    Describe(steps),
                    ParseProblemKind.DuplicateKey,
                    $"ключ уже встречался на строке {previous.LineNumber}; победило последнее значение"));
            }

            return;
        }

        var item = current.GetOrAddArray(last.Name).GetOrAdd(last.Index.Value);

        if (item.OwnValue is { } existing)
        {
            problems.Add(new ParseProblem(
                value.LineNumber,
                Describe(steps),
                ParseProblemKind.DuplicateKey,
                $"ключ уже встречался на строке {existing.LineNumber}; победило последнее значение"));
        }

        item.OwnValue = value;
    }

    private static string Describe(List<ShowPathStep> steps)
    {
        var builder = new StringBuilder();
        foreach (var step in steps)
        {
            if (builder.Length > 0)
            {
                builder.Append('.');
            }

            builder.Append(step.ToString());
        }

        return builder.ToString();
    }

    private static void VerifyCounters(ShowNode node, List<ParseProblem> problems)
    {
        foreach (var (name, array) in node.Arrays)
        {
            foreach (var item in array.Items)
            {
                VerifyCounters(item, problems);
            }

            // Счётчик спрашивается только там, где он есть. У `modifiers` его нет не по недосмотру:
            // индекс там — номер слота модифицируемого атрибута, а не место в списке,
            // и у одного слоя встречаются слоты 0 и 3 без всяких промежуточных.
            if (!Counters.TryGetValue(name, out var counterName))
            {
                continue;
            }

            // Единственное настоящее противоречие — элемент с индексом за пределами счётчика:
            // файл несёт то, чего сам не признаёт. Обратное нормально и встречается в живом
            // материале: `nrOfParameters=3` при двух записанных параметрах означает, что третий
            // целиком умолчальный, а умолчальное в этом формате не записывается вовсе.
            var declared = node.Count(counterName);
            var beyond = array.Items.Where(item => item.Index >= declared).ToList();

            if (beyond.Count > 0)
            {
                problems.Add(new ParseProblem(
                    node.Scalar(counterName)?.LineNumber ?? 0,
                    counterName,
                    ParseProblemKind.CountMismatch,
                    $"«{counterName}» объявляет {declared.ToString(CultureInfo.InvariantCulture)}, "
                    + $"а «{name}» несёт индекс {beyond[^1].Index?.ToString(CultureInfo.InvariantCulture)}"));
            }

        }

        foreach (var child in node.Children.Values)
        {
            VerifyCounters(child, problems);
        }
    }
}
