using System.Diagnostics;
using System.Runtime.Versioning;
using PsDoctor.Core.Observation;

namespace PsDoctor.Infrastructure.Observation;

/// <summary>Запуск ProShow с файлом шоу под заданием — так же, как в опытах: программа и путь в кавычках.</summary>
[SupportedOSPlatform("windows")]
public sealed class ProShowLauncher : IProgramLauncher
{
    public const string DefaultProgramPath = @"C:\Program Files (x86)\Photodex\ProShow Producer\proshow.exe";

    private readonly TimeSpan? sampleInterval;

    public ProShowLauncher(string programPath, TimeSpan? sampleInterval = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(programPath);
        ProgramPath = programPath;
        this.sampleInterval = sampleInterval;
    }

    public string ProgramPath { get; }

    /// <summary>
    /// Второй экземпляр путает кэши и число процессов, поэтому ищется любой процесс с тем же именем образа,
    /// а не только запущенный наблюдателем.
    /// </summary>
    public bool IsProgramRunning()
    {
        var found = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ProgramPath));
        foreach (var process in found)
        {
            process.Dispose();
        }
        return found.Length > 0;
    }

    public IProgramRun Launch(string showPath, IFactRecorder facts, IProgramEvents events)
    {
        ArgumentException.ThrowIfNullOrEmpty(showPath);
        // Текущий каталог — каталог файла шоу, как при открытии проекта двойным щелчком.
        var directory = Path.GetDirectoryName(showPath);
        return ProgramRun.Start(
            ProgramPath,
            $"\"{ProgramPath}\" \"{showPath}\"",
            Directory.Exists(directory) ? directory : null,
            facts,
            events,
            sampleInterval);
    }
}
