using System.Text.Json;

namespace PsDoctor.Workbench;

/// <summary>
/// Пресет — машина, к которой ходит пульт: стенд, компьютер монтажёра. Файл <c>&lt;имя&gt;.json</c> в каталоге пресетов;
/// выбор пресета загружает файл и переподключает пульт. Пустое поле текущего значения не трогает.
/// </summary>
/// <remarks>
/// Пресеты живут в профиле пользователя, а не в репозитории: адрес машины монтажёра — не для открытого Git.
/// Ключа в пресете нет — только путь к его файлу.
/// </remarks>
public sealed record WorkbenchPreset(
    string? Address = null,
    string? KeyFile = null,
    string? ExchangeDirectory = null,
    string? ShowPath = null)
{
    public const string Extension = ".json";

    /// <summary>Каталог пресетов рядом с файлом настроек пульта.</summary>
    public static string DirectoryFor(string settingsPath) =>
        Path.Combine(Path.GetDirectoryName(settingsPath)!, "presets");

    /// <summary>Читает пресет. Испорченный файл — отказ с причиной: молча подключиться не туда хуже, чем не подключиться.</summary>
    public static WorkbenchPreset Load(string path) =>
        JsonSerializer.Deserialize<WorkbenchPreset>(File.ReadAllText(path))
        ?? throw new InvalidDataException($"пресет {path} пуст");

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
