using PsDoctor.Core.Media;
using PsDoctor.Core.Model;
using PsDoctor.Core.Rules;
using Xunit;
using Учёт = PsDoctor.Core.Inventory.Inventory;

namespace PsDoctor.Core.Tests;

public sealed class AcceptanceRulesTests
{
    /// <summary>Шапка с целевым разрешением 1024: максимум из трёх ключей.</summary>
    private static readonly string[] Шапка =
    [
        "displaySizeX=704",
        "displaySizeY=528",
        "videoSizeX=720",
        "videoSizeY=480",
        "outputImageSizeX=1024",
        "outputImageSizeY=768",
    ];

    private static MediaCatalog Каталог(params (string Path, int Width, int Height)[] файлы) =>
        new(файлы.ToDictionary(
            f => new MediaReference(f.Path),
            f => new MediaProbe(MediaProbeStatus.Ok, new PixelSize(f.Width, f.Height), 1_000_000, "png", true, "direct")));

    private static IReadOnlyList<Finding> Прогнать(MediaCatalog каталог, RuleContext контекст, params string[] строки)
    {
        var show = Show.From(SyntheticShow.Document([.. Шапка, .. строки]));
        return AcceptanceRules.Run(Учёт.Build(show, каталог), контекст);
    }

    private static IReadOnlyList<Finding> Прогнать(MediaCatalog каталог, params string[] строки) =>
        Прогнать(каталог, RuleContext.Bare, строки);

    private static Finding Одна(IReadOnlyList<Finding> находки, string правило) =>
        Assert.Single(находки, f => f.RuleId == правило);

    // --- oversized-stills ---

    [Fact]
    public void Негабаритная_картинка_находится_с_целевым_размером()
    {
        // Вывод 1024, зум 100%, запас ×1.5 — значит нужно 1536, а в файле 3840.
        var находка = Одна(
            Прогнать(
                Каталог(("image/a.png", 3840, 2160)),
                "cells=1",
                "cell[0].nrOfImages=1",
                "cell[0].images[0].objectId=1",
                "cell[0].images[0].image=image/a.png",
                "cell[0].images[0].nrOfKeyframes=1",
                "cell[0].images[0].keyframes[0].zoomX=10000"),
            "oversized-stills");

        Assert.Equal(1536, находка.Numbers["targetWidthPx"]);
        Assert.Equal(3840, находка.Numbers["currentWidthPx"]);
        Assert.Equal(10000, находка.Numbers["maxZoomBp"]);
        Assert.True(находка.Numbers["savingBytes"] > 0);
        Assert.Equal(AddressKind.Media, находка.Address.Kind);
    }

    [Fact]
    public void Картинка_по_размеру_находкой_не_становится()
    {
        Assert.DoesNotContain(
            Прогнать(
                Каталог(("image/a.png", 1500, 1000)),
                "cells=1",
                "cell[0].nrOfImages=1",
                "cell[0].images[0].objectId=1",
                "cell[0].images[0].image=image/a.png"),
            f => f.RuleId == "oversized-stills");
    }

    [Fact]
    public void Больший_зум_поднимает_целевой_размер()
    {
        // Зум 28800 это 288%: нужно 1024 × 2.88 × 1.5 = 4423, и 3840 уже мало.
        var находки = Прогнать(
            Каталог(("image/a.png", 3840, 2160)),
            "cells=1",
            "cell[0].nrOfImages=1",
            "cell[0].images[0].objectId=1",
            "cell[0].images[0].image=image/a.png",
            "cell[0].images[0].nrOfKeyframes=1",
            "cell[0].images[0].keyframes[0].zoomX=28800");

        Assert.DoesNotContain(находки, f => f.RuleId == "oversized-stills");
    }

