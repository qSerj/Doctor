using System.Security.Cryptography;
using PsDoctor.Core.Observation;

namespace PsDoctor.Infrastructure.Installation;

/// <summary>
/// Где лежит то, что установщик и сторож оставляют на машине. Программа — в каталоге установки, и её
/// никто здесь не ищет: сторож знает себя сам. Настройки общие для машины, их пишет установщик от
/// администратора. Ключ и журналы — в профиле пользователя, их установщик не трогает вовсе: поэтому
/// переустановка их не теряет, а установщику не нужно знать, чей это профиль.
/// </summary>
public sealed record InstalledLayout(string SettingsFile, string KeyFile, string DataDirectory, string WatchdogLog)
{
    public static InstalledLayout Current
    {
        get
        {
            var machine = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PsDoctor");
            var user = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PsDoctor");
            return new InstalledLayout(
                Path.Combine(machine, "settings.json"),
                Path.Combine(user, "observer.key"),
                Path.Combine(user, "observer", "sessions"),
                Path.Combine(user, "observer", "watchdog.jsonl"));
        }
    }

    /// <summary>Настройки из файла; нет файла — значения по умолчанию. Испорченный файл — ошибка, а не молчаливый умолчательный.</summary>
    public (InstalledSettings? Settings, string? Error) LoadSettings()
    {
        if (!File.Exists(SettingsFile))
        {
            return (InstalledSettings.Default, null);
        }
        try
        {
            var (settings, error) = InstalledSettings.Parse(File.ReadAllText(SettingsFile));
            return (settings, error is null ? null : $"{SettingsFile}: {error}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (null, $"{SettingsFile}: {e.Message}");
        }
    }

    /// <summary>Ключ, если он уже есть и не пуст.</summary>
    public string? ReadKey()
    {
        try
        {
            var key = File.Exists(KeyFile) ? File.ReadAllText(KeyFile).Trim() : "";
            return key.Length > 0 ? key : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Создаёт ключ, если его нет. Существующий не меняется никогда: им уже пользуются App и инженер.
    /// Файл лежит в профиле пользователя и наследует его права — чужие учётные записи его не читают.
    /// </summary>
    public string EnsureKey()
    {
        if (ReadKey() is { } existing)
        {
            return existing;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(KeyFile)!);
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        File.WriteAllText(KeyFile, key);
        return key;
    }
}
