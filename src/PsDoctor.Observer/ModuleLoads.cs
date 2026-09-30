namespace PsDoctor.Observer;

/// <summary>
/// Какие модули уже названы по процессам: загрузка модуля — одна запись на пару процесс и путь, повторные загрузки той же
/// библиотеки не пишутся. Один учёт у помощника ETW (меньше сводки) и у моста (один факт за сеанс, даже если помощник
/// перезапускался и прислал загрузки заново). Путь сравнивается без регистра, как в Windows. Не потокобезопасен.
/// </summary>
public sealed class ModuleLoads
{
    private readonly Dictionary<int, HashSet<string>> seen = [];

    /// <summary><c>true</c> — пара новая и записывается.</summary>
    public bool Add(int processId, string path)
    {
        if (!seen.TryGetValue(processId, out var paths))
        {
            seen[processId] = paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        return paths.Add(path);
    }

    /// <summary>Процесс кончился или номер занят новым: его загрузки снова новые.</summary>
    public void Forget(int processId) => seen.Remove(processId);
}
