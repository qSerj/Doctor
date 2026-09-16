using PsDoctor.Core.Format;
using PsDoctor.Core.Media;
using PsDoctor.Core.Model;
using PsDoctor.Infrastructure;
using Xunit;
using Учёт = PsDoctor.Core.Inventory.Inventory;

namespace PsDoctor.Core.Tests;

/// <summary>
/// Главное число этапа — сумма распакованных пикселей. Разрешений в файле шоу нет,
/// поэтому здесь впервые открываются сами медиафайлы.
/// </summary>
/// <remarks>
/// Пропуски разделены намеренно: у проекта из закрытой памяти медиа рядом нет, у проекта
/// из двора есть. Если бы пропуск был один на всё, весь боевой блок замолчал бы целиком,
/// и никто бы этого не заметил.
/// </remarks>
public sealed class BattleInventoryTests
{
    [Fact]
    public void Инвентарь_боевых_проектов_сходится_с_ожидаемым()
    {
        var checkedAny = false;

        foreach (var (path, label, expected) in Projects())
        {
            checkedAny = true;
            var inventory = Build(path);

            expected.Check("слоёвВСлайдах", inventory.Layers.InSlides, label);
            expected.Check("слоёвВПереходах", inventory.Layers.InTransitions, label);
            expected.Check("слоёвВсего", inventory.Layers.Total, label);
            expected.Check("заменяемыхМестШаблона", inventory.Layers.Replaceable.LayerTotal, label);
            expected.Check("ключевыхКадров", inventory.Keyframes.Total, label);
            expected.Check("суммаДлительностейСлайдовМс", inventory.TotalTimeMs, label);
            expected.Check("ссылокНаМедиа", inventory.Media.ReferenceCount, label);
            expected.Check("уникальныхСсылок", inventory.Media.UniqueCount, label);
            expected.Check("слоёвВидео", inventory.Layers.Video, label);
            expected.Check("слоёвСГрадиентом", inventory.Layers.Gradient, label);
            expected.Check("маскирующихСлоёв", inventory.Layers.Masking, label);
        }

        Assert.True(checkedAny || !BattleProject.IsCalibrated(), "боевого материала с ожидаемыми числами рядом нет");
    }

    [Fact]
    public void Сумма_распакованных_пикселей_сходится_с_ожидаемым()
    {
        foreach (var (path, label, expected) in Projects())
        {
            if (BattleProject.FindMediaRoot(path) is null || !expected.Has("распакованныхБайт"))
            {
                // Медиа рядом с этим проектом не приехали — мерить нечего.
                continue;
            }

            var inventory = Build(path);

            expected.Check("уникальныхКартинок", inventory.Media.ProbedCount, label);
            expected.Check("распакованныхБайт", inventory.Media.UnpackedBytes, label);
            expected.Check("сПотолком1920х1080Байт", inventory.Media.UnpackedBytesCapped, label);
        }
    }

    [Fact]
    public void Все_ссылки_боевых_проектов_находятся_на_диске()
    {
        foreach (var (path, label, _) in Projects())
        {
            if (BattleProject.FindMediaRoot(path) is null)
            {
                continue;
            }

            var inventory = Build(path);

            Assert.True(
                inventory.Media.MissingCount == 0,
                label + ": не найдено ссылок " + inventory.Media.MissingCount);

            Assert.True(
                inventory.Media.BrokenHeaderCount == 0,
                label + ": битых заголовков " + inventory.Media.BrokenHeaderCount);
        }
    }

    [Fact]
    public void Врущее_расширение_опознаётся_по_сигнатуре()
    {
        foreach (var (path, label, expected) in Projects())
        {
            if (BattleProject.FindMediaRoot(path) is null)
            {
                continue;
            }

            // Расширение врёт регулярно, и это не поломка материала, а его свойство:
            // в боевом проекте есть файлы с именем .jpg и PNG внутри. Поэтому проверяется
            // не отсутствие вранья, а то, что доктор его видит и что число не поехало.
            var lying = Build(path).Media.Items.Where(i => i.ExtensionMatchesFormat == false).ToList();

            expected.Check("ссылокСоВрущимРасширением", lying.Count, label);
        }
    }

    private static IEnumerable<(string Path, string Label, ExpectedNumbers Expected)> Projects()
    {
        var paths = BattleProject.FindShowFiles();

        for (var i = 0; i < paths.Count; i++)
        {
            if (ExpectedNumbers.Beside(paths[i]) is { } expected)
            {
                yield return (paths[i], BattleProject.Label(i), expected);
            }
        }
    }

    private static Учёт Build(string showFilePath)
    {
        using var reader = ShowFileEncoding.OpenRead(showFilePath);
        var show = Show.From(ShowFileParser.Parse(reader).Document!);

        var mediaRoot = BattleProject.FindMediaRoot(showFilePath);
        if (mediaRoot is null)
        {
            return Учёт.Build(show, MediaCatalog.Empty);
        }

        var probe = new FileMediaProbe(mediaRoot);
        var catalog = probe.ProbeAll(show.AllLayers.Select(l => l.Image).OfType<MediaReference>());

        return Учёт.Build(show, catalog);
    }
}
