using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Win32;
using PsDoctor.Core.Observation;

namespace PsDoctor.Infrastructure.Observation;

/// <summary>Найденный процесс программы: повышен ли его токен; <c>null</c> — токен не открылся или процессов несколько.</summary>
public sealed record ProgramElevation(bool? Elevated);

/// <summary>
/// Слепок окружения (Э6.3): декодеры и то, откуда они, в обоих видах реестра. ProShow 32-битный и видит только
/// 32-битный вид, а наблюдатель 64-битный: без явного <see cref="RegistryView.Registry32"/> он прочёл бы не то, что
/// видит программа. 64-битный вид снимается тоже — он нужен любой 64-битной программе.
/// Раздел, который не прочёлся, не роняет снятие: в слепке остаётся запись <c>!error</c> с именем исключения.
/// </summary>
[SupportedOSPlatform("windows")]
public static class EnvironmentSnapshotReader
{
    /// <summary>
    /// Список категорий фильтров DirectShow (<c>CLSID_ActiveMovieCategories</c>). Не путать с <c>{083863F1-…}</c>: это
    /// одна из категорий, «DirectShow Filters», и под её <c>Instance</c> лежат фильтры, а не категории (стенд, 30.09.2026).
    /// </summary>
    private const string FilterCategories = "{DA4E3DA0-D07D-11d0-BD50-00A0C911CE86}";

    /// <summary>Категория декодеров картинок WIC.</summary>
    private const string WicDecoders = "{7ED96837-96F0-4812-B211-F13C24117ED3}";

    private const string ErrorKey = "!error";

    private const string StorePackages =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    /// <param name="running">Процесс программы, если он найден: по нему видно, какой <c>proshow.cfg</c> она читает.</param>
    public static EnvironmentSnapshot Read(string programPath, ProgramElevation? running = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(programPath);
        var watch = Stopwatch.StartNew();
        var taken = DateTimeOffset.UtcNow;
        var files = new Dictionary<string, EnvironmentFile>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<EnvironmentEntry>();
        Section(entries, EnvironmentSections.System, null, list => list.Add(SystemEntry()));
        Section(entries, EnvironmentSections.ProShow, null, list => ProShowEntries(list, programPath, running, files));
        // На 32-битной Windows вид один: 64-битный повторил бы 32-битный под другим именем.
        var views = Environment.Is64BitOperatingSystem ? new[] { RegistryView.Registry32, RegistryView.Registry64 } : new[] { RegistryView.Registry32 };
        foreach (var view in views)
        {
            var bits = view == RegistryView.Registry32 ? 32 : 64;
            Section(entries, EnvironmentSections.VideoForWindows, bits, list => VideoForWindows(list, view, bits, files));
            Section(entries, EnvironmentSections.DirectShow, bits, list => DirectShow(list, view, bits, files));
            Section(entries, EnvironmentSections.DirectShowPreferred, bits, list => Preferred(list, view, bits, files));
            Section(entries, EnvironmentSections.MediaFoundation, bits, list => MediaFoundation(list, view, bits, files));
            Section(entries, EnvironmentSections.ImagingComponent, bits, list => Wic(list, view, bits, files));
        }
        Section(entries, EnvironmentSections.StoreCodecs, null, StoreCodecs);
        Section(entries, EnvironmentSections.CodecProducts, null, CodecProductEntries);
        return EnvironmentSnapshot.Create(taken, watch.Elapsed.TotalSeconds, entries);
    }

    /// <summary>Раздел читается целиком или остаётся записью об ошибке — прочитанное до сбоя не выдаётся за целое.</summary>
    private static void Section(List<EnvironmentEntry> entries, string section, int? view, Action<List<EnvironmentEntry>> read)
    {
        var found = new List<EnvironmentEntry>();
        try
        {
            read(found);
            entries.AddRange(found);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            entries.Add(new EnvironmentEntry(section, view, ErrorKey, Values(("error", e.GetType().Name)), null));
        }
    }

