using System.Reflection;
using PsDoctor.Core;
using PsDoctor.Core.Format;
using PsDoctor.Core.Media;
using PsDoctor.Core.Model;
using PsDoctor.Core.Reporting;
using PsDoctor.Core.Rules;
using PsDoctor.Infrastructure;
using Учёт = PsDoctor.Core.Inventory.Inventory;

namespace PsDoctor.Cli;

/// <summary>Коды возврата. Решены раундом; прогон пачки отдаёт максимум по файлам.</summary>
public static class ExitCodes
{
    /// <summary>Разобран, находок, прошедших порог, нет. Пока порогов нет — единственный успешный исход.</summary>
    public const int Clean = 0;

    /// <summary>Есть находки, прошедшие порог. Недостижим, пока порогов нет ни у одного правила.</summary>
    public const int Findings = 1;

    /// <summary>Это не файл шоу или он не читается.</summary>
    public const int NotAShowFile = 2;

    /// <summary>Сбой окружения: нет доступа, нет инструмента, неверные аргументы.</summary>
    public const int Environment = 3;
}

/// <summary>
/// Прогон CLI. Оркестрация живёт здесь: разобрать, собрать ссылки, опросить, построить инвентарь.
/// Этот же шов делит осмотр на дешёвую и дорогую ступени, и потому переделывать его не придётся.
/// </summary>
/// <remarks>
/// Отчёт идёт только в stdout, диагностика только в stderr: одна посторонняя строка в stdout
/// ломает машинного потребителя. Записи в файл нет и не будет — «CLI в чужой каталог не пишет
/// никогда» надёжнее обеспечивается отсутствием умения, чем дисциплиной.
/// </remarks>
public static class Runner
{
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException e)
        {
            stderr.WriteLine(e.Message);
            WriteUsage(stderr);
            return ExitCodes.Environment;
        }

        if (options.WantsHelp)
        {
            WriteUsage(stdout);
            return ExitCodes.Clean;
        }

        if (options.WantsVersion)
        {
            stdout.WriteLine(Version());
            return ExitCodes.Clean;
        }

        if (options.Paths.Count == 0)
        {
            stderr.WriteLine("Не указан ни один файл шоу.");
            WriteUsage(stderr);
            return ExitCodes.Environment;
        }

        // Псевдонимы устойчивы внутри пачки: один файл, подключённый к двум проектам,
        // получает один псевдоним, и обобщать по нему можно.
        var masker = options.Anonymize
            ? new AliasMasker(options.Salt ?? Guid.NewGuid().ToString("n"))
            : (IValueMasker)PassThroughMasker.Instance;

        var worst = ExitCodes.Clean;

        foreach (var path in options.Paths)
        {
            var code = RunOne(path, options, masker, stdout, stderr, now);
            worst = Math.Max(worst, code);
        }

        return worst;
    }

    private static int RunOne(
        string path,
        Options options,
        IValueMasker masker,
        TextWriter stdout,
        TextWriter stderr,
        DateTimeOffset now)
    {
        long? fileBytes = null;

        try
        {
            if (!File.Exists(path))
            {
                stderr.WriteLine($"Файл не найден: {path}");
                return ExitCodes.NotAShowFile;
            }

            fileBytes = new FileInfo(path).Length;

            ParseResult parse;
            using (var reader = ShowFileEncoding.OpenRead(path))
            {
                parse = ShowFileParser.Parse(reader);
            }

            if (!parse.MagicMatched || parse.Document is null)
            {
                stderr.WriteLine($"Это не файл шоу: первая строка не совпала с сигнатурой ({path})");
                ReportWriter.Write(stdout, ReportBuilder.NotAShowFile(path, fileBytes, Version(), now, masker), options.Pretty);
                return ExitCodes.NotAShowFile;
            }

            var show = Show.From(parse.Document);
            var catalog = Probe(path, show);
            var inventory = Учёт.Build(show, catalog);
            var dictionary = FormatDictionary.From(parse.Document);

            var context = new RuleContext(RuleSettings.Default, Path.GetDirectoryName(Path.GetFullPath(path)));
            var findings = AcceptanceRules.Run(inventory, context);

            var report = ReportBuilder.Build(path, fileBytes, parse, inventory, dictionary, findings, Version(), now, masker);
            ReportWriter.Write(stdout, report, options.Pretty);

            // Единицу дают только находки, прошедшие порог. Порогов пока нет ни у одного правила,
            // поэтому исход остаётся нулевым: правила считаются и пишутся, но кода не меняют.
            // Так разделение 0 и 1 осмысленно с первого дня, а не с того, как пороги наберутся.
            return findings.Any(f => f.PassedThreshold) ? ExitCodes.Findings : ExitCodes.Clean;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"Не удалось прочитать {path}: {e.Message}");
            return ExitCodes.Environment;
        }
    }

    private static MediaCatalog Probe(string showFilePath, Show show)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(showFilePath));
        if (directory is null)
        {
            return MediaCatalog.Empty;
        }

        var probe = new FileMediaProbe(directory);
        return probe.ProbeAll(show.AllLayers.Select(l => l.Image).OfType<MediaReference>());
    }

    private static string Version() =>
        typeof(Runner).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(Runner).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    private static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine("psdoctor <путь.psh> [ещё пути…] [ключи]");
        writer.WriteLine();
        writer.WriteLine("  --pretty               отчёт с отступами, для чтения глазами при отладке");
        writer.WriteLine("  --anonymize            обезличенный срез: пути и имена заменяются псевдонимами");
        writer.WriteLine("  --anonymize-salt <с>   соль псевдонимов, чтобы они совпадали между прогонами");
        writer.WriteLine("  --version              версия доктора");
        writer.WriteLine("  --help                 эта справка");
        writer.WriteLine();
        writer.WriteLine("psdoctor observe --help — команды к наблюдателю на стенде.");
        writer.WriteLine();
        writer.WriteLine("Отчёт машинный и идёт в stdout, по строке на файл. Доктор ничего не лечит и никуда не пишет.");
        writer.WriteLine($"Коды возврата: {ExitCodes.Clean} — разобран, находок нет; {ExitCodes.Findings} — есть находки; "
            + $"{ExitCodes.NotAShowFile} — не файл шоу; {ExitCodes.Environment} — сбой окружения.");
    }

    private sealed class Options
    {
        public List<string> Paths { get; } = [];

        public bool Pretty { get; private set; }

        public bool Anonymize { get; private set; }

        public string? Salt { get; private set; }

        public bool WantsHelp { get; private set; }

        public bool WantsVersion { get; private set; }

        public static Options Parse(string[] args)
        {
            var options = new Options();

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--pretty":
                        options.Pretty = true;
                        break;

                    case "--anonymize":
                        options.Anonymize = true;
                        break;

                    case "--anonymize-salt":
                        if (++i >= args.Length)
                        {
                            throw new ArgumentException("У ключа --anonymize-salt не указано значение.");
                        }

                        options.Salt = args[i];
                        options.Anonymize = true;
                        break;

                    case "--help" or "-h" or "-?":
                        options.WantsHelp = true;
                        break;

                    case "--version":
                        options.WantsVersion = true;
                        break;

                    default:
                        if (args[i].StartsWith('-'))
                        {
                            throw new ArgumentException($"Неизвестный ключ: {args[i]}");
                        }

                        options.Paths.Add(args[i]);
                        break;
                }
            }

            return options;
        }
    }
}
