using PsDoctor.Core.Media;
using PsDoctor.Core.Model;
using Xunit;
using Учёт = PsDoctor.Core.Inventory.Inventory;

namespace PsDoctor.Core.Tests;

public sealed class InventoryTests
{
    private static Учёт Build(MediaCatalog media, params string[] lines) =>
        Учёт.Build(Show.From(SyntheticShow.Document(lines)), media);

    private static MediaCatalog Catalog(params (string Path, int Width, int Height)[] files) =>
        new(files.ToDictionary(
            f => new MediaReference(f.Path),
            f => new MediaProbe(MediaProbeStatus.Ok, new PixelSize(f.Width, f.Height), FileBytes: 1, FormatId: "png")));

    [Fact]
    public void Сумма_пикселей_считается_по_уникальным_файлам_а_не_по_ссылкам()
    {
        // Три слоя ссылаются на два файла. Файл грузится один раз, сколько бы слоёв его ни просило.
        var inventory = Build(
            Catalog(("image/a.png", 1000, 1000), ("image/b.png", 100, 100)),
            "cells=1",
            "cell[0].nrOfImages=3",
            "cell[0].images[0].objectId=1",
            "cell[0].images[0].image=image/a.png",
            "cell[0].images[1].objectId=2",
            "cell[0].images[1].image=image/a.png",
            "cell[0].images[2].objectId=3",
            "cell[0].images[2].image=image/b.png");

        Assert.Equal(3, inventory.Media.ReferenceCount);
        Assert.Equal(2, inventory.Media.UniqueCount);
        Assert.Equal((1000L * 1000 + 100L * 100) * 4, inventory.Media.UnpackedBytes);
    }

    [Fact]
    public void Ненайденный_файл_не_обнуляет_сумму_а_попадает_в_счётчик_пропавших()
    {
        var catalog = new MediaCatalog(new Dictionary<MediaReference, MediaProbe>
        {
            [new MediaReference("image/a.png")] = new(MediaProbeStatus.Ok, new PixelSize(100, 100), 1, "png"),
            [new MediaReference("image/b.png")] = MediaProbe.NotFound,
        });

        var inventory = Build(
            catalog,
            "cells=1",
            "cell[0].nrOfImages=2",
            "cell[0].images[0].objectId=1",
            "cell[0].images[0].image=image/a.png",
            "cell[0].images[1].objectId=2",
            "cell[0].images[1].image=image/b.png");

        Assert.Equal(100L * 100 * 4, inventory.Media.UnpackedBytes);
        Assert.Equal(1, inventory.Media.MissingCount);
        Assert.Equal(1, inventory.Media.ProbedCount);
    }

    [Fact]
    public void Потолок_сохраняет_пропорции_и_не_увеличивает_мелкое()
    {
        var inventory = Build(
            Catalog(("image/big.png", 3840, 2160), ("image/small.png", 400, 300)),
            "cells=1",
            "cell[0].nrOfImages=2",
            "cell[0].images[0].objectId=1",
            "cell[0].images[0].image=image/big.png",
            "cell[0].images[1].objectId=2",
            "cell[0].images[1].image=image/small.png");

        // 3840×2160 вписывается в 1920×1080 ровно; 400×300 меньше потолка и не трогается.
        Assert.Equal((1920L * 1080 + 400L * 300) * 4, inventory.Media.UnpackedBytesCapped);
    }

    [Fact]
    public void Потолок_считает_по_узкой_стороне()
    {
        var inventory = Build(
            Catalog(("image/tall.png", 1000, 4000)),
            "cells=1",
            "cell[0].nrOfImages=1",
            "cell[0].images[0].objectId=1",
            "cell[0].images[0].image=image/tall.png");

        // Ограничивает высота: 1080/4000 = 0.27, ширина становится 270.
        Assert.Equal(270L * 1080 * 4, inventory.Media.UnpackedBytesCapped);
    }