    private static EnvironmentEntry SystemEntry()
    {
        var (build, release) = MachineEnvironment.WindowsVersion();
        using var version = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        using var codePage = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Nls\CodePage");
        return new EnvironmentEntry(EnvironmentSections.System, null, "windows", Values(
            ("build", build),
            ("release", release),
            // N-редакция без Media Feature Pack не декодирует WMV и не даёт Media Foundation.
            ("edition", version?.GetValue("EditionID") as string),
            ("product", version?.GetValue("ProductName") as string),
            ("installationType", version?.GetValue("InstallationType") as string),
            ("is64Bit", Environment.Is64BitOperatingSystem ? "true" : "false"),
            ("acp", codePage?.GetValue("ACP") as string),
            ("oemcp", codePage?.GetValue("OEMCP") as string),
            ("uiCulture", CultureInfo.InstalledUICulture.Name),
            // Культура пользователя наблюдателя — того же, под кем работает ProShow.
            ("culture", CultureInfo.CurrentCulture.Name)), null);
    }

    /// <summary>
    /// Программа, её сборка и настройки, от которых зависит путь декодирования: галка «Avoid using DirectShow» выбирает
    /// между встроенным FFmpeg и QuickTime. Файлов настроек может быть два — рядом с программой и в VirtualStore; какой из
    /// них программа читает, решает виртуализация (<see cref="ProShowConfig.Virtualized"/>). Каждый найденный файл —
    /// отдельная запись со своим значением и временем записи: устаревшая копия видна, даже когда действует другая.
    /// </summary>
    private static void ProShowEntries(List<EnvironmentEntry> entries, string programPath, ProgramElevation? running,
        Dictionary<string, EnvironmentFile> files)
    {
        var (programFile, virtualStoreFile) = ProShowConfigFiles(programPath);
        var runAsAdmin = RunAsAdmin(programPath);
        var shortcuts = Shortcuts(programPath);
        var shortcutRunAsAdmin = shortcuts.Any(shortcut => shortcut.RunAsAdmin);
        var virtualized = ProShowConfig.Virtualized(MachineEnvironment.EnableLua(), running is not null, running?.Elevated,
            runAsAdmin || shortcutRunAsAdmin);
        var config = ProShowConfig.Effective(programFile, virtualStoreFile, virtualized);
        entries.Add(new(EnvironmentSections.ProShow, null, "program",
            Values(("build", MachineEnvironment.ProgramBuild(programPath)), ("config", config),
                (ProShowConfig.DShowUseFfmpeg, config is null ? null : Setting(config)),
                ("runAsAdmin", runAsAdmin ? "true" : "false"),
                ("shortcutRunAsAdmin", shortcutRunAsAdmin ? "true" : "false"),
                ("virtualized", virtualized switch { true => "true", false => "false", null => null })),
            Describe(programPath, files)));
        foreach (var shortcut in shortcuts)
        {
            entries.Add(new(EnvironmentSections.ProShow, null, $"shortcut/{shortcut.Key}",
                Values(("runAsAdmin", shortcut.RunAsAdmin ? "true" : "false")), null));
        }
        foreach (var (key, path) in new[] { ("config/program", programFile), ("config/virtual-store", virtualStoreFile) })
        {
            if (path is not null)
            {
                entries.Add(new(EnvironmentSections.ProShow, null, key, Values((ProShowConfig.DShowUseFfmpeg, Setting(path))),
                    Describe(path, files)));
            }
        }
    }

    private static string? Setting(string config)
    {
        try
        {
            return ProShowConfig.Value(File.ReadAllBytes(config), ProShowConfig.DShowUseFfmpeg);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Программа держит файл или его нет — настройка неизвестна, снятие не падает.
            return null;
        }
    }

