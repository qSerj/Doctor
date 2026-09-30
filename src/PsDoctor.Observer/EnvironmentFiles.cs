using System.Text.Json;
using PsDoctor.Core.Observation;

namespace PsDoctor.Observer;

/// <summary>
/// Слепки окружения (Э6.3) рядом с журналами: <c>environment/&lt;идентификатор&gt;.json</c>. Файл пишется, только если
/// такого слепка ещё нет, — у неизменной машины он один на все сеансы. Хранение журналов этот каталог не трогает.
/// </summary>
public static class EnvironmentFiles
{
    public static string Directory(string sessions) => Path.Combine(sessions, "environment");

    public static string Snapshot(string sessions, string id) => Path.Combine(Directory(sessions), id + ".json");

    /// <summary>Идентификатор слепка — шестнадцать строчных шестнадцатеричных знаков; иное в путь не попадает.</summary>
    public static bool IsValidId(string? id) =>
        id is { Length: 16 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>
    /// Кладёт слепок, если его ещё нет. Пишет во временный файл и переименовывает: оборванная запись не оставит
    /// полслепка под настоящим именем.
    /// </summary>
    /// <returns>Слепок лежит на месте — записан сейчас или был раньше.</returns>
    public static bool Save(string sessions, EnvironmentSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!IsValidId(snapshot.Id))
        {
            return false;
        }
        var path = Snapshot(sessions, snapshot.Id);
        if (File.Exists(path))
        {
            return true;
        }
        var temporary = path + ".tmp";
        try
        {
            System.IO.Directory.CreateDirectory(Directory(sessions));
            File.WriteAllText(temporary, EnvironmentSnapshotJson.Serialize(snapshot));
            File.Move(temporary, path, overwrite: false);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Тот же слепок мог лечь другим сеансом между проверкой и переименованием.
            TryDelete(temporary);
            return File.Exists(path);
        }
    }

    /// <returns><c>null</c> — идентификатор негодный, файла нет или он не читается.</returns>
    public static EnvironmentSnapshot? Load(string sessions, string id)
    {
        if (!IsValidId(id))
        {
            return null;
        }
        try
        {
            return EnvironmentSnapshotJson.Deserialize(File.ReadAllText(Snapshot(sessions, id)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
