using PsDoctor.Core.Observation;

namespace PsDoctor.Infrastructure.Observation;

/// <summary>Снимок служебных файлов: обход мест, размер и время записи каждого файла. Файлы не открываются.</summary>
public static class ServiceFileScanner
{
    public static ServiceFilesSnapshot Take(IReadOnlyList<ServiceFilePlace> places)
    {
        ArgumentNullException.ThrowIfNull(places);
        // Пути Windows регистр не различают: один файл, записанный разным регистром из двух мест, — один файл.
        var files = new Dictionary<string, ServiceFileState>(StringComparer.OrdinalIgnoreCase);
        var states = new List<ServiceFilePlaceState>(places.Count);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            // Скрытые и системные тоже: в опыте 03 обход шёл с -Force.
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
        };
        foreach (var place in places)
        {
            var directory = new DirectoryInfo(place.Path);
            if (!directory.Exists)
            {
                states.Add(new ServiceFilePlaceState(place.Path, place.Recursive, place.Pattern, false, 0, null));
                continue;
            }
            options.RecurseSubdirectories = place.Recursive;
            var count = 0;
            string? error = null;
            try
            {
                foreach (var file in directory.EnumerateFiles(place.Pattern, options))
                {
                    // Сведения взяты из самого перечисления: файл, пропавший между шагами обхода, не роняет снимок.
                    files[file.FullName] = new ServiceFileState(file.Length, new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero));
                    count++;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                error = exception.GetType().Name;
            }
            states.Add(new ServiceFilePlaceState(place.Path, place.Recursive, place.Pattern, true, count, error));
        }
        return new ServiceFilesSnapshot(states, files);
    }
}
