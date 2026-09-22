using System.Text.Json;

namespace PsDoctor.Workbench;

internal static class WorkbenchSettings
{
    private static string PathName => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PsDoctor", "Workbench", "settings.json");

    public static string? LoadExchangeDirectory()
    {
        try
        {
            if (!File.Exists(PathName)) return null;
            var value = JsonSerializer.Deserialize<Settings>(File.ReadAllText(PathName));
            return value?.ExchangeDirectory;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static void SaveExchangeDirectory(string value)
    {
        var path = PathName;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new Settings(value)));
    }

    private sealed record Settings(string ExchangeDirectory);
}
