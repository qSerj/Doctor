using System.Net;
using PsDoctor.Core.Observation;
using PsDoctor.Infrastructure.Installation;
using PsDoctor.Observer;
using Xunit;

namespace PsDoctor.Observer.Tests;

/// <summary>Хранение на подложенных журналах: эпизодов в живых сеансах ещё нет, а правило уже должно их держать.</summary>
public sealed class RetentionSweepTests : IDisposable
{
    private static readonly DateTime Сейчас = new(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _каталог = Directory.CreateTempSubdirectory("psdoctor-хранение-").FullName;

    public void Dispose() => Directory.Delete(_каталог, recursive: true);

    private string Журнал(DateTime открыт, string? origin = null, bool эпизод = false, int байтов = 0)
    {
        var id = SessionIds.New(открыт, _ => false);
        var кто = origin is null ? "null" : $"\"{origin}\"";
        var строки = new List<string>
        {
            $$$"""{"number":1,"elapsed":"00:00:00","session":"{{{id}}}","processId":null,"kind":"session-started","data":{"origin":{{{кто}}}}}""",
        };
        if (эпизод)
        {
            строки.Add($$$"""{"number":2,"elapsed":"00:00:01","session":"{{{id}}}","processId":null,"kind":"episode","data":{}}""");
        }
        строки.Add($$$"""{"number":{{{строки.Count + 1}}},"elapsed":"00:00:02","session":"{{{id}}}","processId":null,"kind":"session-finished","data":{"reason":"stopped","pad":"{{{new string('x', байтов)}}}"}}""");
        File.WriteAllText(Path.Combine(_каталог, id + SessionIds.JournalExtension), string.Join("\n", строки) + "\n");
        return id;
    }

    private ObservationService Служба(RetentionLimits? пределы) =>
        new(_каталог, new FakeLauncher(), new ObserverHealth("test", null), utcNow: () => Сейчас, retention: пределы);

    private bool Есть(string id) => File.Exists(Path.Combine(_каталог, id + SessionIds.JournalExtension));

    [Fact]
    public async Task Малые_пределы_удаляют_старые_обычные_сеансы_а_мастера_и_эпизода_оставляют()
    {
        var обычный = Журнал(Сейчас.AddDays(-3));
        var мастер = Журнал(Сейчас.AddDays(-3).AddSeconds(1), origin: SessionOrigins.Wizard);
        var эпизод = Журнал(Сейчас.AddDays(-3).AddSeconds(2), эпизод: true);
        var свежий = Журнал(Сейчас.AddHours(-1));
        Directory.CreateDirectory(Path.Combine(_каталог, "etw"));
        File.WriteAllText(EtwFiles.Raw(_каталог, обычный), "{}");
        File.WriteAllText(EtwFiles.Summary(_каталог, обычный), "{}");
        File.WriteAllText(EtwFiles.Raw(_каталог, мастер), "{}");

        await using var служба = Служба(new RetentionLimits(TimeSpan.FromDays(1), 1024 * 1024, TimeSpan.FromDays(30)));
        служба.Sweep();

        Assert.False(Есть(обычный));
        Assert.False(File.Exists(EtwFiles.Raw(_каталог, обычный)));
        Assert.False(File.Exists(EtwFiles.Summary(_каталог, обычный)));
        Assert.True(Есть(мастер));
        Assert.True(File.Exists(EtwFiles.Raw(_каталог, мастер)));
        Assert.True(Есть(эпизод));
        Assert.True(Есть(свежий));
    }

    [Fact]
    public async Task По_объёму_уходит_старейший_обычный()
    {
        var старый = Журнал(Сейчас.AddHours(-5), байтов: 3000);
        var мастер = Журнал(Сейчас.AddHours(-6), origin: SessionOrigins.Wizard, байтов: 3000);
        var новый = Журнал(Сейчас.AddHours(-1), байтов: 3000);

        await using var служба = Служба(new RetentionLimits(TimeSpan.FromDays(30), 7000, TimeSpan.FromDays(180)));
        служба.Sweep();

        Assert.False(Есть(старый));
        Assert.True(Есть(мастер));
        Assert.True(Есть(новый));
    }

    [Fact]
    public async Task Без_пределов_ничего_не_удаляется()
    {
        var старый = Журнал(Сейчас.AddDays(-400));

        await using var служба = Служба(null);
        служба.Sweep();

        Assert.True(Есть(старый));
    }

    [Fact]
    public void Ключи_хранения_наблюдателя()
    {
        var ключ = Path.Combine(_каталог, "observer.key");
        File.WriteAllText(ключ, "секрет");

        var (безКлючей, _) = ObserverOptions.Parse(["--key-file", ключ]);
        var (одинКлюч, _) = ObserverOptions.Parse(["--key-file", ключ, "--keep-days", "2"]);
        var (плохой, ошибка) = ObserverOptions.Parse(["--key-file", ключ, "--keep-mb", "-5"]);

        Assert.Null(безКлючей!.Retention);
        Assert.Equal(InstalledSettings.Default.Retention with { Age = TimeSpan.FromDays(2) }, одинКлюч!.Retention);
        Assert.Null(плохой);
        Assert.Contains("--keep-mb", ошибка!, StringComparison.Ordinal);
    }

    [Fact]
    public void Сторож_передаёт_наблюдателю_настройки_и_раскладку()
    {
        var раскладка = new InstalledLayout(
            Path.Combine(_каталог, "settings.json"), Path.Combine(_каталог, "observer.key"),
            Path.Combine(_каталог, "sessions"), Path.Combine(_каталог, "watchdog.jsonl"));
        File.WriteAllText(раскладка.KeyFile, "секрет");

        var ключи = Watchdog.ObserverArguments(InstalledSettings.Default, раскладка);
        var (options, ошибка) = ObserverOptions.Parse(ключи);

        Assert.Null(ошибка);
        Assert.Equal(IPAddress.Loopback, options!.Address);
        Assert.Equal(8100, options.Port);
        Assert.Equal(раскладка.DataDirectory, options.DataDirectory);
        Assert.Equal(InstalledSettings.Default.Retention, options.Retention);
    }

    [Fact]
    public void Ключ_создаётся_один_раз_и_не_меняется()
    {
        var раскладка = new InstalledLayout("", Path.Combine(_каталог, "профиль", "observer.key"), "", "");

        var первый = раскладка.EnsureKey();
        var второй = раскладка.EnsureKey();

        Assert.Equal(64, первый.Length);
        Assert.Equal(первый, второй);
        Assert.Equal(первый, раскладка.ReadKey());
    }
}
