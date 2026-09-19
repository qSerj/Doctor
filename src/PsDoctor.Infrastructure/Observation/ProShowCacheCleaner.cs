using System.Diagnostics;
using System.Runtime.Versioning;

namespace PsDoctor.Infrastructure.Observation;

public enum ProShowCacheKind
{
    Program,
    Project,
}

public sealed record ProShowCacheFile(string Path, ProShowCacheKind Kind);

public sealed record SavedProShowCache(string OriginalPath, string SavedPath);

public sealed record ProShowCacheFailure(string Path, string Reason);

public sealed record ProShowCacheCleanupResult(
    bool ProgramRunning,
    IReadOnlyList<SavedProShowCache> Saved,
    IReadOnlyList<ProShowCacheFailure> Failed);

/// <summary>Переименовывает только два известных кэша, оставляя исходные байты рядом для восстановления.</summary>
[SupportedOSPlatform("windows")]
public sealed class ProShowCacheCleaner
{
    private readonly string programPath;
    private readonly string localAppData;
    private readonly Func<bool> isProgramRunning;

    public ProShowCacheCleaner(string? programPath = null, string? localAppData = null, Func<bool>? isProgramRunning = null)
    {
        this.programPath = Path.GetFullPath(programPath ?? ProShowLauncher.DefaultProgramPath);
        this.localAppData = Path.GetFullPath(localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        this.isProgramRunning = isProgramRunning ?? AnyProShowProcessRunning;
    }

    public bool IsProgramRunning() => isProgramRunning();

    public IReadOnlyList<ProShowCacheFile> Find(string? showPath)
    {
        string? projectCache = null;
        if (showPath is not null)
        {
            var fullShowPath = Path.GetFullPath(showPath);
            if (!string.Equals(Path.GetExtension(fullShowPath), ".psh", StringComparison.OrdinalIgnoreCase)
                || !File.Exists(fullShowPath))
            {
                throw new FileNotFoundException("Show file not found", fullShowPath);
            }
            projectCache = Path.ChangeExtension(fullShowPath, ".pxc");
        }

        var programDirectory = Path.GetDirectoryName(programPath)!;
        var relativeDirectory = Path.GetRelativePath(Path.GetPathRoot(programDirectory)!, programDirectory);
        var virtualStore = Path.Combine(localAppData, "VirtualStore", relativeDirectory, "proshow.phd");
        var installed = Path.Combine(programDirectory, "proshow.phd");
        var candidates = new List<ProShowCacheFile>();
        if (projectCache is not null && File.Exists(projectCache))
        {
            candidates.Add(new ProShowCacheFile(projectCache, ProShowCacheKind.Project));
        }
        foreach (var path in new[] { virtualStore, installed }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(path))
            {
                candidates.Add(new ProShowCacheFile(path, ProShowCacheKind.Program));
            }
        }
        return candidates;
    }

    public ProShowCacheCleanupResult Clean(string? showPath)
    {
        if (IsProgramRunning())
        {
            return new ProShowCacheCleanupResult(true, [], []);
        }

        var saved = new List<SavedProShowCache>();
        var failed = new List<ProShowCacheFailure>();
        foreach (var candidate in Find(showPath))
        {
            // Между предварительным просмотром и переименованием ProShow могли запустить вручную.
            if (IsProgramRunning())
            {
                return new ProShowCacheCleanupResult(true, saved, failed);
            }
            if (!File.Exists(candidate.Path))
            {
                continue;
            }
            var backup = candidate.Path + ".psdoctor-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")
                + "-" + Guid.NewGuid().ToString("N") + ".bak";
            try
            {
                File.Move(candidate.Path, backup);
                saved.Add(new SavedProShowCache(candidate.Path, backup));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                failed.Add(new ProShowCacheFailure(candidate.Path, error.GetType().Name));
            }
        }
        return new ProShowCacheCleanupResult(false, saved, failed);
    }

    private static bool AnyProShowProcessRunning()
    {
        foreach (var name in ProShowLauncher.ImageNames)
        {
            var processes = Process.GetProcessesByName(name);
            foreach (var process in processes)
            {
                process.Dispose();
            }
            if (processes.Length > 0)
            {
                return true;
            }
        }
        return false;
    }
}
