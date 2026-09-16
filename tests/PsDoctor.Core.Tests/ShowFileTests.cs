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
    public void Каждый_боевой_проект_начинается_с_сигнатуры()
    {
        var paths = BattleProject.FindShowFiles();
        if (paths.Count == 0)
        {
            // Боевого материала рядом нет — проверять нечего.
            return;
        }

        for (var i = 0; i < paths.Count; i++)
        {
            using var reader = ShowFileEncoding.OpenRead(paths[i]);
            Assert.True(
                ShowFile.LooksLikeShowFile(reader.ReadLine() ?? string.Empty),
                BattleProject.Label(i) + ": первая строка не совпала с сигнатурой");
        }
    }
}