    [Fact]
    public void Зум_берётся_наибольший_по_всем_ссылающимся_слоям()
    {
        // Один слой тянет картинку до 200%, значит уменьшать надо по нему, а не по спокойному соседу.
        var находка = Одна(
            Прогнать(
                Каталог(("image/a.png", 3840, 2160)),
                "cells=1",
                "cell[0].nrOfImages=2",
                "cell[0].images[0].objectId=1",
                "cell[0].images[0].image=image/a.png",
                "cell[0].images[0].nrOfKeyframes=1",
                "cell[0].images[0].keyframes[0].zoomX=10000",
                "cell[0].images[1].objectId=2",
                "cell[0].images[1].image=image/a.png",
                "cell[0].images[1].nrOfKeyframes=1",
                "cell[0].images[1].keyframes[0].zoomX=20000"),
            "oversized-stills");

        Assert.Equal(20000, находка.Numbers["maxZoomBp"]);
        Assert.Equal(2, находка.Numbers["layerCount"]);
    }

    [Fact]
    public void Множитель_запаса_это_настройка_а_не_константа()
    {
        var строки = new[]
        {
            "cells=1",
            "cell[0].nrOfImages=1",
            "cell[0].images[0].objectId=1",
            "cell[0].images[0].image=image/a.png",
            "cell[0].images[0].nrOfKeyframes=1",
            "cell[0].images[0].keyframes[0].zoomX=10000",
        };

        var каталог = Каталог(("image/a.png", 3840, 2160));
        var сПолутора = Одна(Прогнать(каталог, строки), "oversized-stills");
        var сДвойным = Одна(Прогнать(каталог, new RuleContext(new RuleSettings(2.0)), строки), "oversized-stills");

        Assert.Equal(1536, сПолутора.Numbers["targetWidthPx"]);
        Assert.Equal(2048, сДвойным.Numbers["targetWidthPx"]);
    }

    [Fact]
    public void Без_целевого_разрешения_размерные_правила_молчат()
    {
        var show = Show.From(SyntheticShow.Document(
            "cells=1",
            "cell[0].nrOfImages=1",
            "cell[0].images[0].objectId=1",
            "cell[0].images[0].image=image/a.png"));

        var находки = AcceptanceRules.Run(
            Учёт.Build(show, Каталог(("image/a.png", 3840, 2160))),
            RuleContext.Bare);

        Assert.DoesNotContain(находки, f => f.RuleId is "oversized-stills" or "zoom-exceeds-pixels");
    }

    // --- zoom-exceeds-pixels ---

    [Fact]
    public void Нехватка_пикселей_под_зум_находится_у_слоя()
    {
        // Зум 288% при выводе 1024 требует 2949 пикселей, а в файле 1000.
        var находка = Одна(
            Прогнать(
                Каталог(("image/a.png", 1000, 800)),
                "cells=1",
                "cell[0].nrOfImages=1",
                "cell[0].images[0].objectId=7",
                "cell[0].images[0].image=image/a.png",
                "cell[0].images[0].nrOfKeyframes=1",
                "cell[0].images[0].keyframes[0].zoomX=28800"),
            "zoom-exceeds-pixels");

        Assert.Equal(2949, находка.Numbers["neededWidthPx"]);
        Assert.Equal(1000, находка.Numbers["actualWidthPx"]);
        Assert.Equal(AddressKind.Layer, находка.Address.Kind);
        Assert.Equal(7, находка.Address.ObjectId);
    }

    [Fact]
    public void Сторожевое_правило_смотрит_и_на_слои_переходов()
    {
        // Решение ревизии: проверяются все слои с растровым файлом, включая слои внутри переходов.
        var находка = Одна(
            Прогнать(
                Каталог(("image/a.png", 100, 100)),
                "cells=1",
                "cell[0].customTransition.nrOfImages=1",
                "cell[0].customTransition.images[0].objectId=3",
                "cell[0].customTransition.images[0].image=image/a.png"),
            "zoom-exceeds-pixels");

        Assert.Equal(SceneKind.Transition, находка.Address.Scene);
    }

    [Fact]
    public void Запас_в_сторожевое_правило_не_входит()
    {
        // Вопрос не «сколько взять с запасом», а «хватает ли вообще»: 1024 ровно хватает.
        Assert.DoesNotContain(
            Прогнать(
                Каталог(("image/a.png", 1024, 768)),
                "cells=1",
                "cell[0].nrOfImages=1",
                "cell[0].images[0].objectId=1",
                "cell[0].images[0].image=image/a.png"),
            f => f.RuleId == "zoom-exceeds-pixels");
    }

