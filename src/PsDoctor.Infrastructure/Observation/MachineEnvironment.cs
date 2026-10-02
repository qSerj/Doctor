using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using PsDoctor.Core.Observation;
using static PsDoctor.Infrastructure.Observation.Win32Job;

namespace PsDoctor.Infrastructure.Observation;

/// <summary>
/// Окружение машины для факта <c>environment</c>. Каждый вопрос задаётся отдельно, и незнание — <c>null</c>: сеанс не
/// ждёт центр безопасности дольше <see cref="AntivirusTimeout"/> и не падает из-за закрытого раздела реестра.
/// Версию Windows, сборку ProShow и список программ берёт и слепок окружения (<see cref="EnvironmentSnapshotReader"/>),
/// версию файла оба берут у <see cref="FileDescription"/>: один код на оба, ошибка в нём чинится в одном месте.
/// </summary>
[SupportedOSPlatform("windows")]
public static class MachineEnvironment
{
    /// <summary>Сколько ждать ответа центра безопасности Windows: чтение идёт под замком подключения.</summary>
    public static readonly TimeSpan AntivirusTimeout = TimeSpan.FromSeconds(5);

    private const uint TokenQuery = 0x0008;
    private const int TokenElevationClass = 20;

    private static readonly string[] UninstallKeys =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    ];

    /// <summary>
    /// Модули Photodex рядом с программой, чья версия — сборка ProShow. У самого <c>proshow.exe</c> версии файла и продукта —
    /// «1, 0, 0, 1», у записи в разделе удаления программ — только 9 и 0, а <c>burn.dll</c> и <c>pxplay.exe</c> той же
    /// установки несут «9,00,0,3797» (стенд 29.09.2026).
    /// </summary>
    private static readonly string[] BuildModules = ["burn.dll", "pxplay.exe"];

    public static EnvironmentFacts Read(string programPath, int? processId)
    {
        var (build, release) = WindowsVersion();
        var (total, free) = Memory();
        return new EnvironmentFacts(
            build,
            release,
            programPath,
            File.Exists(programPath),
            FileDescription.Version(programPath),
            processId is { } pid ? Elevated(pid) : null,
            EnableLua(),
            Antivirus(),
            KLite(),
            total,
            free,
            Disk(Environment.SystemDirectory),
            Disk(Path.GetTempPath()),
            ProgramBuild(programPath),
            Processor: Processor(),
            Bios: Bios());
    }

    /// <summary>Первый процессор по реестру: у всех ядер имя и микрокод одни.</summary>
    private static ProcessorInfo? Processor()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return key is null
                ? null
                : new ProcessorInfo(
                    (key.GetValue("ProcessorNameString") as string)?.Trim(),
                    key.GetValue("Identifier") as string,
                    ProcessorInfo.Revision(key.GetValue("Update Revision") as byte[]),
                    ProcessorInfo.Revision(key.GetValue("Previous Update Revision") as byte[]));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static BiosInfo? Bios()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            if (key is null)
            {
                return null;
            }
            var board = string.Join(" ", new[] { "BaseBoardManufacturer", "BaseBoardProduct" }
                .Select(name => (key.GetValue(name) as string)?.Trim())
                .Where(value => !string.IsNullOrEmpty(value)));
            return new BiosInfo(key.GetValue("BIOSVendor") as string, key.GetValue("BIOSVersion") as string,
                key.GetValue("BIOSReleaseDate") as string, board.Length > 0 ? board : null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    internal static (string? Build, string? Release) WindowsVersion()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key is null)
            {
                return (null, null);
            }
            var build = $"{key.GetValue("CurrentMajorVersionNumber")}.{key.GetValue("CurrentMinorVersionNumber")}."
                + $"{key.GetValue("CurrentBuildNumber")}.{key.GetValue("UBR")}";
            return (build, key.GetValue("DisplayVersion") as string ?? key.GetValue("ReleaseId") as string);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return (null, null);
        }
    }

    internal static int? EnableLua()
    {
        try
        {
            return Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA", null) as int?;
        }
        catch (Exception e) when (e is IOException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>Версия файла первого из <see cref="BuildModules"/>, что нашёлся в каталоге программы.</summary>
    internal static string? ProgramBuild(string programPath)
    {
        var directory = Path.GetDirectoryName(programPath);
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }
        return BuildModules
            .Select(module => FileDescription.Version(Path.Combine(directory, module)))
            .FirstOrDefault(version => !string.IsNullOrEmpty(version));
    }

    /// <summary>Токен процесса повышен; не открылся — <c>null</c>.</summary>
    internal static bool? Elevated(int processId)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            if (!OpenProcessToken(process, TokenQuery, out var token))
            {
                return null;
            }
            try
            {
                return GetTokenInformation(token, TokenElevationClass, out var elevation, sizeof(uint), out _) ? elevation != 0 : null;
            }
            finally
            {
                CloseHandle(token);
            }
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static IReadOnlyList<AntivirusProduct>? Antivirus()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\SecurityCenter2", "SELECT displayName, productState FROM AntiVirusProduct");
            searcher.Options.Timeout = AntivirusTimeout;
            var products = new List<AntivirusProduct>();
            using var results = searcher.Get();
            foreach (var item in results)
            {
                using (item)
                {
                    products.Add(new AntivirusProduct(item["displayName"] as string ?? "", item["productState"] is uint state ? (int)state : null));
                }
            }
            return products;
        }
        catch (Exception e) when (e is ManagementException or COMException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>K-Lite по разделу удаления программ: машины и пользователя, 64- и 32-битному.</summary>
    private static IReadOnlyList<InstalledProduct> KLite() =>
        InstalledPrograms()
            .Where(program => program.Name.Contains("K-Lite", StringComparison.OrdinalIgnoreCase))
            .Select(program => new InstalledProduct(program.Name, program.Version))
            .Distinct()
            .ToList();

    /// <summary>Программа из раздела удаления.</summary>
    /// <param name="Hive">Чей раздел: <c>HKLM</c> или <c>HKCU</c>.</param>
    /// <param name="Key">Имя подраздела удаления.</param>
    /// <param name="View">Вид: 32 — раздел <c>WOW6432Node</c>, иначе 64.</param>
    internal sealed record InstalledProgram(string Hive, string Key, int View, string Name, string? Version, string? Publisher);

    /// <summary>
    /// Программы из раздела удаления машины и пользователя, 64- и 32-битного; без имени — не программа, а служебная
    /// запись установщика. Закрытый раздел пропускается.
    /// </summary>
    internal static IReadOnlyList<InstalledProgram> InstalledPrograms()
    {
        var found = new List<InstalledProgram>();
        foreach (var (hive, root) in new[] { ("HKLM", Registry.LocalMachine), ("HKCU", Registry.CurrentUser) })
        {
            foreach (var path in UninstallKeys)
            {
                try
                {
                    using var uninstall = root.OpenSubKey(path);
                    if (uninstall is null)
                    {
                        continue;
                    }
                    // На 32-битной Windows раздела WOW6432Node нет, а единственный вид — 32-битный.
                    var view = !Environment.Is64BitOperatingSystem || path.Contains("WOW6432Node", StringComparison.OrdinalIgnoreCase) ? 32 : 64;
                    foreach (var name in uninstall.GetSubKeyNames())
                    {
                        using var product = uninstall.OpenSubKey(name);
                        if (product?.GetValue("DisplayName") is string display && display.Length > 0)
                        {
                            found.Add(new InstalledProgram(hive, name, view, display,
                                product.GetValue("DisplayVersion") as string, product.GetValue("Publisher") as string));
                        }
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                }
            }
        }
        return found;
    }

    private static (long? Total, long? Free) Memory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? ((long)status.TotalPhys, (long)status.AvailPhys) : (null, null);
    }

    private static DiskSpace? Disk(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root))
            {
                return null;
            }
            var drive = new DriveInfo(root);
            return new DiskSpace(drive.Name, drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr token, int informationClass, out uint information, uint length, out uint returned);
}
