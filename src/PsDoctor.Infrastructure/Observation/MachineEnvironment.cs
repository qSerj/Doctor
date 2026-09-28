using System.Diagnostics;
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

    public static EnvironmentFacts Read(string programPath, int? processId)
    {
        var (build, release) = WindowsVersion();
        var (total, free) = Memory();
        return new EnvironmentFacts(
            build,
            release,
            programPath,
            File.Exists(programPath),
            ProgramVersion(programPath),
            processId is { } pid ? Elevated(pid) : null,
            EnableLua(),
            Antivirus(),
            KLite(),
            total,
            free,
            Disk(Environment.SystemDirectory),
            Disk(Path.GetTempPath()));
    }

    private static (string? Build, string? Release) WindowsVersion()
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

    private static int? EnableLua()
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

    private static string? ProgramVersion(string path)
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

    /// <summary>Токен процесса повышен; не открылся — <c>null</c>.</summary>
    private static bool? Elevated(int processId)
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
    private static IReadOnlyList<InstalledProduct> KLite()
    {
        var found = new List<InstalledProduct>();
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
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
                    foreach (var name in uninstall.GetSubKeyNames())
                    {
                        using var product = uninstall.OpenSubKey(name);
                        if (product?.GetValue("DisplayName") is string display
                            && display.Contains("K-Lite", StringComparison.OrdinalIgnoreCase))
                        {
                            found.Add(new InstalledProduct(display, product.GetValue("DisplayVersion") as string));
                        }
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                }
            }
        }
        return found.Distinct().ToList();
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
