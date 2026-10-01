namespace PsDoctor.Observer;

/// <summary>
/// Какие модули уже названы: загрузка модуля — одна запись на пару «образ процесса и путь модуля» за сеанс. Рендер
/// запускает сотни одинаковых <c>device-enc.dll</c> и <c>conhost.exe</c> с одними и теми же библиотеками; запись на
/// каждый процесс добавляла журналу рендера 56 % (Э6.3, опыт 5, 30.09.2026), а нового не говорила. Новый образ или новая
/// библиотека у знакомого образа по-прежнему видны при первом появлении.
/// Один учёт у помощника ETW (меньше сводки) и у моста (один факт за сеанс, даже если помощник перезапускался и прислал
/// загрузки заново). Образ сравнивается без расширения — ETW называет его то <c>fvideo.exe</c>, то <c>fvideo</c>; образ
/// неизвестен — вместо него номер процесса. Сравнение без регистра, как в Windows. Не потокобезопасен.
/// </summary>
public sealed class ModuleLoads
{
    private readonly HashSet<(string Image, string Path)> seen = new(new KeyComparer());

    /// <summary><c>true</c> — пара новая и записывается.</summary>
    public bool Add(string? image, int processId, string path) => seen.Add((Key(image, processId), path));

    /// <summary>Образ без расширения, неизвестный — номер процесса; тот же ключ у сводки файлов (<see cref="FileActivity"/>).</summary>
    internal static string Key(string? image, int processId) =>
        string.IsNullOrEmpty(image) ? "#" + processId : System.IO.Path.GetFileNameWithoutExtension(image);

    private sealed class KeyComparer : IEqualityComparer<(string Image, string Path)>
    {
        public bool Equals((string Image, string Path) x, (string Image, string Path) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Image, y.Image) && StringComparer.OrdinalIgnoreCase.Equals(x.Path, y.Path);

        public int GetHashCode((string Image, string Path) value) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(value.Image), StringComparer.OrdinalIgnoreCase.GetHashCode(value.Path));
    }
}
