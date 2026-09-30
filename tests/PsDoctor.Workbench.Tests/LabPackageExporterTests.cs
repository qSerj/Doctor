using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using PsDoctor.Core.Observation;
using PsDoctor.Observer;
using PsDoctor.Observer.Client;
using Xunit;

namespace PsDoctor.Workbench.Tests;

/// <summary>Пакет сеанса из Workbench против наблюдателя на петле: слепок окружения сеанса едет в пакете (Э6.3, часть В).</summary>
public sealed class LabPackageExporterTests : IAsyncLifetime
{
    private const string Ключ = "workbench-export-key-0123456789";

    private static readonly EnvironmentFacts Окружение = new("10.0.19045.4894", "22H2", @"C:\ProShow\proshow.exe", true, "1, 0, 0, 1",
        false, 1, null, [], 16L << 30, 8L << 30, null, null, "9,00,0,3797");

    private readonly string каталог = Directory.CreateTempSubdirectory("psdoctor-пакет-").FullName;
    private readonly Запуск запуск = new() { Чужой = true, Окружение = Окружение };
    private WebApplication наблюдатель = null!;
    private ObserverClient клиент = null!;

    public async Task InitializeAsync()
    {
        наблюдатель = ObserverHost.Build(new ObserverOptions(IPAddress.Loopback, 0, Ключ, Path.Combine(каталог, "sessions")), запуск);
        await наблюдатель.StartAsync();
        клиент = new ObserverClient(new Uri(наблюдатель.Urls.Single()), Ключ);
    }

    public async Task DisposeAsync()
    {
        клиент.Dispose();
        await наблюдатель.StopAsync();
        await наблюдатель.DisposeAsync();
        Directory.Delete(каталог, recursive: true);
    }

    private async Task<string> ЗакрытыйСеанс()
    {
        var подключён = await клиент.AttachAsync();
        await клиент.StopAsync(подключён.Session);
        return подключён.Session;
    }

    private async Task<(string Пакет, JsonElement Run)> Экспорт(string сеанс)
    {
        var пакет = await new LabPackageExporter().ExportAsync(клиент, сеанс, "", "", Path.Combine(каталог, "exchange"), null, includeRaw: false);
        using var run = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(пакет, "run.json")));
        return (пакет, run.RootElement.Clone());
    }

    [Fact]
    public async Task Слепок_сеанса_ложится_в_пакет_рядом_с_журналом()
    {
        запуск.Слепок = EnvironmentSnapshot.Create(new DateTimeOffset(2026, 9, 30, 18, 0, 0, TimeSpan.Zero), 1.5,
        [
            new EnvironmentEntry(EnvironmentSections.VideoForWindows, 32, "vidc.xvid",
                new Dictionary<string, string?> { ["driver"] = "xvidvfw.dll" }, null),
        ]);
        var сеанс = await ЗакрытыйСеанс();

        var (пакет, run) = await Экспорт(сеанс);

        Assert.Equal("saved", run.GetProperty("Environment").GetString());
        var файл = Path.Combine(пакет, "results", $"environment-{запуск.Слепок.Id}.json");
        Assert.Equal(запуск.Слепок.Id, EnvironmentSnapshotJson.Deserialize(await File.ReadAllTextAsync(файл)).Id);
        // Слепок описан в манифесте, как любой файл пакета.
        Assert.Contains($"results/environment-{запуск.Слепок.Id}.json", await File.ReadAllTextAsync(Path.Combine(пакет, "manifest.json")),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Сеанс_без_слепка_даёт_пакет_без_него()
    {
        var сеанс = await ЗакрытыйСеанс();

        var (пакет, run) = await Экспорт(сеанс);

        Assert.Equal("none", run.GetProperty("Environment").GetString());
        Assert.Empty(Directory.GetFiles(Path.Combine(пакет, "results"), "environment-*.json"));
    }

    [Fact]
    public async Task Слепок_пропал_у_наблюдателя_пакет_собирается_с_пометкой()
    {
        запуск.Слепок = EnvironmentSnapshot.Create(new DateTimeOffset(2026, 9, 30, 18, 0, 0, TimeSpan.Zero), 1.5, []);
        var сеанс = await ЗакрытыйСеанс();
        File.Delete(EnvironmentFiles.Snapshot(Path.Combine(каталог, "sessions"), запуск.Слепок.Id));

        var (_, run) = await Экспорт(сеанс);

        Assert.Equal("unavailable", run.GetProperty("Environment").GetString());
    }
}
