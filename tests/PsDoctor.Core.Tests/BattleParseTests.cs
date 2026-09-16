using PsDoctor.Core.Format;
using PsDoctor.Infrastructure;
using Xunit;

namespace PsDoctor.Core.Tests;

/// <summary>
/// Инварианты разбора на настоящих проектах: то, что обязано быть верным для любого файла шоу,
/// а не числа конкретной клиентской работы. Числа проверяются отдельно и живут рядом с проектом,
/// а не в коде открытого репозитория.
/// </summary>
public sealed class BattleParseTests
{
    [Fact]
    public void Каждый_боевой_проект_разбирается_без_единой_проблемы()
    {
        var paths = BattleProject.FindShowFiles();
        if (paths.Count == 0)
        {
            return;
        }

        for (var i = 0; i < paths.Count; i++)
        {
            var result = Parse(paths[i]);

            Assert.True(result.MagicMatched, BattleProject.Label(i) + ": сигнатура не совпала");
            Assert.NotNull(result.Document);

            // Проблема разбора — это не находка в проекте, а сигнал, что формат понят хуже, чем кажется.
            // Поэтому перечисляем её целиком: без текста непонятно, что именно разошлось.
            Assert.True(
                result.Problems.Count == 0,
                BattleProject.Label(i) + ": " + string.Join("; ", result.Problems.Take(5)));
        }
    }

    [Fact]
    public void В_каждом_боевом_проекте_счётчики_сходятся_с_индексами()
    {
        var paths = BattleProject.FindShowFiles();
        if (paths.Count == 0)
        {
            return;
        }

        for (var i = 0; i < paths.Count; i++)
        {
            var document = Parse(paths[i]).Document!;
            var slides = document.Root.Array("cell");

            Assert.NotNull(slides);
            Assert.Equal(document.Root.Count("cells"), slides.Items.Count);
            Assert.True(slides.IsDense, BattleProject.Label(i) + ": слайды идут с дырами");

            foreach (var slide in slides.Items)
            {
                Assert.Equal(slide.Count("nrOfImages"), slide.Items("images").Count);

                var transition = slide.Child("customTransition");
                if (transition is not null)
                {
                    Assert.Equal(transition.Count("nrOfImages"), transition.Items("images").Count);
                }

                foreach (var layer in slide.Items("images"))
                {
                    Assert.Equal(layer.Count("nrOfKeyframes"), layer.Items("keyframes").Count);
                }
            }
        }
    }

    [Fact]
    public void Разбор_боевого_проекта_не_теряет_ни_одной_строки()
    {
        var paths = BattleProject.FindShowFiles();
        if (paths.Count == 0)
        {
            return;
        }

        for (var i = 0; i < paths.Count; i++)
        {
            var document = Parse(paths[i]).Document!;

            // Сигнатура — единственная строка файла, которая ключом не становится.
            Assert.Equal(document.LineCount - 1, document.KeyCount);
        }
    }

    private static ParseResult Parse(string path)
    {
        using var reader = ShowFileEncoding.OpenRead(path);
        return ShowFileParser.Parse(reader);
    }
}
