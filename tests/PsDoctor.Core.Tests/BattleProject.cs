using PsDoctor.Core;

namespace PsDoctor.Core.Tests;

/// <summary>
/// Граница между открытым репозиторием и закрытым материалом.
/// Боевые проекты — чужая клиентская работа: они лежат во дворе (<c>inbound/</c>) и в закрытой
/// памяти (<c>Memorex/sources/</c>), и ни один из этих каталогов не версионируется.
/// Тесты, которым они нужны, находят их сами и не выполняются, когда материала нет.
/// Имена боевых файлов здесь намеренно не записаны: они содержат названия клиентских работ.
/// </summary>
internal static class BattleProject
{
    /// <summary>Каталоги, где ищется боевой материал, относительно корня репозитория.</summary>
    private static readonly string[] Yards = ["inbound", Path.Combine("Memorex", "sources")];

    /// <summary>
    /// Все найденные файлы шоу, в устойчивом порядке.
    /// Порядок устойчив, чтобы номер проекта в сообщении об ошибке значил одно и то же между прогонами.
    /// </summary>
    public static IReadOnlyList<string> FindShowFiles()
    {
        var root = FindRepositoryRoot();
        if (root is null)
        {
            return [];
        }

        var found = new List<string>();
        foreach (var yard in Yards)
        {
            var directory = Path.Combine(root, yard);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            found.AddRange(Directory.EnumerateFiles(directory, "*" + ShowFile.Extension, SearchOption.AllDirectories));
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    /// <summary>
    /// Каталог с медиа рядом с файлом шоу, или <c>null</c>, когда медиа не приехали.
    /// Разделение обязательно: у проекта из закрытой памяти медиа рядом нет, у проекта из двора есть,
    /// и тест на разбор должен пропускаться отдельно от теста на сумму пикселей.
    /// </summary>
    public static string? FindMediaRoot(string showFilePath)
    {
        var directory = Path.GetDirectoryName(showFilePath);
        if (directory is null)
        {
            return null;
        }

        return Directory.Exists(Path.Combine(directory, "image")) ? directory : null;
    }

    /// <summary>
    /// Неопознающая подпись проекта для сообщений об ошибках: порядковый номер, а не имя.
    /// Имя содержит название клиентской работы и в вывод тестов попадать не должно.
    /// </summary>
    public static string Label(int ordinal) => "боевой проект №" + (ordinal + 1);

    private static string? FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PsDoctor.slnx")))
            {
                return dir.FullName;
            }
        }

        return null;
    }
}
