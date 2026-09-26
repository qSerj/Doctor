using System.Text.Json;

namespace PsDoctor.Workbench;

/// <summary>
/// Что пульт помнит между запусками: адрес, путь к файлу ключа (не сам ключ), каталог обмена, файл шоу с последними
/// и последний опыт. Пути вводятся один раз. Переменные окружения главнее — это решает модель, не хранилище.
/// </summary>
/// <remarks>
/// Имена полей — как их пишет <see cref="JsonSerializer"/> по умолчанию: файл прежнего пульта, где был только
/// <c>ExchangeDirectory</c>, читается без переделки.
/// </remarks>
public sealed record WorkbenchSettings(
    string? Address = null,
    string? KeyFile = null,
    string? ExchangeDirectory = null,
    string? ShowPath = null,
    IReadOnlyList<string>? RecentShows = null,
    string? Experiment = null)
{
    /// <summary>Сколько последних файлов шоу помнить.</summary>
    public const int RecentLimit = 10;

    /// <summary>Файл настроек в профиле пользователя.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PsDoctor", "Workbench", "settings.json");

    /// <summary>Нет файла или он испорчен — пустые настройки: пульт открывается и без памяти.</summary>
    public static WorkbenchSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new WorkbenchSettings();
            return JsonSerializer.Deserialize<WorkbenchSettings>(File.ReadAllText(path)) ?? new WorkbenchSettings();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new WorkbenchSettings();
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
