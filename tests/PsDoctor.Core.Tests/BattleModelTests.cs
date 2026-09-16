using PsDoctor.Core.Format;
using PsDoctor.Core.Model;
using PsDoctor.Infrastructure;
using Xunit;

namespace PsDoctor.Core.Tests;

/// <summary>
/// Числа боевых проектов. Сами числа лежат рядом с проектами, здесь только их сверка —
/// и инварианты, которые верны для любого файла шоу.
/// </summary>
public sealed class BattleModelTests
{
    [Fact]
    public void Числа_боевых_проектов_сходятся()
    {
        var paths = BattleProject.FindShowFiles();
        if (paths.Count == 0)
        {
            return;
        }

        var checkedAny = false;

        for (var i = 0; i < paths.Count; i++)
        {
            var expected = ExpectedNumbers.Beside(paths[i]);
            if (expected is null)
            {
                continue;
            }

            checkedAny = true;
            var label = BattleProject.Label(i);
            var show = Load(paths[i]);

            var inSlides = show.Slides.Sum(s => s.DeclaredLayerCount);
            var inTransitions = show.Slides.Sum(s => s.Transition?.DeclaredLayerCount ?? 0);

            expected.Check("строк", show.Document.LineCount, label);
            expected.Check("слайдов", show.DeclaredSlideCount, label);
            expected.Check("слоёвВСлайдах", inSlides, label);
            expected.Check("слоёвВПереходах", inTransitions, label);
            expected.Check("слоёвВсего", inSlides + inTransitions, label);
            expected.Check("ключевыхКадров", show.AllLayers.Sum(l => l.DeclaredKeyframeCount), label);
            expected.Check("суммаДлительностейСлайдовМс", show.TotalTimeMs, label);

            // Заменяемые места считаются по слоям. Подписи несут тот же ключ и в это число не входят —
            // одно общее число заставляло бы гадать, входят они или нет.
            expected.Check(
                "заменяемыхМестШаблона",
                show.AllLayers.Count(l => l.IsReplaceableTemplate == true),
                label);

            expected.Check("слоёвВидео", show.AllLayers.Count(l => l.IsVideo == true), label);
            expected.Check("слоёвСГрадиентом", show.AllLayers.Count(l => l.UsesGradient == true), label);
            expected.Check("маскирующихСлоёв", show.AllLayers.Count(l => l.IsMasking == true), label);

            var references = show.AllLayers.Select(l => l.Image).OfType<MediaReference>().ToList();
            expected.Check("ссылокНаМедиа", references.Count, label);
            expected.Check("уникальныхСсылок", references.Distinct().Count(), label);

            expected.Check("звуковыхДорожек", show.DeclaredSoundCount, label);
            expected.Check("стилейПодписей", show.DeclaredCaptionStyleCount, label);
            expected.Check("подписей", show.Slides.Sum(s => s.DeclaredCaptionCount), label);
            expected.Check("модификаторов", show.DeclaredModifierCount, label);
        }

        Assert.True(checkedAny || !BattleProject.IsCalibrated(), "рядом с боевыми проектами нет ни одного файла " + ExpectedNumbers.FileName);
    }

    [Fact]
    public void Адреса_слоёв_боевых_проектов_различны_и_надёжны()
    {
        var paths = BattleProject.FindShowFiles();
        if (paths.Count == 0)
        {
            return;
        }

        for (var i = 0; i < paths.Count; i++)
        {
            var label = BattleProject.Label(i);
            var layers = Load(paths[i]).AllLayers.ToList();
            var keys = layers.Select(l => l.Address.StableKey).ToList();

            Assert.Equal(layers.Count, keys.Distinct(StringComparer.Ordinal).Count());

            var weak = layers.Count(l => l.Address.Stability == AddressStability.Weak);
            Assert.True(weak == 0, label + ": слабых адресов " + weak);
        }
    }

    [Fact]
    public void Слои_в_переходах_считаются_той_же_арифметикой()
    {
        var paths = BattleProject.FindShowFiles();
        if (paths.Count == 0)
        {
            return;
        }

        foreach (var path in paths)
        {
            var show = Load(path);

            // Отдельной ветки для слоёв перехода нет: полный перебор обязан совпасть
            // с суммой по сценам, иначе где-то завелось второе дерево.
            Assert.Equal(
                show.Scenes.Sum(scene => scene.Layers.Count),
                show.AllLayers.Count());
        }
    }

    [Fact]
    public void В_боевых_проектах_музыка_и_длительность_фильма_читаются_вместе()
    {
        var paths = BattleProject.FindShowFiles();
        if (paths.Count == 0)
        {
            return;
        }

        foreach (var path in paths)
        {
            var show = Load(path);

            Assert.True(show.TotalTimeMs > 0);
            Assert.All(show.Sounds, track => Assert.NotNull(track.File));
        }
    }

    private static Show Load(string path)
    {
        using var reader = ShowFileEncoding.OpenRead(path);
        return Show.From(ShowFileParser.Parse(reader).Document!);
    }
}
