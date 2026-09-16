using System.Text;
using PsDoctor.Core;
using PsDoctor.Infrastructure;
using Xunit;

namespace PsDoctor.Infrastructure.Tests;

public sealed class ShowFileEncodingTests : IDisposable
{
    private readonly string _двор = Directory.CreateTempSubdirectory("psdoctor-кодировка-").FullName;

    public void Dispose() => Directory.Delete(_двор, recursive: true);

    private string Положить(string имя, byte[] содержимое)
    {
        var путь = Path.Combine(_двор, имя);
        File.WriteAllBytes(путь, содержимое);
        return путь;
    }

    [Fact]
    public void Кодировка_1251_доступна_после_регистрации_провайдера()
    {
        var bytes = new byte[] { 0xCF, 0xF0, 0xE8, 0xE2, 0xE5, 0xF2 };

        Assert.Equal("Привет", ShowFileEncoding.Cp1251.GetString(bytes));
    }

    [Fact]
    public void Файл_читается_в_1251_а_не_в_кодировке_системы()
    {
        var путь = Положить("шоу.psh", ShowFileEncoding.Cp1251.GetBytes(ShowFile.Magic + "\r\nimage=image/ёлка.png\r\n"));

        using var reader = ShowFileEncoding.OpenRead(путь);
        reader.ReadLine();

        Assert.Equal("image=image/ёлка.png", reader.ReadLine());
    }

    [Fact]
    public void Метка_порядка_байт_не_отменяет_кодировку_молча()
    {
        // Три байта в начале чужого файла не должны отменять решение «CP1251 — единственная дверь».
        // Со включённым распознаванием метки первая строка прочиталась бы как UTF-8 и сигнатура бы уцелела,
        // а вот кириллица дальше — нет. Поэтому распознавание выключено, а метка сообщается отдельно.
        var тело = ShowFileEncoding.Cp1251.GetBytes(ShowFile.Magic + "\r\nimage=image/ёлка.png\r\n");
        var путь = Положить("с-меткой.psh", [.. Encoding.UTF8.GetPreamble(), .. тело]);

        Assert.True(ShowFileEncoding.HasByteOrderMark(путь));

        using var reader = ShowFileEncoding.OpenRead(путь);

        // Метка прочитана как три обычных символа CP1251 и осталась в строке — файл не переинтерпретирован.
        var первая = reader.ReadLine();
        Assert.NotNull(первая);
        Assert.EndsWith(ShowFile.Magic, первая, StringComparison.Ordinal);
        Assert.Equal("image=image/ёлка.png", reader.ReadLine());
    }

    [Fact]
    public void Обычный_файл_метки_не_имеет()
    {
        var путь = Положить("шоу.psh", ShowFileEncoding.Cp1251.GetBytes(ShowFile.Magic + "\r\n"));

        Assert.False(ShowFileEncoding.HasByteOrderMark(путь));
    }

    [Fact]
    public void Короткий_файл_не_ломает_проверку_метки()
    {
        Assert.False(ShowFileEncoding.HasByteOrderMark(Положить("пусто.psh", [])));
        Assert.False(ShowFileEncoding.HasByteOrderMark(Положить("два.psh", [0xEF, 0xBB])));
    }
}
