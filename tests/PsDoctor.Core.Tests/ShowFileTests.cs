using PsDoctor.Core;
using PsDoctor.Infrastructure;
using Xunit;

namespace PsDoctor.Core.Tests;

public sealed class ShowFileTests
{
    [Fact]
    public void Сигнатура_опознаётся_вместе_с_переводом_строки()
    {
        Assert.True(ShowFile.LooksLikeShowFile(ShowFile.Magic + "\r\n"));
    }

    [Fact]
    public void Обычная_строка_файлом_шоу_не_считается()
    {
        Assert.False(ShowFile.LooksLikeShowFile("title=ProShow Slideshow"));
    }

    [Fact]
    public void Кодировка_1251_доступна_после_регистрации_провайдера()
    {
        var bytes = new byte[] { 0xCF, 0xF0, 0xE8, 0xE2, 0xE5, 0xF2 };
        Assert.Equal("Привет", ShowFileEncoding.Cp1251.GetString(bytes));
    }

    [Fact]
    public void Боевой_проект_начинается_с_сигнатуры()
    {
        var path = BattleProject.FindShowFile();
        if (path is null)
        {
            // Закрытого хранилища рядом нет — проверять нечего.
            return;
        }

        using var reader = ShowFileEncoding.OpenRead(path);
        Assert.True(ShowFile.LooksLikeShowFile(reader.ReadLine() ?? string.Empty));
    }
}
