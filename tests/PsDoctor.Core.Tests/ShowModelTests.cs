using PsDoctor.Core.Format;
using PsDoctor.Core.Model;
using Xunit;

namespace PsDoctor.Core.Tests;

public sealed class ShowModelTests
{
    private static Show Build(params string[] lines) => Show.From(SyntheticShow.Document(lines));

    [Fact]
    public void Слой_перехода_и_слой_слайда_один_тип_и_одна_арифметика()
    {
        var show = Build(
            "cells=1",
            "cell[0].nrOfImages=2",
            "cell[0].images[0].objectId=1",
            "cell[0].images[1].objectId=2",
            "cell[0].customTransition.nrOfImages=3",
            "cell[0].customTransition.images[0].objectId=1",
            "cell[0].customTransition.images[1].objectId=2",
            "cell[0].customTransition.images[2].objectId=3");

        Assert.Equal(5, show.AllLayers.Count());
        Assert.Equal(2, show.Slides[0].Layers.Count);
        Assert.Equal(3, show.Slides[0].Transition!.Layers.Count);
        Assert.All(show.Slides[0].Transition!.Layers, layer => Assert.Equal(SceneKind.Transition, layer.Scene));
    }

    [Fact]
    public void Длительность_фильма_складывается_из_слайдов_без_переходов()
    {
        var show = Build(
            "cells=2",
            "cell[0].time=100000",
            "cell[0].transTime=1000",
            "cell[1].time=117600",
            "cell[1].transTime=2000");

        Assert.Equal(217600, show.TotalTimeMs);
    }

    [Fact]
    public void Наибольший_зум_берётся_по_обеим_осям_и_всем_кадрам()
    {
        var show = Build(
            "cells=1",
            "cell[0].nrOfImages=1",
            "cell[0].images[0].nrOfKeyframes=3",
            "cell[0].images[0].keyframes[0].zoomX=10000",
            "cell[0].images[0].keyframes[1].zoomY=28800",
            "cell[0].images[0].keyframes[2].zoomX=15000");

        Assert.Equal(28800, show.Slides[0].Layers[0].MaxZoomBp);
    }

    [Fact]
    public void Слой_без_ключевых_кадров_наибольшего_зума_не_имеет()
    {
        var show = Build("cells=1", "cell[0].nrOfImages=1", "cell[0].images[0].objectId=1");

        Assert.Null(show.Slides[0].Layers[0].MaxZoomBp);
    }

    [Fact]
    public void Целевое_разрешение_берётся_максимумом_и_называет_ключ()
    {
        var show = Build(
            "cells=0",
            "displaySizeX=704",
            "displaySizeY=528",
            "videoSizeX=720",
            "videoSizeY=480",
            "outputImageSizeX=1024",
            "outputImageSizeY=768");

        var size = show.Header.TargetSize!;

        Assert.Equal(1024, size.WidthPx);
        Assert.Equal(768, size.HeightPx);
        Assert.Equal("outputImageSize", size.SourceKey);
    }

    [Fact]
    public void Высота_берётся_парой_к_победившему_ключу_а_не_своим_максимумом()
    {
        // У победителя высота меньше, чем у проигравшего. Отдельный максимум по высоте
        // дал бы разрешение 1024×768, которого нет ни в одном ключе.
        var show = Build("cells=0", "displaySizeX=704", "displaySizeY=768", "videoSizeX=1024", "videoSizeY=576");

        Assert.Equal(1024, show.Header.TargetSize!.WidthPx);
        Assert.Equal(576, show.Header.TargetSize!.HeightPx);
    }

    [Fact]
    public void Без_ключей_размера_целевого_разрешения_нет()
    {
        Assert.Null(Build("cells=0").Header.TargetSize);
    }