    // --- oversized-video ---

    [Fact]
    public void Видео_ради_куска_находится_с_потраченным_временем()
    {
        var находка = Одна(
            Прогнать(
                MediaCatalog.Empty,
                "cells=1",
                "cell[0].time=5000",
                "cell[0].nrOfImages=1",
                "cell[0].images[0].objectId=1",
                "cell[0].images[0].image=video/a.mp4",
                "cell[0].images[0].isVideo=1",
                "cell[0].images[0].videoStartTime=2000",
                "cell[0].images[0].videoEndTime=12100",
                "cell[0].images[0].videoLength=41000"),
            "oversized-video");

        Assert.Equal(41000, находка.Numbers["videoLengthMs"]);
        Assert.Equal(10100, находка.Numbers["usedMs"]);
        Assert.Equal(30900, находка.Numbers["wastedMs"]);
    }

    [Fact]
    public void Без_записанной_обрезки_кусок_ограничен_длительностью_слайда()
    {
        var находка = Одна(
            Прогнать(
                MediaCatalog.Empty,
                "cells=1",
                "cell[0].time=4000",
                "cell[0].nrOfImages=1",
                "cell[0].images[0].objectId=1",
                "cell[0].images[0].image=video/a.mp4",
                "cell[0].images[0].isVideo=1",
                "cell[0].images[0].videoLength=33000"),
            "oversized-video");

        Assert.Equal(4000, находка.Numbers["usedMs"]);
        Assert.Equal(29000, находка.Numbers["wastedMs"]);
    }

    [Fact]
    public void Видео_использованное_целиком_находкой_не_становится()
    {
        Assert.DoesNotContain(
            Прогнать(
                MediaCatalog.Empty,
                "cells=1",
                "cell[0].time=5000",
                "cell[0].nrOfImages=1",
                "cell[0].images[0].objectId=1",
                "cell[0].images[0].image=video/a.mp4",
                "cell[0].images[0].isVideo=1",
                "cell[0].images[0].videoStartTime=0",
                "cell[0].images[0].videoEndTime=5000",
                "cell[0].images[0].videoLength=5000"),
            f => f.RuleId == "oversized-video");
    }

    // --- cp1251-paths ---

    [Fact]
    public void Неразрешённая_ссылка_находится_и_помечает_нелатинский_путь()
    {
        var каталог = new MediaCatalog(new Dictionary<MediaReference, MediaProbe>
        {
            [new MediaReference("image/ёлка.png")] = MediaProbe.NotFound,
        });

        var находка = Одна(
            Прогнать(
                каталог,
                "cells=1",
                "cell[0].nrOfImages=1",
                "cell[0].images[0].objectId=1",
                "cell[0].images[0].image=image/ёлка.png"),
            "cp1251-paths");

        Assert.Equal(1, находка.Numbers["nonAsciiPath"]);
        Assert.Equal(1, находка.Numbers["referenceCount"]);
    }

    [Fact]
    public void Найденная_ссылка_находкой_не_становится()
    {
        Assert.DoesNotContain(
            Прогнать(
                Каталог(("image/a.png", 100, 100)),
                "cells=1",
                "cell[0].nrOfImages=1",
                "cell[0].images[0].objectId=1",
                "cell[0].images[0].image=image/a.png"),
            f => f.RuleId == "cp1251-paths");
    }

    // --- foreign-root ---

    [Fact]
    public void Чужой_корень_находится_сравнением_каталогов()
    {
        var находка = Одна(
            Прогнать(
                MediaCatalog.Empty,
                new RuleContext(RuleSettings.Default, "C:/проекты/мой"),
                "cells=0",
                "fileName=My Computer/C:/Users/чужой/Desktop/шоу/шоу.psh"),
            "foreign-root");

        Assert.Equal(AddressKind.Show, находка.Address.Kind);
        Assert.Equal(1, находка.Numbers["recordedPathIsAbsolute"]);
    }

