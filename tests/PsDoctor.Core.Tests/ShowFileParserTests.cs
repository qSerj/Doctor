using PsDoctor.Core.Format;
using Xunit;

namespace PsDoctor.Core.Tests;

public sealed class ShowFileParserTests
{
    [Fact]
    public void Знак_равенства_внутри_значения_режется_по_первому()
    {
        var document = SyntheticShow.Document("ctaURL=http://example.invalid/?a=1&b=2");

        Assert.Equal("http://example.invalid/?a=1&b=2", document.Root.Scalar("ctaURL")?.Raw);
    }

    [Fact]
    public void Сигнатура_сама_содержит_знак_равенства_и_ключом_не_становится()
    {
        var document = SyntheticShow.Document("title=шоу");

        Assert.Single(document.Root.Scalars);
        Assert.Equal(1, document.KeyCount);
    }

    [Fact]
    public void Пустое_значение_кавычки_и_отсутствие_ключа_различаются()
    {
        var document = SyntheticShow.Document("description=\"\"", "notes=");

        Assert.Equal("\"\"", document.Root.Scalar("description")?.Raw);
        Assert.Equal(string.Empty, document.Root.Scalar("description")?.AsText());
        Assert.Equal(string.Empty, document.Root.Scalar("notes")?.Raw);
        Assert.Null(document.Root.Scalar("title"));
    }

    [Fact]
    public void Отрицательное_значение_разбирается()
    {
        var document = SyntheticShow.Document("attributeMask=-1");

        Assert.Equal(-1, document.Root.Int("attributeMask"));
    }

    [Fact]
    public void Признак_понимает_только_единицу_и_ноль()
    {
        var document = SyntheticShow.Document("isVideo=1", "useGradient=0", "sizeMode=3");

        Assert.True(document.Root.Flag("isVideo"));
        Assert.False(document.Root.Flag("useGradient"));
        Assert.Null(document.Root.Flag("sizeMode"));
    }

    [Fact]
    public void Индексированный_лист_несёт_собственное_значение()
    {
        var document = SyntheticShow.Document(
            "cells=1",
            "cell[0].mappedCaptions=1",
            "cell[0].mappedCaption[0]=7");

        var slide = document.Root.Items("cell")[0];
        var mapped = slide.Items("mappedCaption")[0];

        Assert.Equal("7", mapped.OwnValue?.Raw);
    }