    [Fact]
    public void У_каждого_файла_перечислены_адреса_ссылающихся_слоёв()
    {
        var inventory = Build(
            Catalog(("image/a.png", 10, 10)),
            "cells=1",
            "cell[0].nrOfImages=2",
            "cell[0].images[0].objectId=41",
            "cell[0].images[0].image=image/a.png",
            "cell[0].images[1].objectId=42",
            "cell[0].images[1].image=image/a.png");

        var item = Assert.Single(inventory.Media.Items);

        Assert.Equal(2, item.ReferenceCount);
        Assert.Equal(2, item.Addresses.Count);
        Assert.All(item.Addresses, address => Assert.Equal(AddressKind.Layer, address.Kind));
    }

    [Fact]
    public void Заменяемые_места_разложены_по_трём_числам()
    {
        var inventory = Build(
            MediaCatalog.Empty,
            "cells=1",
            "cell[0].nrOfImages=1",
            "cell[0].images[0].objectId=1",
            "cell[0].images[0].replaceableTemplate=1",
            "cell[0].customTransition.nrOfImages=2",
            "cell[0].customTransition.images[0].objectId=1",
            "cell[0].customTransition.images[0].replaceableTemplate=1",
            "cell[0].customTransition.images[1].objectId=2",
            "cell[0].customTransition.images[1].replaceableTemplate=1",
            "cell[0].captions=1",
            "cell[0].caption[0].internalId=5",
            "cell[0].caption[0].replaceableTemplate=1");

        var replaceable = inventory.Layers.Replaceable;

        Assert.Equal(1, replaceable.InSlides);
        Assert.Equal(2, replaceable.InTransitions);
        Assert.Equal(1, replaceable.InCaptions);
        Assert.Equal(3, replaceable.LayerTotal);
    }

    [Fact]
    public void Ссылки_разложены_по_расширениям()
    {
        var inventory = Build(
            MediaCatalog.Empty,
            "cells=1",
            "cell[0].nrOfImages=3",
            "cell[0].images[0].objectId=1",
            "cell[0].images[0].image=image/a.PNG",
            "cell[0].images[1].objectId=2",
            "cell[0].images[1].image=image/b.png",
            "cell[0].images[2].objectId=3",
            "cell[0].images[2].image=video/c.mp4");

        Assert.Equal(2, inventory.Media.ReferencesByExtension["png"]);
        Assert.Equal(1, inventory.Media.ReferencesByExtension["mp4"]);
    }

    [Fact]
    public void Неопрошенное_считается_отдельно_от_ненайденного()
    {
        var inventory = Build(
            MediaCatalog.Empty,
            "cells=1",
            "cell[0].nrOfImages=1",
            "cell[0].images[0].objectId=1",
            "cell[0].images[0].image=video/a.mp4");

        Assert.Equal(1, inventory.Media.NotProbedCount);
        Assert.Equal(0, inventory.Media.MissingCount);
        Assert.Equal(0, inventory.Media.UnpackedBytes);
    }

    [Fact]
    public void Длительности_перехода_и_фильма_не_смешиваются()
    {
        var inventory = Build(
            MediaCatalog.Empty,
            "cells=1",
            "cell[0].time=100000",
            "cell[0].transTime=1000",
            "cell[0].customTransition.time=3000",
            "cell[0].customTransition.nrOfImages=0");

        Assert.Equal(100000, inventory.TotalTimeMs);
        Assert.Equal(1000, inventory.TotalTransTimeMs);
        Assert.Equal(3000, inventory.TotalTransitionTimeMs);
    }

    [Fact]
    public void Гарнитуры_собираются_из_подписей_и_из_библиотеки()
    {
        var inventory = Build(
            MediaCatalog.Empty,
            "cells=1",
            "cell[0].captions=1",
            "cell[0].caption[0].internalId=1",
            "cell[0].caption[0].logFont.lfFaceName=Arial",
            "nrOfCaptionStyles=1",
            "captionStyle[0].caption.logFont.lfFaceName=Arial");

        var font = Assert.Single(inventory.Fonts);

        Assert.Equal("Arial", font.FaceName);
        Assert.Equal(2, font.RefCount);
    }
}
