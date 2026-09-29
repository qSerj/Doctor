using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PsDoctor.Infrastructure.Observation;

/// <summary>Где ProShow и откуда это известно: строка <c>program-resolved</c> в журнале сторожа.</summary>
/// <param name="Source"><see cref="ProShowLocator.Settings"/>, <see cref="ProShowLocator.Association"/>, <see cref="ProShowLocator.ProgramFiles"/> или <see cref="ProShowLocator.Default"/>.</param>
/// <param name="Exists">Файл есть в момент поиска.</param>
public sealed record ProgramLocation(string Path, string Source, bool Exists);

/// <summary>
/// Поиск ProShow — один порядок у сторожа и App (Э6.2, часть Д): путь из настроек — как есть, даже если файла нет,
/// чтобы ошибка инженера была видна; иначе исполняемый файл ассоциации <c>.psh</c>, если это <c>proshow.exe</c> и он
/// есть; иначе <c>Photodex\ProShow Producer\proshow.exe</c> в Program Files (x86) и Program Files; иначе обычное место.
/// Раньше путь был зашит, и ProShow в другом каталоге дежурство молча не видело: подключение отказывало
/// <c>program-not-running</c>.
/// </summary>
public static class ProShowLocator
{
    public const string Settings = "settings";
    public const string Association = "association";
    public const string ProgramFiles = "program-files";
    public const string Default = "default";

    private const string ImageName = "proshow.exe";

    /// <summary>Выбор без обращения к Windows: ассоциацию, места и проверку файла даёт вызывающий.</summary>
    /// <param name="fallback">Обычное место — последний запас, даже если файла там нет.</param>
    public static ProgramLocation Choose(string? configured, string? association, IReadOnlyList<string> candidates, string fallback,
        Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentException.ThrowIfNullOrEmpty(fallback);
        ArgumentNullException.ThrowIfNull(exists);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return new ProgramLocation(configured, Settings, exists(configured));
        }
        // Имя сверяется по обеим косым: выбор проверяется тестами и вне Windows, где обратная косая — не разделитель.
        if (!string.IsNullOrWhiteSpace(association)
            && association[(association.LastIndexOfAny(['\\', '/']) + 1)..].Equals(ImageName, StringComparison.OrdinalIgnoreCase)
            && exists(association))
        {
            return new ProgramLocation(association, Association, true);
        }
        foreach (var candidate in candidates)
        {
            if (exists(candidate))
            {
                return new ProgramLocation(candidate, ProgramFiles, true);
            }
        }
        return new ProgramLocation(fallback, Default, exists(fallback));
    }

    /// <summary>ProShow этой машины для пользователя, от чьего имени идёт поиск: ассоциация учитывает его выбор.</summary>
    [SupportedOSPlatform("windows")]
    public static ProgramLocation Resolve(string? configured) =>
        Choose(configured, AssociatedExecutable(".psh"), Candidates(), ProShowLauncher.DefaultProgramPath, File.Exists);

    [SupportedOSPlatform("windows")]
    private static List<string> Candidates() =>
        new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles }
            .Select(Environment.GetFolderPath)
            .Where(folder => folder.Length > 0)
            .Select(folder => Path.Combine(folder, "Photodex", "ProShow Producer", ImageName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private const uint AssocfNoTruncate = 0x20;
    private const uint AssocfInitIgnoreUnknown = 0x400;
    private const uint AssocstrExecutable = 2;

    /// <summary>Исполняемый файл команды «открыть» для расширения; ассоциации нет — <c>null</c>.</summary>
    [SupportedOSPlatform("windows")]
    private static string? AssociatedExecutable(string extension)
    {
        var buffer = new char[1024];
        var length = (uint)buffer.Length;
        var result = AssocQueryStringW(AssocfInitIgnoreUnknown | AssocfNoTruncate, AssocstrExecutable, extension, "open", buffer, ref length);
        // Длина — со знаком конца строки.
        return result == 0 && length > 1 ? new string(buffer, 0, (int)length - 1) : null;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int AssocQueryStringW(uint flags, uint str, string assoc, string? extra, [Out] char[] output, ref uint length);
}
