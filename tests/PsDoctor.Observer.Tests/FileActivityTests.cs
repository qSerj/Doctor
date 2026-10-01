using Xunit;

namespace PsDoctor.Observer.Tests;

/// <summary>Сводка файловых операций по минутам (Э6.4): одна запись на минуту, образ процесса и файл.</summary>
public sealed class FileActivityTests
{
    private static readonly DateTime Минута = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
    private const string Клип = @"C:\Проект\clip.mp4";

    [Fact]
    public void Процессы_одного_образа_с_одним_файлом_за_минуту_дают_одну_запись()
    {
        var сводка = new FileActivity();
        // ETW называет образ то с расширением, то без; регистр пути тоже не различается, как в Windows.
        сводка.Add(Минута.AddSeconds(1), 2000, "device-enc.dll", Клип, "open", 0);
        сводка.Add(Минута.AddSeconds(2), 2001, "device-enc", Клип.ToUpperInvariant(), "read", 4096);
        сводка.Add(Минута.AddSeconds(3), 2002, "DEVICE-ENC.DLL", Клип, "read", 1000);

        var запись = Assert.Single(сводка.All());

        Assert.Equal(2000, запись.ProcessId);
        Assert.Equal("device-enc.dll", запись.Image);
        Assert.Equal(Клип, запись.File);
        Assert.Equal(3, запись.Processes);
        Assert.Equal((1, 2, 5096L, 0, 0L, 0), (запись.Opens, запись.Reads, запись.ReadBytes, запись.Writes, запись.WriteBytes,
            запись.SharingViolations));
    }

    [Fact]
    public void Первая_и_последняя_операция_и_число_секунд_внутри_минуты()
    {
        var сводка = new FileActivity();
        сводка.Add(Минута.AddSeconds(5.3), 2000, "proshow.exe", Клип, "open", 0);
        сводка.Add(Минута.AddSeconds(5.9), 2000, "proshow.exe", Клип, "write", 10);
        сводка.Add(Минута.AddSeconds(40), 2000, "proshow.exe", Клип, "sharing-violation", 0);
        // События разных процессоров приходят не по порядку.
        сводка.Add(Минута.AddSeconds(2), 2000, "proshow.exe", Клип, "open", 0);

        var запись = Assert.Single(сводка.All());

        Assert.Equal(Минута.AddSeconds(2), запись.TimeUtc);
        Assert.Equal(Минута.AddSeconds(40), запись.LastUtc);
        Assert.Equal(3, запись.Seconds);
        Assert.Equal((2, 1, 10L, 1), (запись.Opens, запись.Writes, запись.WriteBytes, запись.SharingViolations));
    }

    [Fact]
    public void Другая_минута_другой_образ_или_файл_дают_новую_запись()
    {
        var сводка = new FileActivity();
        сводка.Add(Минута.AddSeconds(59.9), 2000, "proshow.exe", Клип, "open", 0);
        сводка.Add(Минута.AddSeconds(60), 2000, "proshow.exe", Клип, "open", 0);
        сводка.Add(Минута.AddSeconds(1), 2001, "fvideo.exe", Клип, "open", 0);
        сводка.Add(Минута.AddSeconds(1), 2000, "proshow.exe", @"C:\Проект\a.jpg", "open", 0);

        var записи = сводка.All();

        Assert.Equal(4, записи.Count);
        Assert.Equal(2, записи.Count(z => z.Image == "proshow.exe" && z.File == Клип));
    }

    [Fact]
    public void Образ_неизвестен_процессы_различаются_по_номеру()
    {
        var сводка = new FileActivity();
        сводка.Add(Минута, 3000, null, Клип, "open", 0);
        сводка.Add(Минута, 3001, null, Клип, "open", 0);
        сводка.Add(Минута, 3000, null, Клип, "open", 0);

        Assert.Equal([(3000, 2), (3001, 1)], сводка.All().Select(z => (z.ProcessId, z.Opens)).OrderBy(z => z.ProcessId));
    }

    [Fact]
    public void Файл_без_имени_сводится_отдельно()
    {
        var сводка = new FileActivity();
        сводка.Add(Минута, 2000, "proshow.exe", null, "read", 1);
        сводка.Add(Минута, 2000, "proshow.exe", null, "read", 2);

        var запись = Assert.Single(сводка.All());

        Assert.Null(запись.File);
        Assert.Equal(3, запись.ReadBytes);
    }

    [Fact]
    public void Минута_уходит_после_своего_конца_и_опоздания_один_раз()
    {
        var сводка = new FileActivity();
        сводка.Add(Минута.AddSeconds(30), 2000, "proshow.exe", Клип, "open", 0);
        сводка.Add(Минута.AddSeconds(61), 2000, "proshow.exe", Клип, "open", 0);

        var конец = Минута.AddMinutes(1);
        Assert.Empty(сводка.Due(конец + FileActivity.Late - TimeSpan.FromMilliseconds(1)));
        var ушла = Assert.Single(сводка.Due(конец + FileActivity.Late));
        Assert.Equal(Минута.AddSeconds(30), ушла.TimeUtc);
        Assert.Empty(сводка.Due(конец + FileActivity.Late));

        // Остановка отдаёт и незаконченную минуту.
        Assert.Equal(Минута.AddSeconds(61), Assert.Single(сводка.All()).TimeUtc);
        Assert.Empty(сводка.All());
    }

    [Fact]
    public void Опоздавшее_событие_открывает_вторую_запись_той_же_минуты()
    {
        var сводка = new FileActivity();
        сводка.Add(Минута.AddSeconds(58), 2000, "proshow.exe", Клип, "open", 0);
        Assert.Single(сводка.Due(Минута.AddMinutes(1) + FileActivity.Late));

        сводка.Add(Минута.AddSeconds(59), 2000, "proshow.exe", Клип, "read", 7);

        var опоздавшая = Assert.Single(сводка.All());
        Assert.Equal((0, 1, 7L), (опоздавшая.Opens, опоздавшая.Reads, опоздавшая.ReadBytes));
    }
}
