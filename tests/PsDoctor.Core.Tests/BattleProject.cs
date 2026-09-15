using PsDoctor.Core;

namespace PsDoctor.Core.Tests;

/// <summary>
/// Граница между открытым репозиторием и закрытым хранилищем памяти.
/// Боевой проект — чужая клиентская работа, он лежит в Memorex/ и сюда не копируется.
/// Тесты, которым он нужен, находят его сами и не выполняются, когда его нет.
/// Имя файла здесь намеренно не записано: оно содержит название клиентской работы.
/// </summary>
internal static class BattleProject
{
    public static string? FindShowFile()
    {
        var root = FindRepositoryRoot();
        if (root is null)
        {
            return null;
        }

        var sources = Path.Combine(root, "Memorex", "sources");
        return Directory.Exists(sources)
            ? Directory.EnumerateFiles(sources, "*" + ShowFile.Extension, SearchOption.AllDirectories).FirstOrDefault()
            : null;
    }

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
