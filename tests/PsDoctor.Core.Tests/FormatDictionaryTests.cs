using PsDoctor.Core.Format;
using PsDoctor.Infrastructure;
using Xunit;

namespace PsDoctor.Core.Tests;

public sealed class FormatDictionaryTests
{
    /// <summary>
    /// Формы, которые доктор читает, но которых в наличном материале нет.
    /// Список именной: без него опечатка в имени ключа не проявится никак —
    /// доктор просто молча ничего не прочитает.
    /// </summary>
    private static readonly string[] ExpectedAbsent =
    [
        // Слой-коррекция внутри перехода не встретился пока ни разу. Читать этот ключ доктор
        // всё равно обязан: форматом такой слой не запрещён, а сторожевое правило проверяет
        // и слои переходов.
        //
        // Список был длиннее: пока проектов было два, казалось, что слои переходов вообще не
        // несут файлов. На семи проектах оказалось, что несут — и это ровно та поправка, ради
        // которой материала нужно больше одного проекта.
        "cell[].customTransition.images[].isAdjustmentLayer",
    ];

    [Fact]
    public void Незнакомый_ключ_попадает_в_словарь_с_пометкой_неизвестного()
    {
        // Ключ настоящий: он есть в живых файлах, но доктору пока не нужен.
        var document = SyntheticShow.Document("cells=0", "outputQuality=80");
        var dictionary = FormatDictionary.From(document);

        var known = Assert.Single(dictionary.Shapes, s => s.Shape == "cells");
        var unknown = Assert.Single(dictionary.Shapes, s => s.Shape == "outputQuality");

        Assert.True(known.Known);
        Assert.False(unknown.Known);
        Assert.Equal(1, dictionary.UnknownShapeCount);
    }

    [Fact]
    public void Индексы_из_формы_выброшены_а_вхождения_сосчитаны()
    {
        var document = SyntheticShow.Document(
            "cells=2",
            "cell[0].nrOfImages=1",
            "cell[1].nrOfImages=1",
            "cell[0].images[0].keyframes[0].zoomX=10000",
            "cell[1].images[0].keyframes[0].zoomX=28800");

        var shape = Assert.Single(
            FormatDictionary.From(document).Shapes,
            s => s.Shape == "cell[].images[].keyframes[].zoomX");

        Assert.Equal(2, shape.Count);
        Assert.Equal(ValueKind.Integer, shape.Kind);
        Assert.Equal(10000, shape.Min);
        Assert.Equal(28800, shape.Max);
    }

    [Fact]
    public void Индексированный_лист_получает_свою_форму()
    {
        var document = SyntheticShow.Document(
            "cells=1",
            "cell[0].mappedCaptions=1",
            "cell[0].mappedCaption[0]=7");

        Assert.Single(FormatDictionary.From(document).Shapes, s => s.Shape == "cell[].mappedCaption[]");
    }

    [Fact]
    public void Текстовые_и_числовые_формы_различаются()
    {
        var document = SyntheticShow.Document("cells=0", "title=шоу", "description=");
        var shapes = FormatDictionary.From(document).Shapes;

        Assert.Equal(ValueKind.Integer, Assert.Single(shapes, s => s.Shape == "cells").Kind);
        Assert.Equal(ValueKind.Text, Assert.Single(shapes, s => s.Shape == "title").Kind);
        Assert.Equal(ValueKind.Empty, Assert.Single(shapes, s => s.Shape == "description").Kind);
    }

    [Fact]
    public void Образцы_берутся_только_у_нечисловых_значений()
    {
        var document = SyntheticShow.Document("cells=0", "title=шоу", "videoFrameRate=29970");
        var shapes = FormatDictionary.From(document).Shapes;

        Assert.Equal(["шоу"], Assert.Single(shapes, s => s.Shape == "title").Samples);
        Assert.Empty(Assert.Single(shapes, s => s.Shape == "videoFrameRate").Samples);
    }

    [Fact]
    public void Каждая_известная_форма_встречается_в_боевом_материале()
    {
        var paths = BattleProject.FindShowFiles();
        if (paths.Count == 0)
        {
            return;
        }

        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            using var reader = ShowFileEncoding.OpenRead(path);
            var document = ShowFileParser.Parse(reader).Document!;
            foreach (var shape in FormatDictionary.From(document).Shapes)
            {
                present.Add(shape.Shape);
            }
        }

        var absent = ShowKnownKeys.All.Where(k => !present.Contains(k)).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(ExpectedAbsent.Order(StringComparer.Ordinal), absent);
    }

    [Fact]
    public void Незнакомых_форм_в_боевом_проекте_больше_чем_знакомых()
    {
        var paths = BattleProject.FindShowFiles();
        if (paths.Count == 0)
        {
            return;
        }

        // Это не курьёз, а мера: доктор читает малую часть формата и должен об этом знать.
        // Словарь форм и есть способ видеть остальное, не притворяясь, что его нет.
        for (var i = 0; i < paths.Count; i++)
        {
            using var reader = ShowFileEncoding.OpenRead(paths[i]);
            var dictionary = FormatDictionary.From(ShowFileParser.Parse(reader).Document!);

            Assert.True(
                dictionary.UnknownShapeCount > dictionary.Shapes.Count - dictionary.UnknownShapeCount,
                BattleProject.Label(i) + ": незнакомых форм " + dictionary.UnknownShapeCount);
        }
    }
}