    /// <summary>
    /// <c>proshow.cfg</c> рядом с программой и его копия в <c>%LOCALAPPDATA%\VirtualStore</c> — туда Windows уводит запись
    /// виртуализованной программы без прав на Program Files. VirtualStore — пользователя наблюдателя: он тот же, под кем
    /// работает ProShow. Нет файла — <c>null</c>.
    /// </summary>
    internal static (string? Program, string? VirtualStore) ProShowConfigFiles(string programPath)
    {
        var directory = Path.GetDirectoryName(programPath);
        if (string.IsNullOrEmpty(directory))
        {
            return (null, null);
        }
        var root = Path.GetPathRoot(directory);
        var program = Path.Combine(directory, ProShowConfig.FileName);
        var virtualStore = string.IsNullOrEmpty(root)
            ? null
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VirtualStore",
                directory[root.Length..], ProShowConfig.FileName);
        return (File.Exists(program) ? program : null, virtualStore is not null && File.Exists(virtualStore) ? virtualStore : null);
    }

    /// <summary>
    /// Метка «от имени администратора» у программы в свойствах совместимости: у пользователя или у всех, в обоих видах
    /// реестра. Имя значения — полный путь программы.
    /// </summary>
    private static bool RunAsAdmin(string programPath)
    {
        const string layers = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
        var program = Path.GetFullPath(programPath);
        foreach (var (hive, view) in new[]
                 {
                     (RegistryHive.CurrentUser, RegistryView.Default),
                     (RegistryHive.LocalMachine, RegistryView.Registry64),
                     (RegistryHive.LocalMachine, RegistryView.Registry32),
                 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey(layers);
                if (key?.GetValueNames().FirstOrDefault(name => string.Equals(name, program, StringComparison.OrdinalIgnoreCase)) is { } name
                    && ProShowConfig.HasRunAsAdmin(key.GetValue(name) as string))
                {
                    return true;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Раздел не прочёлся — метки там для нас нет.
            }
        }
        return false;
    }

    /// <summary>
    /// Где лежат ярлыки, которыми запускают программу: рабочий стол, «Пуск» и закреплённые на панели задач — у
    /// пользователя и у всех. Ключ записи — имя места и путь внутри него: имя пользователя в ключ не попадает, и ярлыки
    /// двух машин сопоставляются.
    /// </summary>
    private static IEnumerable<(string Name, string Directory)> ShortcutPlaces() =>
    [
        ("desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
        ("common-desktop", Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)),
        ("start-menu", Environment.GetFolderPath(Environment.SpecialFolder.StartMenu)),
        ("common-start-menu", Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)),
        ("pinned", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Internet Explorer\Quick Launch\User Pinned")),
    ];

    /// <summary>Самый большой ярлык, который читается: обычный — единицы килобайт.</summary>
    private const long ShortcutLimit = 1 << 20;

    /// <summary>
    /// Ярлыки на программу и галка «от имени администратора» у каждого. Ярлык, который не прочёлся, пропускается: он
    /// ничего не говорит о том, как запускают программу.
    /// </summary>
    private static List<(string Key, bool RunAsAdmin)> Shortcuts(string programPath)
    {
        var program = Path.GetFullPath(programPath);
        var found = new List<(string Key, bool RunAsAdmin)>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var (name, directory) in ShortcutPlaces())
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                continue;
            }
            foreach (var path in Directory.EnumerateFiles(directory, "*.lnk", options))
            {
                try
                {
                    if (new FileInfo(path).Length > ShortcutLimit
                        || ShellLink.Parse(File.ReadAllBytes(path)) is not { Target: { } target } link
                        || !string.Equals(Environment.ExpandEnvironmentVariables(target), program, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    found.Add(($"{name}/{Path.GetRelativePath(directory, path).Replace('\\', '/')}", link.RunAsAdmin));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Ярлык держат или закрыли — пропускаем.
                }
            }
        }
        return found;
    }

    /// <summary>Кодеки VfW и ACM: значения <c>vidc.*</c> и <c>msacm.*</c> раздела <c>Drivers32</c>.</summary>
    private static void VideoForWindows(List<EnvironmentEntry> entries, RegistryView view, int bits, Dictionary<string, EnvironmentFile> files)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        using var drivers = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Drivers32");
        if (drivers is null)
        {
            return;
        }
        foreach (var name in drivers.GetValueNames())
        {
            if (!name.StartsWith("vidc.", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("msacm.", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var driver = drivers.GetValue(name) as string;
            entries.Add(new EnvironmentEntry(EnvironmentSections.VideoForWindows, bits, name.ToLowerInvariant(),
                Values(("driver", driver)), Module(driver, bits, files)));
        }
    }

    /// <summary>
    /// Фильтры DirectShow всех категорий из реестра: имя, CLSID, merit из <c>FilterData</c> и библиотека. Устройства,
    /// которые Windows перечисляет на лету, в реестре не лежат и в слепок не попадают.
    /// </summary>
    private static void DirectShow(List<EnvironmentEntry> entries, RegistryView view, int bits, Dictionary<string, EnvironmentFile> files)
    {
        using var classes = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view);
        using var categories = classes.OpenSubKey($@"CLSID\{FilterCategories}\Instance");
        if (categories is null)
        {
            return;
        }
        foreach (var category in categories.GetSubKeyNames())
        {
            string? categoryName;
            using (var described = categories.OpenSubKey(category))
            {
                categoryName = described?.GetValue("FriendlyName") as string;
            }
            using var instances = classes.OpenSubKey($@"CLSID\{category}\Instance");
            if (instances is null)
            {
                continue;
            }
            foreach (var instance in instances.GetSubKeyNames())
            {
                using var filter = instances.OpenSubKey(instance);
                if (filter is null)
                {
                    continue;
                }
                var clsid = filter.GetValue("CLSID") as string ?? instance;
                var merit = DirectShowFilterData.Merit(filter.GetValue("FilterData") as byte[]);
                var inproc = InprocServer(classes, clsid);
                entries.Add(new EnvironmentEntry(EnvironmentSections.DirectShow, bits, $"{category.ToUpperInvariant()}/{instance.ToUpperInvariant()}",
                    Values(
                        ("category", categoryName),
                        ("name", filter.GetValue("FriendlyName") as string),
                        ("clsid", clsid.ToUpperInvariant()),
                        ("merit", merit is { } m ? $"0x{m:X8}" : null),
                        ("inproc", inproc)),
                    Module(inproc, bits, files)));
            }
        }
    }

    /// <summary>Переназначения <c>DirectShow\Preferred</c>: какой фильтр Windows берёт для подтипа медиа мимо merit.</summary>
    private static void Preferred(List<EnvironmentEntry> entries, RegistryView view, int bits, Dictionary<string, EnvironmentFile> files)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        using var preferred = root.OpenSubKey(@"SOFTWARE\Microsoft\DirectShow\Preferred");
        if (preferred is null)
        {
            return;
        }
        using var classes = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view);
        foreach (var subtype in preferred.GetValueNames())
        {
            var filter = preferred.GetValue(subtype) as string;
            var inproc = filter is null ? null : InprocServer(classes, filter);
            entries.Add(new EnvironmentEntry(EnvironmentSections.DirectShowPreferred, bits, subtype.ToUpperInvariant(),
                Values(("filter", filter?.ToUpperInvariant()), ("inproc", inproc)), Module(inproc, bits, files)));
        }
    }

    /// <summary>Преобразования Media Foundation по категориям: декодеры, кодеры, эффекты.</summary>
    private static void MediaFoundation(List<EnvironmentEntry> entries, RegistryView view, int bits, Dictionary<string, EnvironmentFile> files)
    {
        using var classes = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view);
        using var categories = classes.OpenSubKey(@"MediaFoundation\Transforms\Categories");
        if (categories is null)
        {
            return;
        }
        foreach (var category in categories.GetSubKeyNames())
        {
            using var members = categories.OpenSubKey(category);
            if (members is null)
            {
                continue;
            }
            foreach (var clsid in members.GetSubKeyNames())
            {
                string? name;
                using (var transform = classes.OpenSubKey($@"MediaFoundation\Transforms\{clsid}"))
                {
                    name = transform?.GetValue(null) as string;
                }
                var inproc = InprocServer(classes, clsid);
                entries.Add(new EnvironmentEntry(EnvironmentSections.MediaFoundation, bits, $"{category.ToUpperInvariant()}/{clsid.ToUpperInvariant()}",
                    Values(("name", name), ("inproc", inproc)), Module(inproc, bits, files)));
            }
        }
    }

    /// <summary>Декодеры картинок WIC: имя, расширения файлов, версия из регистрации и библиотека.</summary>
    private static void Wic(List<EnvironmentEntry> entries, RegistryView view, int bits, Dictionary<string, EnvironmentFile> files)
    {
        using var classes = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view);
        using var instances = classes.OpenSubKey($@"CLSID\{WicDecoders}\Instance");
        if (instances is null)
        {
            return;
        }
        foreach (var clsid in instances.GetSubKeyNames())
        {
            using var decoder = classes.OpenSubKey($@"CLSID\{clsid}");
            var inproc = InprocServer(classes, clsid);
            entries.Add(new EnvironmentEntry(EnvironmentSections.ImagingComponent, bits, clsid.ToUpperInvariant(),
                Values(
                    ("name", decoder?.GetValue("FriendlyName") as string),
                    ("extensions", decoder?.GetValue("FileExtensions") as string),
                    ("version", decoder?.GetValue("Version") as string),
                    ("inproc", inproc)),
                Module(inproc, bits, files)));
        }
    }

    /// <summary>Расширения кодеков из Store по репозиторию пакетов пользователя — того же, под кем работает ProShow.</summary>
    private static void StoreCodecs(List<EnvironmentEntry> entries)
    {
        using var packages = Registry.CurrentUser.OpenSubKey(StorePackages);
        if (packages is null)
        {
            return;
        }
        foreach (var fullName in packages.GetSubKeyNames())
        {
            if (StorePackage.Parse(fullName) is { IsCodec: true } package)
            {
                entries.Add(new EnvironmentEntry(EnvironmentSections.StoreCodecs, null, package.FullName,
                    Values(("name", package.Name), ("version", package.Version), ("architecture", package.Architecture)), null));
            }
        }
    }

    /// <summary>Наборы кодеков и инструменты — тот же обход списка программ, что у факта <c>environment</c>.</summary>
    private static void CodecProductEntries(List<EnvironmentEntry> entries)
    {
        foreach (var program in MachineEnvironment.InstalledPrograms().Where(program => CodecProducts.Matches(program.Name)))
        {
            var key = $"{program.Hive}/{program.View}/{program.Key}";
            if (entries.Any(entry => entry.Key == key))
            {
                continue;
            }
            entries.Add(new EnvironmentEntry(EnvironmentSections.CodecProducts, null, key,
                Values(("name", program.Name), ("version", program.Version), ("publisher", program.Publisher)), null));
        }
    }

    /// <summary>Строка <c>InprocServer32</c> класса как записана; нет — <c>null</c>.</summary>
    private static string? InprocServer(RegistryKey classes, string clsid)
    {
        using var server = classes.OpenSubKey($@"CLSID\{clsid}\InprocServer32");
        return server?.GetValue(null) as string;
    }

    /// <summary>Библиотека, которую загрузит процесс этой разрядности, описанная один раз за снятие.</summary>
    private static EnvironmentFile? Module(string? registered, int bits, Dictionary<string, EnvironmentFile> files) =>
        ResolveModule(registered, bits) is { } path ? Describe(path, files) : null;

    /// <summary>
    /// Путь библиотеки глазами процесса нужной разрядности. Переменные раскрываются; имя без каталога ищется, как правило,
    /// в системном каталоге. 32-битный процесс, открывая <c>System32</c>, попадает в <c>SysWOW64</c> — наблюдатель
    /// 64-битный и сам туда не попадёт, поэтому путь переводится явно.
    /// </summary>
    internal static string? ResolveModule(string? registered, int bits)
    {
        if (string.IsNullOrWhiteSpace(registered))
        {
            return null;
        }
        var path = Environment.ExpandEnvironmentVariables(registered.Trim().Trim('"'));
        var redirected = bits == 32 && Environment.Is64BitOperatingSystem;
        var system = redirected ? Environment.GetFolderPath(Environment.SpecialFolder.SystemX86) : Environment.SystemDirectory;
        try
        {
            if (!Path.IsPathRooted(path))
            {
                return Path.Combine(system, path);
            }
            var native = Environment.SystemDirectory.TrimEnd('\\') + '\\';
            return redirected && path.StartsWith(native, StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(system, path[native.Length..])
                : path;
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    /// <summary>Описание файла, один раз на путь за снятие: один модуль регистрируют многие фильтры.</summary>
    private static EnvironmentFile Describe(string path, Dictionary<string, EnvironmentFile> files)
    {
        if (!files.TryGetValue(path, out var described))
        {
            files[path] = described = FileDescription.Describe(path);
        }
        return described;
    }

    private static Dictionary<string, string?> Values(params (string Name, string? Value)[] values) =>
        values.ToDictionary(value => value.Name, value => value.Value, StringComparer.Ordinal);
}
