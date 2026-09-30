using System.Diagnostics;
using PsDoctor.Core.Observation;

namespace PsDoctor.Infrastructure.Observation;

/// <summary>
/// Файл глазами наблюдения: есть ли, версия, размер, время записи. Один код на факт <c>environment</c>, слепок окружения
/// и факт <c>etw-image-loaded</c> (Э6.3): модуль, загруженный программой, описан так же, как в слепке, и сравнивается с ним.
/// Без Windows: вне её версии нет, остальное читается.
/// </summary>
public static class FileDescription
{
    /// <summary>Версия файла из его ресурса; нет файла, ресурса или доступа — <c>null</c>.</summary>
    public static string? Version(string path)
    {
        try
        {
            return File.Exists(path) ? FileVersionInfo.GetVersionInfo(path).FileVersion : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Описание файла. Недоступный файл записан как отсутствующий.</summary>
    public static EnvironmentFile Describe(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists
                ? new EnvironmentFile(path, true, Version(path), info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero))
                : new EnvironmentFile(path, false, null, null, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new EnvironmentFile(path, false, null, null, null);
        }
    }
}
