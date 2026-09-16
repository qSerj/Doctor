using System.Text.Json;
using PsDoctor.Core.Format;
using PsDoctor.Core.Media;
using PsDoctor.Core.Model;
using PsDoctor.Core.Reporting;
using PsDoctor.Core.Rules;
using Xunit;
using Учёт = PsDoctor.Core.Inventory.Inventory;

namespace PsDoctor.Core.Tests;

public sealed class ReportTests
{
    private static readonly DateTimeOffset Момент = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] Образец =
    [
        "title=шоу",
        "fileName=My Computer/C:/Users/чужой/Desktop/шоу.psh",
        "displaySizeX=704",
        "displaySizeY=528",
        "videoSizeX=720",
        "videoSizeY=480",
        "outputImageSizeX=1024",
        "outputImageSizeY=768",
        "cells=1",
        "cell[0].time=5000",
        "cell[0].nrOfImages=1",
        "cell[0].images[0].objectId=41",
        "cell[0].images[0].name=слой",
        "cell[0].images[0].image=image/фото.png",
        "sounds=1",
        "sound[0].file=audio/трек.mp3",
        "sound[0].length=9000",
    ];

    private static Report Build(IValueMasker masker, params string[] lines)
    {
        var text = ShowFile.Magic + "\r\n" + string.Join("\r\n", lines.Length > 0 ? lines : Образец) + "\r\n";
        using var reader = new StringReader(text);
        var parse = ShowFileParser.Parse(reader);
        var show = Show.From(parse.Document!);

        var catalog = new MediaCatalog(new Dictionary<MediaReference, MediaProbe>
        {
            [new MediaReference("image/фото.png")] = new(MediaProbeStatus.Ok, new PixelSize(3840, 2160), 1_000_000, "png", true, "direct"),
        });

        var inventory = Учёт.Build(show, catalog);

        return ReportBuilder.Build(
            "C:/проекты/шоу.psh",
            123456,
            parse,
            inventory,
            FormatDictionary.From(parse.Document!),
            AcceptanceRules.Run(inventory, RuleContext.Bare),
            "1.2.3",
            Момент,
            masker);
    }

    private static JsonElement Json(Report report) =>
        JsonDocument.Parse(ReportWriter.ToJson(report)).RootElement;

    [Fact]
    public void В_отчёте_есть_версия_схемы_версия_доктора_и_словарь_единиц()
    {
        var json = Json(Build(PassThroughMasker.Instance));

        Assert.Equal("psdoctor.report", json.GetProperty("schema").GetString());
        Assert.Equal(1, json.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("1.2.3", json.GetProperty("doctorVersion").GetString());
        Assert.Equal(6, json.GetProperty("units").EnumerateObject().Count());
    }

    [Fact]
    public void Момент_сборки_приходит_снаружи_а_не_из_часов_ядра()
    {
        Assert.Equal(Момент, Build(PassThroughMasker.Instance).ProducedAtUtc);
    }

    [Fact]
    public void Находка_несёт_правило_адрес_уровень_уверенность_и_числа()
    {
        var findings = Json(Build(PassThroughMasker.Instance)).GetProperty("findings");

        Assert.True(findings.GetArrayLength() > 0);

        var находка = findings.EnumerateArray().Single(f => f.GetProperty("ruleId").GetString() == "oversized-stills");

        Assert.Equal("Self", находка.GetProperty("level").GetString());
        Assert.Equal("High", находка.GetProperty("confidence").GetString());
        Assert.Equal("Media", находка.GetProperty("address").GetProperty("kind").GetString());
        Assert.Equal(3840, находка.GetProperty("numbers").GetProperty("currentWidthPx").GetInt32());
    }

    [Fact]
    public void Ни_одна_находка_порога_не_проходит()
    {
        // Порогов нет ни у одного правила, и это решённое поведение, а не недоделка:
        // правило считается и пишется, но кода возврата не меняет.
        var findings = Json(Build(PassThroughMasker.Instance)).GetProperty("findings");

        Assert.All(
            findings.EnumerateArray(),
            f => Assert.False(f.GetProperty("passedThreshold").GetBoolean()));
    }

    [Fact]
    public void Идентификаторы_правил_в_обезличенном_срезе_остаются_как_есть()
    {
        // По ним сравниваются прогоны и собирается статистика — скрывать их незачем и вредно.
        var findings = Json(Build(new AliasMasker("соль"))).GetProperty("findings");

        Assert.Contains(
            findings.EnumerateArray(),
            f => f.GetProperty("ruleId").GetString() == "oversized-stills");
    }

    [Fact]
    public void Незаданное_поле_пишется_null_а_не_опускается()
    {
        // Читатель обязан отличать «доктор такого поля не знает» от «проект этого не задал».
        var json = Json(Build(PassThroughMasker.Instance));
        var slide = json.GetProperty("inventory").GetProperty("slides").GetProperty("items")[0];

        Assert.True(slide.TryGetProperty("transId", out var transId));
        Assert.Equal(JsonValueKind.Null, transId.ValueKind);

        var media = json.GetProperty("inventory").GetProperty("media");
        Assert.Equal(JsonValueKind.Null, media.GetProperty("videoUnpackedBytes").ValueKind);
        Assert.Equal("notProbed", media.GetProperty("videoProbeStatus").GetString());
    }

    [Fact]
    public void Целевое_разрешение_названо_вместе_с_ключом_и_правилом()
    {
        var header = Json(Build(PassThroughMasker.Instance)).GetProperty("header");

        Assert.Equal(1024, header.GetProperty("targetSizePx").GetProperty("widthPx").GetInt32());
        Assert.Equal("outputImageSize", header.GetProperty("targetSizeSource").GetString());
        Assert.Contains("max(", header.GetProperty("targetSizeRule").GetString());

        // Кандидаты в отчёте затем, чтобы открытый вопрос закрылся данными пачки, а не рассуждением.
        Assert.Equal(5, header.GetProperty("sizeCandidates").GetArrayLength());
    }

    [Fact]
    public void Обезличенный_отчёт_не_содержит_ни_одной_исходной_строки()
    {
        var text = ReportWriter.ToJson(Build(new AliasMasker("соль")));

        foreach (var secret in new[] { "шоу", "чужой", "фото", "трек", "слой", "проекты", "Desktop" })
        {
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Обезличивание_сохраняет_числа_и_расширения()
    {
        var json = Json(Build(new AliasMasker("соль")));
        var media = json.GetProperty("inventory").GetProperty("media");

        Assert.True(json.GetProperty("anonymized").GetBoolean());
        Assert.Equal(3840L * 2160 * 4, media.GetProperty("unpackedBytes").GetInt64());
        Assert.Equal("png", media.GetProperty("items")[0].GetProperty("extension").GetString());
        Assert.Equal(5000, json.GetProperty("inventory").GetProperty("slides").GetProperty("totalTimeMs").GetInt32());
    }

    [Fact]
    public void Один_вход_даёт_один_псевдоним_разные_входы_разные()
    {
        var masker = new AliasMasker("соль");

        Assert.Equal(masker.Mask(MaskKind.Media, "image/a.png"), masker.Mask(MaskKind.Media, "image/a.png"));
        Assert.NotEqual(masker.Mask(MaskKind.Media, "image/a.png"), masker.Mask(MaskKind.Media, "image/b.png"));
    }

    [Fact]
    public void Разная_соль_даёт_разные_псевдонимы()
    {
        Assert.NotEqual(
            new AliasMasker("одна").Mask(MaskKind.Media, "image/a.png"),
            new AliasMasker("другая").Mask(MaskKind.Media, "image/a.png"));
    }

    [Fact]
    public void В_обезличенном_словаре_нет_образцов_значений()
    {
        // Образец значения — самый вероятный канал утечки пути или имени.
        var shapes = Json(Build(new AliasMasker("соль"))).GetProperty("dictionary").GetProperty("shapes");

        Assert.All(
            shapes.EnumerateArray(),
            shape => Assert.Equal(JsonValueKind.Null, shape.GetProperty("samples").ValueKind));
    }

    [Fact]
    public void В_обычном_отчёте_образцы_на_месте()
    {
        var shapes = Json(Build(PassThroughMasker.Instance)).GetProperty("dictionary").GetProperty("shapes");
        var title = shapes.EnumerateArray().Single(s => s.GetProperty("shape").GetString() == "title");

        Assert.Equal("шоу", title.GetProperty("samples")[0].GetString());
    }

    [Fact]
    public void Адрес_в_обезличенном_срезе_строится_из_псевдонима_и_остаётся_устойчивым()
    {
        var первый = Json(Build(new AliasMasker("соль")));
        var второй = Json(Build(new AliasMasker("соль")));

        var ключ = первый.GetProperty("inventory").GetProperty("media").GetProperty("items")[0]
            .GetProperty("addresses")[0].GetProperty("stableKey").GetString();

        Assert.NotNull(ключ);
        Assert.DoesNotContain("фото", ключ, StringComparison.Ordinal);
        Assert.Equal(
            ключ,
            второй.GetProperty("inventory").GetProperty("media").GetProperty("items")[0]
                .GetProperty("addresses")[0].GetProperty("stableKey").GetString());
    }

    [Fact]
    public void Не_файл_шоу_даёт_отчёт_без_инвентаря_но_со_схемой()
    {
        var json = JsonDocument.Parse(ReportWriter.ToJson(
            ReportBuilder.NotAShowFile("C:/x.psh", 10, "1.2.3", Момент, PassThroughMasker.Instance))).RootElement;

        Assert.False(json.GetProperty("source").GetProperty("magicMatched").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("inventory").ValueKind);
        Assert.Equal("psdoctor.report", json.GetProperty("schema").GetString());
    }

    [Fact]
    public void Отчёт_умещается_в_одну_строку()
    {
        // JSON Lines: прогон пачки дописывается, режется и читается построчно.
        Assert.DoesNotContain('\n', ReportWriter.ToJson(Build(PassThroughMasker.Instance)));
    }

    [Fact]
    public void Ключ_pretty_даёт_отступы()
    {
        Assert.Contains('\n', ReportWriter.ToJson(Build(PassThroughMasker.Instance), pretty: true));
    }
}