    [Fact]
    public void Перестановка_слоёв_внутри_слайда_не_меняет_адрес()
    {
        var прямой = Build(
            "cells=1",
            "cell[0].nrOfImages=2",
            "cell[0].images[0].objectId=41",
            "cell[0].images[0].image=image/a.png",
            "cell[0].images[1].objectId=42",
            "cell[0].images[1].image=image/b.png");

        var обратный = Build(
            "cells=1",
            "cell[0].nrOfImages=2",
            "cell[0].images[0].objectId=42",
            "cell[0].images[0].image=image/b.png",
            "cell[0].images[1].objectId=41",
            "cell[0].images[1].image=image/a.png");

        Assert.Equal(
            прямой.AllLayers.Select(l => l.Address.StableKey).Order(StringComparer.Ordinal),
            обратный.AllLayers.Select(l => l.Address.StableKey).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Смена_подключённого_файла_меняет_адрес()
    {
        var было = Build("cells=1", "cell[0].nrOfImages=1", "cell[0].images[0].objectId=41", "cell[0].images[0].image=image/a.png");
        var стало = Build("cells=1", "cell[0].nrOfImages=1", "cell[0].images[0].objectId=41", "cell[0].images[0].image=image/b.png");

        Assert.NotEqual(было.AllLayers.Single().Address.StableKey, стало.AllLayers.Single().Address.StableKey);
    }

    [Fact]
    public void Слой_слайда_и_слой_перехода_с_одним_objectId_различаются()
    {
        var show = Build(
            "cells=1",
            "cell[0].nrOfImages=1",
            "cell[0].images[0].objectId=3",
            "cell[0].customTransition.nrOfImages=1",
            "cell[0].customTransition.images[0].objectId=3");

        var keys = show.AllLayers.Select(l => l.Address.StableKey).ToArray();

        Assert.Equal(2, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.All(show.AllLayers, layer => Assert.Equal(AddressStability.Strong, layer.Address.Stability));
    }

    [Fact]
    public void Слой_без_objectId_получает_слабый_адрес()
    {
        var show = Build("cells=1", "cell[0].nrOfImages=1", "cell[0].images[0].image=image/a.png");

        Assert.Equal(AddressStability.Weak, show.AllLayers.Single().Address.Stability);
    }

    [Fact]
    public void Совпадение_ключей_помечает_слабыми_оба_адреса()
    {
        // Два слоя одной сцены с одним objectId и одним файлом: адрес их не различает,
        // и делать вид, что различает, нельзя.
        var show = Build(
            "cells=1",
            "cell[0].nrOfImages=2",
            "cell[0].images[0].objectId=7",
            "cell[0].images[0].image=image/a.png",
            "cell[0].images[1].objectId=7",
            "cell[0].images[1].image=image/a.png");

        Assert.All(show.AllLayers, layer => Assert.Equal(AddressStability.Weak, layer.Address.Stability));
        Assert.Equal(2, show.AllLayers.Select(l => l.Address.StableKey).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Ссылка_сравнивается_без_учёта_регистра_и_вида_разделителя()
    {
        var a = new MediaReference("image/Фото.PNG");
        var b = new MediaReference(@"image\фото.png");

        Assert.Equal(a, b);
        Assert.Equal("png", a.Extension);
        Assert.Equal("image/Фото.PNG", a.Raw);
    }

    [Fact]
    public void Обрезка_видео_считает_использованный_кусок()
    {
        var show = Build(
            "cells=1",
            "cell[0].nrOfImages=1",
            "cell[0].images[0].isVideo=1",
            "cell[0].images[0].videoStartTime=2000",
            "cell[0].images[0].videoEndTime=12100",
            "cell[0].images[0].videoLength=14700");

        Assert.Equal(10100, show.Slides[0].Layers[0].Video!.UsedMs);
    }

    [Fact]
    public void У_слоя_без_видео_обрезки_нет_вовсе()
    {
        var show = Build("cells=1", "cell[0].nrOfImages=1", "cell[0].images[0].image=image/a.png");

        Assert.Null(show.Slides[0].Layers[0].Video);
    }

    [Fact]
    public void Признаки_слоя_остаются_необъявленными_а_не_ложными()
    {
        var show = Build("cells=1", "cell[0].nrOfImages=1", "cell[0].images[0].objectId=1");
        var layer = show.Slides[0].Layers[0];

        Assert.Null(layer.IsVideo);
        Assert.Null(layer.IsMasking);
        Assert.Null(layer.IsReplaceableTemplate);
    }

    [Fact]
    public void Собственный_звук_слайда_читается_из_подузла()
    {
        var show = Build("cells=1", "cell[0].sound.file=audio/x.mp3", "cell[0].sound.volume=100");

        Assert.Equal("audio/x.mp3", show.Slides[0].Sound?.Raw);
    }

    [Fact]
    public void Разреженные_слайды_сохраняют_свои_номера_в_адресе()
    {
        var show = Build("cells=3", "cell[0].time=100", "cell[2].time=300");

        Assert.Equal([0, 2], show.Slides.Select(s => s.SlideOrdinal));
        Assert.Equal("slide:2", show.Slides[1].Address.StableKey);
    }
}
