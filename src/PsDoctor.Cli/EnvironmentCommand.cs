using System.Globalization;
using System.Text.Json;
using PsDoctor.Core.Observation;

namespace PsDoctor.Cli;

/// <summary>Коды возврата <c>psdoctor environment</c> — как у <c>diff</c>: сценарий видит «разные» без разбора вывода.</summary>
public static class EnvironmentExitCodes
{
    public const int Same = 0;

    public const int Different = 1;

    /// <summary>Файл не прочёлся, не слепок, неверные аргументы.</summary>
    public const int Error = 3;
}

/// <summary>
/// <c>psdoctor environment diff &lt;a.json&gt; &lt;b.json&gt; [--machines]</c> — чем две машины различаются для программы (Э6.3).
/// Работает без наблюдателя и на любой ОС: слепки — файлы, снятые <c>psdoctor observe environment --out</c> или
/// лежащие в пакете сеанса. Вердиктов нет: разница, а не суждение.
/// </summary>
public static class EnvironmentCommand
{
    public static int Run(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        var json = args.Contains("--json");
        var mode = args.Contains("--machines") ? EnvironmentComparisonMode.Machines : EnvironmentComparisonMode.SameMachine;
        var files = args.Where(arg => arg is not ("--json" or "--machines")).ToList();
        if (files is ["--help" or "-h"])
        {
            WriteUsage(stdout);
            return EnvironmentExitCodes.Same;
        }
        if (files is not ["diff", var first, var second] || first.StartsWith("--", StringComparison.Ordinal) || second.StartsWith("--", StringComparison.Ordinal))
        {
            WriteUsage(stderr);
            return EnvironmentExitCodes.Error;
        }

        if (Read(first, stderr) is not { } before || Read(second, stderr) is not { } after)
        {
            return EnvironmentExitCodes.Error;
        }
        var diff = EnvironmentComparison.Compare(before, after, mode);
        if (json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(diff, ObservationJson.Options));
        }
        else
        {
            WriteText(diff, stdout);
        }
        return diff.IsEmpty ? EnvironmentExitCodes.Same : EnvironmentExitCodes.Different;
    }

    private static EnvironmentSnapshot? Read(string path, TextWriter stderr)
    {
        try
        {
            return EnvironmentSnapshotJson.Deserialize(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            stderr.WriteLine($"Не удалось прочитать слепок {path}: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Для чтения глазами: строка итога, дальше записи по разделам — <c>+</c> только во втором слепке, <c>-</c> только в
    /// первом, <c>~</c> изменилась, с полями «было → стало».
    /// </summary>
    public static void WriteText(EnvironmentDiff diff, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(diff);
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteLine($"{diff.BeforeId} → {diff.AfterId}: добавлено {Count(diff.Added.Count)}, убрано {Count(diff.Removed.Count)}, "
            + $"изменено {Count(diff.Changed.Count)}");
        foreach (var entry in diff.Added)
        {
            writer.WriteLine($"+ {Describe(entry)}");
        }
        foreach (var entry in diff.Removed)
        {
            writer.WriteLine($"- {Describe(entry)}");
        }
        foreach (var change in diff.Changed)
        {
            writer.WriteLine($"~ {change.Section} {View(change.View)} {change.Key}");
            foreach (var field in change.Fields)
            {
                writer.WriteLine($"    {field.Name}: {field.Before ?? "—"} → {field.After ?? "—"}");
            }
        }
    }

    private static string Describe(EnvironmentEntry entry)
    {
        // Тождество — через пробел, как у изменённой записи; поля — через два.
        var parts = new List<string> { $"{entry.Section} {View(entry.View)} {entry.Key}" };
        parts.AddRange(entry.Values.Where(pair => pair.Value is not null).OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}={pair.Value}"));
        if (entry.File is { } file)
        {
            parts.Add(file.Exists ? $"{file.Path} {file.Version ?? "без версии"}" : $"{file.Path} нет файла");
        }
        return string.Join("  ", parts);
    }

    private static string View(int? view) => view?.ToString(CultureInfo.InvariantCulture) ?? "-";

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine("psdoctor environment diff <a.json> <b.json> [--machines] [--json]");
        writer.WriteLine();
        writer.WriteLine("  Разница двух слепков окружения: + только во втором, - только в первом, ~ изменилось.");
        writer.WriteLine("  Регистр в путях не различается. Без --machines слепки — одна машина до и после.");
        writer.WriteLine("  --machines  слепки двух машин: время записи файлов не сравнивается");
        writer.WriteLine("  --json      разница одной строкой JSON");
        writer.WriteLine();
        writer.WriteLine($"Коды возврата: {EnvironmentExitCodes.Same} — одинаковы; {EnvironmentExitCodes.Different} — различаются; "
            + $"{EnvironmentExitCodes.Error} — ошибка.");
    }
}
