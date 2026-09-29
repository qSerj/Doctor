using PsDoctor.Infrastructure.Observation;
using Xunit;

namespace PsDoctor.Infrastructure.Tests;

/// <summary>Порядок поиска ProShow (Э6.2, часть Д) без Windows: ассоциацию, места и файлы задаёт тест.</summary>
public sealed class ProShowLocatorTests
{
    private const string Обычное = @"C:\Program Files (x86)\Photodex\ProShow Producer\proshow.exe";
    private const string Другое = @"C:\Program Files\Photodex\ProShow Producer\proshow.exe";
    private const string Своё = @"D:\ProShow\proshow.exe";

    private static readonly string[] Места = [Обычное, Другое];

    private static Func<string, bool> Есть(params string[] файлы) => путь => файлы.Contains(путь);

    [Fact]
    public void Путь_из_настроек_берётся_как_есть_даже_без_файла()
    {
        var найдено = ProShowLocator.Choose(Своё, Обычное, Места, Обычное, Есть(Обычное));

        Assert.Equal(new ProgramLocation(Своё, ProShowLocator.Settings, false), найдено);
    }

    [Fact]
    public void Без_настроек_ассоциация_если_это_proshow_exe_и_он_есть()
    {
        Assert.Equal(new ProgramLocation(Своё, ProShowLocator.Association, true),
            ProShowLocator.Choose(null, Своё, Места, Обычное, Есть(Своё, Обычное)));
        // Пробелы в настройках — как нет пути; имя сверяется без учёта регистра.
        const string заглавными = @"D:\ProShow\ProShow.EXE";
        Assert.Equal(new ProgramLocation(заглавными, ProShowLocator.Association, true),
            ProShowLocator.Choose("  ", заглавными, Места, Обычное, Есть(заглавными)));
    }

    [Fact]
    public void Чужая_или_пропавшая_программа_ассоциации_пропускается()
    {
        Assert.Equal(new ProgramLocation(Другое, ProShowLocator.ProgramFiles, true),
            ProShowLocator.Choose(null, @"C:\Program Files\Viewer\viewer.exe", Места, Обычное, Есть(@"C:\Program Files\Viewer\viewer.exe", Другое)));
        Assert.Equal(new ProgramLocation(Другое, ProShowLocator.ProgramFiles, true),
            ProShowLocator.Choose(null, Своё, Места, Обычное, Есть(Другое)));
    }

    [Fact]
    public void Места_по_порядку_а_без_них_обычное_место_даже_без_файла()
    {
        Assert.Equal(new ProgramLocation(Обычное, ProShowLocator.ProgramFiles, true),
            ProShowLocator.Choose(null, null, Места, Обычное, Есть(Обычное, Другое)));
        Assert.Equal(new ProgramLocation(Обычное, ProShowLocator.Default, false),
            ProShowLocator.Choose(null, null, Места, Обычное, Есть()));
    }
}
