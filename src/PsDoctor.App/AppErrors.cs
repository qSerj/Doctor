using System.Text.Json;
using PsDoctor.Core.Observation;

namespace PsDoctor.App;

/// <summary>
/// Необработанные исключения App — строкой JSON в <c>%LOCALAPPDATA%\PsDoctor\app-errors.jsonl</c>. Исключение на потоке
/// окна гасится: Doctor в трее переживает ошибку одного действия. Инженер видит в файле, что случилось.
/// </summary>
internal static class AppErrors
{
    private static readonly Lock Gate = new();

    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PsDoctor", "app-errors.jsonl");

    /// <param name="source">Где поймано: <c>ui</c>, <c>domain</c>, <c>task</c>.</param>
    /// <param name="handled">Исключение погашено и App продолжает работать.</param>
    public static void Write(Exception? error, string source, bool handled)
    {
        var line = JsonSerializer.Serialize(new
        {
            atUtc = DateTime.UtcNow,
            source,
            type = error?.GetType().FullName,
            message = error?.Message,
            stack = error?.ToString(),
            handled,
        }, ObservationJson.Options);
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.AppendAllText(Path, line + "\n");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Записать некуда — App всё равно продолжает работать.
        }
    }
}
