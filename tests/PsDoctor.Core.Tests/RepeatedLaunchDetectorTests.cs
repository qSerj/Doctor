using PsDoctor.Core.Observation;
using Xunit;

namespace PsDoctor.Core.Tests;

/// <summary>
/// Детектор цикла <c>qtime.exe</c> (Э4.4). Выжимки — времена запусков <c>qtime.exe</c> в секундах сеанса из пяти журналов
/// стенда 30.09.2026 (<c>~/Lab/exchange/e63-path/</c>), без путей и командных строк.
/// </summary>
public sealed class RepeatedLaunchDetectorTests
{
    private const int ProShow = 6812;

    /// <summary>Галка снята, QuickTime нет, шоу с видео открыто: 96 запусков за 73 с.</summary>
    private static readonly double[] CacheOff =
    [
        28.4, 28.4, 28.4, 28.4, 28.4, 28.4, 30.5, 30.5, 35.4, 35.4, 37.4, 37.4, 37.4, 37.4, 37.4, 37.4, 38.5, 38.5, 40.5, 40.5,
        40.5, 40.5, 40.5, 40.5, 45.6, 45.6, 45.6, 45.6, 45.6, 45.6, 50.5, 50.6, 50.6, 50.6, 50.6, 50.6, 55.7, 55.7, 55.7, 55.7,
        55.7, 55.7, 60.7, 60.7, 60.7, 60.7, 60.7, 60.7, 65.8, 65.8, 65.8, 65.8, 65.8, 65.8, 70.7, 70.7, 70.8, 70.8, 70.8, 70.8,
        75.9, 75.9, 75.9, 75.9, 75.9, 75.9, 80.8, 80.8, 80.8, 80.8, 80.8, 80.8, 86.0, 86.0, 86.0, 86.0, 86.0, 86.0, 90.9, 91.0,
        91.0, 91.0, 91.0, 91.0, 96.1, 96.1, 96.1, 96.1, 96.1, 96.1, 101.0, 101.0, 101.0, 101.0, 101.0, 101.0,
    ];

    public static TheoryData<string, double[]> НормальныеЖурналы => new()
    {
        { "cache-on: галка отмечена", [] },
        { "avoid-off: галка снята, видео добавлено в новое шоу", [36.5, 36.6, 740.8, 740.8] },
        { "qt-off: галка снята, QuickTime стоит", [29.2, 32.7, 33.8, 36.8] },
        { "qt-off-2: то же, второй проход", [32.2, 33.3, 34.3, 37.0, 223.4, 224.5, 241.0, 242.0, 243.3] },
    };

    private static Fact Запуск(long номер, double секунд, string образ = "qtime.exe", int? родитель = ProShow) =>
        new(номер, TimeSpan.FromSeconds(секунд), "сеанс", 7000 + (int)номер, ProgramFactKinds.EtwProcessStarted,
            ObservationJson.ToElement(new { image = образ, commandLine = (string?)null, parentProcessId = родитель, timeUtc = (string?)null }));

    /// <summary>Запуски вперемешку с посторонним фактом, как в живом журнале.</summary>
    private static List<EpisodeFound> Прогнать(IEnumerable<double> времена, string образ = "qtime.exe", int? родитель = ProShow)
    {
        var детектор = new RepeatedLaunchDetector(RepeatedLaunchPattern.QuickTimeLoop);
        var эпизоды = new List<EpisodeFound>();
        var номер = 0L;
        foreach (var t in времена)
        {
            var посторонний = new Fact(++номер, TimeSpan.FromSeconds(t), "сеанс", ProShow, ProgramFactKinds.FileIo, ObservationJson.Empty);
            Assert.Null(детектор.Observe(посторонний));
            if (детектор.Observe(Запуск(++номер, t, образ, родитель)) is { } эпизод)
            {
                эпизоды.Add(эпизод);
            }
        }
        return эпизоды;
    }

    [Fact]
    public void Цикл_qtime_узнаётся_один_раз_на_двадцатом_запуске()
    {
        var детектор = new RepeatedLaunchDetector(RepeatedLaunchPattern.QuickTimeLoop);
        var когда = new List<int>();
        var эпизоды = new List<EpisodeFound>();
        for (var i = 0; i < CacheOff.Length; i++)
        {
            if (детектор.Observe(Запуск(i + 1, CacheOff[i])) is { } эпизод)
            {
                эпизоды.Add(эпизод);
                когда.Add(i + 1);
            }
        }

        var найден = Assert.Single(эпизоды);
        Assert.Equal([20], когда);
        Assert.Equal("qtime-loop", найден.Pattern);
        Assert.Equal("qtime.exe", найден.Image);
        Assert.Equal(ProShow, найден.ParentProcessId);
        Assert.Equal(20, найден.Launches);
        Assert.Equal(TimeSpan.FromSeconds(60), найден.Window);
        Assert.Equal(TimeSpan.FromSeconds(28.4), найден.FirstLaunch);
    }

    [Theory]
    [MemberData(nameof(НормальныеЖурналы))]
    public void Нормальная_работа_эпизода_не_даёт(string журнал, double[] времена)
    {
        Assert.True(Прогнать(времена).Count == 0, журнал);
    }

    [Fact]
    public void Посторонние_факты_в_журнале_цикла_не_мешают()
    {
        Assert.Single(Прогнать(CacheOff));
    }

    [Fact]
    public void Другой_образ_не_считается()
    {
        Assert.Empty(Прогнать(CacheOff, образ: "device-enc.dll"));
    }

    [Fact]
    public void Образ_узнаётся_с_путём_и_в_другом_регистре()
    {
        Assert.Single(Прогнать(CacheOff, образ: @"C:\Program Files (x86)\Photodex\ProShow Producer\QTIME.EXE"));
    }

    [Fact]
    public void Медленные_запуски_вне_окна_не_копятся()
    {
        // Раз в 4 с десять минут: 150 запусков, но в любые 60 с — не больше 16.
        var времена = Enumerable.Range(0, 150).Select(i => i * 4.0);

        Assert.Empty(Прогнать(времена));
    }

    [Fact]
    public void Разные_родители_считаются_порознь()
    {
        var детектор = new RepeatedLaunchDetector(RepeatedLaunchPattern.QuickTimeLoop);
        var эпизоды = new List<EpisodeFound>();
        for (var i = 0; i < 38; i++)
        {
            // Два ProShow по 19 запусков за 19 с: вместе 38, у каждого ниже порога.
            if (детектор.Observe(Запуск(i + 1, i / 2.0, родитель: i % 2 == 0 ? 100 : 200)) is { } эпизод)
            {
                эпизоды.Add(эпизод);
            }
        }

        Assert.Empty(эпизоды);
    }

    [Fact]
    public void Родитель_без_номера_тоже_узнаётся()
    {
        var найден = Assert.Single(Прогнать(CacheOff, родитель: null));

        Assert.Null(найден.ParentProcessId);
    }
}