    [Fact]
    public void Разреженные_индексы_сохраняют_номера_и_не_сдвигаются()
    {
        // Средний элемент не записан, потому что у него все значения умолчальные.
        // Это норма формата: молча сдвинуть индексы значило бы соврать в адресе находки.
        var result = SyntheticShow.Parse("cells=3", "cell[0].time=100", "cell[2].time=300");
        var cells = result.Document!.Root.Array("cell")!;

        Assert.Equal(2, cells.Items.Count);
        Assert.Null(cells.At(1));
        Assert.Equal(300, cells.At(2)?.Int("time"));
        Assert.False(cells.IsDense);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void Счётчик_больше_набора_проблемой_не_считается()
    {
        // `nrOfParameters=3` при двух записанных параметрах встречается в живом материале:
        // третий параметр целиком умолчальный, а умолчальное в этом формате не пишется.
        var result = SyntheticShow.Parse(
            "modifierCount=1",
            "modifier[0].nrOfActions=1",
            "modifier[0].action[0].nrOfParameters=3",
            "modifier[0].action[0].parameter[0].constant=500",
            "modifier[0].action[0].parameter[1].constant=7000");

        Assert.Empty(result.Problems);
    }

    [Fact]
    public void Слоты_модификаторов_дырами_не_считаются()
    {
        // У `modifiers` счётчика нет: индекс там — номер слота модифицируемого атрибута,
        // и у одного слоя законно встречаются слоты 0 и 3 без промежуточных.
        var result = SyntheticShow.Parse(
            "cells=1",
            "cell[0].nrOfImages=1",
            "cell[0].images[0].modifiers[0].id=565",
            "cell[0].images[0].modifiers[3].id=566");

        Assert.Empty(result.Problems);
    }

    [Fact]
    public void Кривой_ключ_даёт_проблему_а_не_исключение()
    {
        var result = SyntheticShow.Parse("cell[x].time=100", "title=шоу");

        Assert.Contains(result.Problems, p => p.Kind == ParseProblemKind.MalformedKey);
        Assert.Equal("шоу", result.Document!.Root.Text("title"));
    }

    [Fact]
    public void Строка_без_знака_равенства_даёт_проблему()
    {
        var result = SyntheticShow.Parse("просто строка");

        Assert.Contains(result.Problems, p => p.Kind == ParseProblemKind.NoSeparator);
    }

    [Fact]
    public void Пустая_строка_внутри_файла_даёт_проблему()
    {
        var result = SyntheticShow.Parse("title=шоу", string.Empty, "cells=0");

        Assert.Contains(result.Problems, p => p.Kind == ParseProblemKind.BlankLine);
    }

    [Fact]
    public void Повторный_ключ_побеждает_последним_и_записан_проблемой()
    {
        var result = SyntheticShow.Parse("title=первое", "title=второе");

        Assert.Equal("второе", result.Document!.Root.Text("title"));
        Assert.Contains(result.Problems, p => p.Kind == ParseProblemKind.DuplicateKey);
    }

    [Fact]
    public void Повторный_ключ_с_тем_же_значением_не_противоречие()
    {
        // По хранилищу владельца — 390 повторов `shadowColor` в 32 файлах, все с совпавшим
        // значением. Мешать их с настоящим противоречием значит записать три десятка
        // здоровых проектов в подозреваемые.
        var result = SyntheticShow.Parse("title=одно", "title=одно");

        Assert.Equal("одно", result.Document!.Root.Text("title"));
        Assert.Contains(result.Problems, p => p.Kind == ParseProblemKind.RepeatedKey);
        Assert.DoesNotContain(result.Problems, p => p.Kind == ParseProblemKind.DuplicateKey);
    }

    [Fact]
    public void Повторный_элемент_массива_с_тем_же_значением_не_противоречие()
    {
        var result = SyntheticShow.Parse("cells=1", "cell[0]=8", "cell[0]=8");

        Assert.Contains(result.Problems, p => p.Kind == ParseProblemKind.RepeatedKey);
        Assert.DoesNotContain(result.Problems, p => p.Kind == ParseProblemKind.DuplicateKey);
    }

    [Fact]
    public void Конфликт_листа_и_узла_записан_проблемой()
    {
        var result = SyntheticShow.Parse("sound=2", "sound.file=audio/x.mp3");

        Assert.Contains(result.Problems, p => p.Kind == ParseProblemKind.LeafNodeConflict);
    }

    [Fact]
    public void Индекс_за_пределами_счётчика_записан_проблемой()
    {
        // Файл несёт слайд, которого его собственный счётчик не признаёт. Вот это противоречие.
        var result = SyntheticShow.Parse("cells=1", "cell[0].time=100", "cell[2].time=300");

        var problem = Assert.Single(result.Problems, p => p.Kind == ParseProblemKind.CountMismatch);
        Assert.Equal("cells", problem.RawKey);
    }

    [Fact]
    public void Сошедшийся_счётчик_проблемы_не_даёт()
    {
        var result = SyntheticShow.Parse("cells=2", "cell[0].time=100", "cell[1].time=200");

        Assert.Empty(result.Problems);
    }

    [Fact]
    public void Отсутствующий_счётчик_слоёв_означает_ноль()
    {
        var document = SyntheticShow.Document("cells=1", "cell[0].time=100");
        var slide = document.Root.Items("cell")[0];

        Assert.Equal(0, slide.Count("nrOfImages"));
        Assert.Empty(slide.Items("images"));
        Assert.Null(slide.Int("nrOfImages"));
    }

    [Fact]
    public void Необязательный_ключ_без_значения_остаётся_пустым_а_не_нулём()
    {
        var document = SyntheticShow.Document("cells=1", "cell[0].time=100");
        var slide = document.Root.Items("cell")[0];

        Assert.Null(slide.Int("transId"));
        Assert.Null(slide.Flag("useCustomTransition"));
    }

    [Fact]
    public void Глубокий_путь_строит_дерево_целиком()
    {
        var document = SyntheticShow.Document(
            "cells=1",
            "cell[0].nrOfImages=1",
            "cell[0].images[0].nrOfKeyframes=1",
            "cell[0].images[0].image=image/a.png",
            "cell[0].images[0].keyframes[0].zoomX=28800");

        var layer = document.Root.Items("cell")[0].Items("images")[0];

        Assert.Equal("image/a.png", layer.Text("image"));
        Assert.Equal(28800, layer.Items("keyframes")[0].Int("zoomX"));
    }

    [Fact]
    public void Переход_несёт_собственные_слои_тем_же_устройством()
    {
        var document = SyntheticShow.Document(
            "cells=1",
            "cell[0].customTransition.nrOfImages=2",
            "cell[0].customTransition.images[0].objectId=3",
            "cell[0].customTransition.images[1].objectId=4");

        var transition = document.Root.Items("cell")[0].Child("customTransition")!;

        Assert.Equal(2, transition.Count("nrOfImages"));
        Assert.Equal(2, transition.Items("images").Count);
    }

    [Fact]
    public void Не_файл_шоу_документа_не_даёт()
    {
        using var reader = new StringReader("title=ProShow Slideshow\r\ncells=1\r\n");
        var result = ShowFileParser.Parse(reader);

        Assert.False(result.MagicMatched);
        Assert.Null(result.Document);
    }

    [Fact]
    public void Пустой_ввод_документа_не_даёт()
    {
        using var reader = new StringReader(string.Empty);
        var result = ShowFileParser.Parse(reader);

        Assert.False(result.MagicMatched);
        Assert.Null(result.Document);
    }

    [Fact]
    public void Номер_строки_запоминается_вместе_со_значением()
    {
        var document = SyntheticShow.Document("title=шоу", "cells=1", "cell[0].time=100");

        Assert.Equal(2, document.Root.Scalar("title")?.LineNumber);
        Assert.Equal(4, document.Root.Items("cell")[0].Scalar("time")?.LineNumber);
    }
}
