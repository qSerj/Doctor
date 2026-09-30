using System.Runtime.Versioning;
using PsDoctor.Core.Observation;
using PsDoctor.Infrastructure.Observation;
using Xunit;
using Xunit.Abstractions;

namespace PsDoctor.Infrastructure.Tests;

/// <summary>
/// Слепок окружения на настоящей машине (Э6.3, критерий 2): на любой Windows есть кодеки ACM и фильтры DirectShow
/// самой системы в 32-битном виде. Не на Windows молча не выполняется. Время снятия и число записей по разделам — в
/// выводе теста: это часть цены наблюдения.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EnvironmentSnapshotReaderTests(ITestOutputHelper вывод)
{
    [Fact]
    public void Слепок_машины_даёт_32_битные_DirectShow_и_VfW_и_описывает_каждый_файл()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var слепок = EnvironmentSnapshotReader.Read(ProShowLauncher.DefaultProgramPath);

        вывод.WriteLine($"слепок {слепок.Id}: {слепок.Entries.Count} записей за {слепок.Seconds:F2} с");
        foreach (var раздел in слепок.Entries.GroupBy(e => (e.Section, e.View)))
        {
            вывод.WriteLine($"  {раздел.Key.Section} {раздел.Key.View?.ToString() ?? "-"}: {раздел.Count()}");
        }
        var ошибки = слепок.Entries.Where(e => e.Key == "!error").Select(e => $"{e.Section}/{e.View}: {e.Values["error"]}").ToList();
        Assert.True(ошибки.Count == 0, "разделы не прочлись: " + string.Join("; ", ошибки));

        Assert.Contains(слепок.Entries, e => e.Section == EnvironmentSections.DirectShow && e.View == 32);
        Assert.Contains(слепок.Entries, e => e.Section == EnvironmentSections.VideoForWindows && e.View == 32);
        Assert.Single(слепок.Entries, e => e.Section == EnvironmentSections.System);

        // Файл есть — у него версия или хотя бы размер; нет — признак «файла нет». Молчаливой записи не бывает.
        var безОписания = слепок.Entries
            .Where(e => e.File is { Exists: true, Version: null, Size: null })
            .Select(e => $"{e.Section}/{e.View}/{e.Key}: {e.File!.Path}")
            .ToList();
        Assert.True(безОписания.Count == 0, "файлы без описания: " + string.Join("; ", безОписания));
        var безВерсии = слепок.Entries.Where(e => e.File is { Exists: true, Version: null }).Select(e => e.File!.Path).Distinct().ToList();
        вывод.WriteLine($"файлов без версии: {безВерсии.Count}");
        foreach (var путь in безВерсии)
        {
            вывод.WriteLine($"  {путь}");
        }
    }

    [Fact]
    public void Библиотеки_32_битного_вида_ищутся_там_же_где_их_найдёт_32_битный_процесс()
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitOperatingSystem)
        {
            return;
        }

        var слепок = EnvironmentSnapshotReader.Read(ProShowLauncher.DefaultProgramPath);

        var system32 = Environment.SystemDirectory.TrimEnd('\\') + '\\';
        var мимо = слепок.Entries
            .Where(e => e.View == 32 && e.File is { } f && f.Path.StartsWith(system32, StringComparison.OrdinalIgnoreCase))
            .Select(e => $"{e.Section}/{e.Key}: {e.File!.Path}")
            .ToList();
        Assert.True(мимо.Count == 0, "32-битные записи указывают в System32: " + string.Join("; ", мимо));
    }

    [Fact]
    public void Два_снятия_подряд_дают_один_идентификатор()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var первый = EnvironmentSnapshotReader.Read(ProShowLauncher.DefaultProgramPath);
        var второй = EnvironmentSnapshotReader.Read(ProShowLauncher.DefaultProgramPath);

        var разница = EnvironmentComparison.Compare(первый, второй);
        Assert.True(разница.IsEmpty,
            $"добавлено {разница.Added.Count}, убрано {разница.Removed.Count}, изменено: "
            + string.Join("; ", разница.Changed.Select(c => $"{c.Section}/{c.Key} {string.Join(",", c.Fields.Select(f => f.Name))}")));
        Assert.Equal(первый.Id, второй.Id);
    }
}
