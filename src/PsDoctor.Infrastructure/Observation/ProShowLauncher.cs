using System.Diagnostics;
using static PsDoctor.Infrastructure.Observation.Win32Job;
using System.Runtime.Versioning;
using PsDoctor.Core.Observation;

namespace PsDoctor.Infrastructure.Observation;

/// <summary>Запуск ProShow с файлом шоу под заданием — так же, как в опытах: программа и путь в кавычках.</summary>
[SupportedOSPlatform("windows")]
public sealed class ProShowLauncher : IProgramLauncher, IProgramAttacher
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

    /// <summary>Сверяет полный путь и время создания: одно имя образа может принадлежать другой установке.</summary>
    public ProgramTarget FindRunning()
    {
        var found = new List<ProgramTarget>();
        var unverified = false;
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ProgramPath)))
        {
            using (process)
            {
                var handle = OpenProcess(ProcessQueryLimitedInformation, false, process.Id);
                if (handle == IntPtr.Zero) { unverified = true; continue; }
                try
                {
                    var buffer = new char[32768];
                    var length = (uint)buffer.Length;
                    if (!QueryFullProcessImageNameW(handle, 0, buffer, ref length)
                        || !GetProcessTimes(handle, out var created, out _, out _, out _))
                    {
                        unverified = true;
                        continue;
                    }
                    var image = new string(buffer, 0, (int)length);
                    if (Path.GetFullPath(image).Equals(Path.GetFullPath(ProgramPath), StringComparison.OrdinalIgnoreCase))
                        found.Add(new ProgramTarget(process.Id, DateTime.FromFileTimeUtc(created), image));
                }
                finally { CloseHandle(handle); }
            }
        }
        return found.Count switch
        {
            0 when unverified => throw new ProgramAttachException(ObserverErrors.AttachFailed),
            0 => throw new ProgramAttachException(ObserverErrors.ProgramNotRunning),
            1 when unverified => throw new ProgramAttachException(ObserverErrors.AmbiguousProgram),
            > 1 => throw new ProgramAttachException(ObserverErrors.AmbiguousProgram),
            _ => found[0],
        };
    }

    public IProgramRun Attach(ProgramTarget target, IFactRecorder facts, IProgramEvents events)
    {
        var current = FindRunning();
        if (current.ProcessId != target.ProcessId || current.StartedUtc != target.StartedUtc)
            throw new ProgramAttachException(ObserverErrors.AttachFailed);
        facts.Record(ProgramFactKinds.ProgramAttached, target, target.ProcessId);
        return AttachedRun.Start(target, facts, events);
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
            sampleInterval,
            hygiene: new SessionHygiene(ServicePlaces(ProgramPath, showPath), ImageNames, facts));
    }

    /// <summary>Образы куста ProShow: программа, видеомодуль, ffmpeg и ffprobe под чужими именами.</summary>
    public static readonly IReadOnlyList<string> ImageNames = ["proshow", "fvideo", "device-enc"];

    /// <summary>
    /// Места служебных файлов — те же, что в опыте 03, и ещё два. Каталог программы берётся на уровень выше exe
    /// (<c>Photodex</c>), и его зеркало в <c>VirtualStore</c>: куда пишет неповышенная программа, решает UAC.
    /// Временный каталог — только файлы ffmpeg программы (<c>py*</c>, <c>px*</c>, <c>dpx*</c>, опыт 07): прочего там
    /// много и оно чужое. Каталог файла шоу — без вложенных, ради <c>.pxc</c>, <c>.bak</c> и автосохранения рядом.
    /// </summary>
    public static IReadOnlyList<ServiceFilePlace> ServicePlaces(string programPath, string showPath)
    {
        var vendor = Path.GetDirectoryName(Path.GetDirectoryName(programPath)) ?? Path.GetDirectoryName(programPath)!;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var temp = Path.GetTempPath();
        var places = new List<ServiceFilePlace>
        {
            new(vendor, true),
            new(Path.Combine(local, "VirtualStore", vendor[Path.GetPathRoot(vendor)!.Length..]), true),
            new(Path.Combine(roaming, "Photodex"), true),
            new(Path.Combine(local, "Photodex"), true),
            new(Path.Combine(common, "Photodex"), true),
            new(Path.Combine(documents, "ProShow Producer"), true),
            new(Path.Combine(documents, "ProShow"), true),
            new(temp, false, "py*"),
            new(temp, false, "px*"),
            new(temp, false, "dpx*"),
        };
        if (Path.GetDirectoryName(showPath) is { Length: > 0 } show)
        {
            places.Add(new ServiceFilePlace(show, false));
        }
        return places;
    }
}