    [Fact]
    public void Свой_каталог_чужим_корнем_не_считается()
    {
        // Иначе доктор жаловался бы и на проект, лежащий там, где его создали.
        Assert.DoesNotContain(
            Прогнать(
                MediaCatalog.Empty,
                new RuleContext(RuleSettings.Default, @"C:\проекты\мой"),
                "cells=0",
                "fileName=My Computer/C:/проекты/мой/шоу.psh"),
            f => f.RuleId == "foreign-root");
    }

    [Fact]
    public void Без_известного_каталога_правило_о_корне_молчит()
    {
        Assert.DoesNotContain(
            Прогнать(MediaCatalog.Empty, "cells=0", "fileName=My Computer/C:/Users/чужой/Desktop/шоу.psh"),
            f => f.RuleId == "foreign-root");
    }

    // --- audio-longer-than-show ---

    [Fact]
    public void Музыка_длиннее_фильма_находится()
    {
        var находка = Одна(
            Прогнать(
                MediaCatalog.Empty,
                "cells=1",
                "cell[0].time=217600",
                "sounds=1",
                "sound[0].file=audio/a.mp3",
                "sound[0].length=251520",
                "sound[0].startTime=2220",
                "sound[0].endTime=199923"),
            "audio-longer-than-show");

        Assert.Equal(251520, находка.Numbers["lengthMs"]);
        Assert.Equal(217600, находка.Numbers["showDurationMs"]);
        Assert.Equal(33920, находка.Numbers["excessMs"]);
        Assert.Equal(197703, находка.Numbers["usedMs"]);
    }

    [Fact]
    public void Короткая_дорожка_находкой_не_становится()
    {
        Assert.DoesNotContain(
            Прогнать(
                MediaCatalog.Empty,
                "cells=1",
                "cell[0].time=217600",
                "sounds=1",
                "sound[0].file=audio/a.mp3",
                "sound[0].length=61512"),
            f => f.RuleId == "audio-longer-than-show");
    }

    // --- общее ---

    [Fact]
    public void Порядок_находок_устойчив_между_прогонами()
    {
        // Список, который перетряхивается сам, сравнивать между версиями невозможно.
        var строки = new[]
        {
            "cells=1",
            "cell[0].time=1000",
            "cell[0].nrOfImages=2",
            "cell[0].images[0].objectId=1",
            "cell[0].images[0].image=image/a.png",
            "cell[0].images[1].objectId=2",
            "cell[0].images[1].image=image/b.png",
            "sounds=1",
            "sound[0].file=audio/a.mp3",
            "sound[0].length=99000",
        };

        var каталог = Каталог(("image/a.png", 3840, 2160), ("image/b.png", 3000, 2000));

        Assert.Equal(
            Прогнать(каталог, строки).Select(f => f.RuleId + "|" + f.Address.StableKey),
            Прогнать(каталог, строки).Select(f => f.RuleId + "|" + f.Address.StableKey));
    }

    [Fact]
    public void Все_правила_этапа_на_месте_и_идентификаторы_не_повторяются()
    {
        var ожидаемые = new[]
        {
            "oversized-stills",
            "zoom-exceeds-pixels",
            "oversized-video",
            "cp1251-paths",
            "foreign-root",
            "audio-longer-than-show",
        };

        Assert.Equal(ожидаемые.Order(StringComparer.Ordinal), AcceptanceRules.All.Select(r => r.Id).Order(StringComparer.Ordinal));
        Assert.Equal(AcceptanceRules.All.Count, AcceptanceRules.All.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Ни_одно_правило_порога_пока_не_имеет()
    {
        var находки = Прогнать(
            Каталог(("image/a.png", 3840, 2160)),
            "cells=1",
            "cell[0].time=1000",
            "cell[0].nrOfImages=1",
            "cell[0].images[0].objectId=1",
            "cell[0].images[0].image=image/a.png",
            "sounds=1",
            "sound[0].file=audio/a.mp3",
            "sound[0].length=99000");

        Assert.NotEmpty(находки);
        Assert.All(находки, f => Assert.False(f.PassedThreshold));
    }
}
