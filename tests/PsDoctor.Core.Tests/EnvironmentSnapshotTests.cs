using System.Text.Json;
using PsDoctor.Core.Observation;
using Xunit;

namespace PsDoctor.Core.Tests;

public sealed class EnvironmentSnapshotTests
{
    private static readonly DateTimeOffset Снят = new(2026, 9, 30, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Собран = new(2021, 3, 4, 10, 0, 0, TimeSpan.Zero);

    private static EnvironmentEntry Фильтр(string clsid, string имя, string merit, string версия = "1.2.3.4", int вид = 32) =>
        new(EnvironmentSections.DirectShow, вид, $"video-decoders/{clsid}",
            new Dictionary<string, string?> { ["name"] = имя, ["merit"] = merit },
            new EnvironmentFile($@"C:\Windows\SysWOW64\{имя}.ax", true, версия, 1000, Собран));

    private static EnvironmentEntry КодекVfw(string имя, string dll) =>
        new(EnvironmentSections.VideoForWindows, 32, имя,
            new Dictionary<string, string?> { ["driver"] = dll },
            new EnvironmentFile($@"C:\Windows\SysWOW64\{dll}", true, "1.0", 500, Собран));

    private static EnvironmentEntry Система() =>
        new(EnvironmentSections.System, null, "windows",
            new Dictionary<string, string?> { ["build"] = "10.0.19045.4894", ["edition"] = "Professional", ["acp"] = "1251" },
            null);

    private static List<EnvironmentEntry> Стенд() =>
    [
        Система(),
        Фильтр("{AAA}", "LAV Video Decoder", "0x00800003"),
        Фильтр("{BBB}", "Microsoft DTV-DVD Video Decoder", "0x005FFFFF"),
        КодекVfw("vidc.xvid", "xvidvfw.dll"),
        КодекVfw("vidc.i420", "iyuv_32.dll"),
    ];

    [Fact]
    public void Идентификатор_не_зависит_от_порядка_записей_и_полей()
    {
        var записи = Стенд();
        var переставлено = записи.AsEnumerable().Reverse()
            .Select(e => e with { Values = e.Values.Reverse().ToDictionary(p => p.Key, p => p.Value) })
            .ToList();

        Assert.Equal(EnvironmentSnapshot.ComputeId(записи), EnvironmentSnapshot.ComputeId(переставлено));
    }

    [Fact]
    public void Идентификатор_не_зависит_от_времени_и_длительности_снятия()
    {
        var первый = EnvironmentSnapshot.Create(Снят, 1.5, Стенд());
        var второй = EnvironmentSnapshot.Create(Снят.AddDays(3), 7.0, Стенд());

        Assert.Equal(первый.Id, второй.Id);
        Assert.Equal(16, первый.Id.Length);
    }

    [Fact]
    public void Время_файла_в_другом_поясе_тот_же_идентификатор()
    {
        var записи = Стенд();
        var сдвинуто = записи.Select(e => e.File is null ? e : e with { File = e.File with { Written = e.File.Written!.Value.ToOffset(TimeSpan.FromHours(3)) } });

        Assert.Equal(EnvironmentSnapshot.ComputeId(записи), EnvironmentSnapshot.ComputeId(сдвинуто));
    }

    public static TheoryData<string> Правки() => new()
    {
        "merit", "версия", "размер", "время", "путь", "нет файла", "вид", "ключ", "раздел", "новое поле", "поле null вместо пропуска",
    };

    [Theory]
    [MemberData(nameof(Правки))]
    public void Идентификатор_меняется_от_любого_поля_записи(string правка)
    {
        var записи = Стенд();
        var фильтр = записи[1];
        var файл = фильтр.File!;
        записи[1] = правка switch
        {
            "merit" => фильтр with { Values = new Dictionary<string, string?>(фильтр.Values) { ["merit"] = "0x00600000" } },
            "версия" => фильтр with { File = файл with { Version = "1.2.3.5" } },
            "размер" => фильтр with { File = файл with { Size = 1001 } },
            "время" => фильтр with { File = файл with { Written = Собран.AddSeconds(1) } },
            "путь" => фильтр with { File = файл with { Path = @"C:\Program Files (x86)\LAV\lav.ax" } },
            "нет файла" => фильтр with { File = файл with { Exists = false } },
            "вид" => фильтр with { View = 64 },
            "ключ" => фильтр with { Key = "video-decoders/{AAB}" },
            "раздел" => фильтр with { Section = EnvironmentSections.MediaFoundation },
            "новое поле" => фильтр with { Values = new Dictionary<string, string?>(фильтр.Values) { ["clsid"] = "{AAA}" } },
            "поле null вместо пропуска" => фильтр with { Values = new Dictionary<string, string?>(фильтр.Values) { ["clsid"] = null } },
            _ => throw new ArgumentOutOfRangeException(nameof(правка)),
        };

        Assert.NotEqual(EnvironmentSnapshot.ComputeId(Стенд()), EnvironmentSnapshot.ComputeId(записи));
    }

    [Fact]
    public void Две_записи_с_одним_тождеством_ошибка()
    {
        var записи = Стенд();
        записи.Add(Фильтр("{AAA}", "Другое имя", "0x1"));

        Assert.Throws<ArgumentException>(() => EnvironmentSnapshot.Create(Снят, 0, записи));
    }

    [Fact]
    public void Одинаковый_ключ_в_разных_видах_реестра_разные_записи()
    {
        var записи = Стенд();
        записи.Add(Фильтр("{AAA}", "LAV Video Decoder", "0x00800003", вид: 64));

        var слепок = EnvironmentSnapshot.Create(Снят, 0, записи);

        Assert.Equal(6, слепок.Entries.Count);
    }

    [Fact]
    public void Записи_слепка_в_каноническом_порядке()
    {
        var слепок = EnvironmentSnapshot.Create(Снят, 0, Стенд().AsEnumerable().Reverse());

        Assert.Equal(
            ["directshow/video-decoders/{AAA}", "directshow/video-decoders/{BBB}", "system/windows", "vfw/vidc.i420", "vfw/vidc.xvid"],
            слепок.Entries.Select(e => $"{e.Section}/{e.Key}"));
    }

    [Fact]
    public void Слепок_проходит_JSON_туда_и_обратно()
    {
        var слепок = EnvironmentSnapshot.Create(Снят, 2.25, Стенд());

        var json = JsonSerializer.Serialize(слепок, ObservationJson.Options);
        var прочитан = JsonSerializer.Deserialize<EnvironmentSnapshot>(json, ObservationJson.Options)!;

        Assert.Equal(слепок.Id, прочитан.Id);
        Assert.Equal(слепок.Id, EnvironmentSnapshot.ComputeId(прочитан.Entries));
        Assert.Equal(слепок.Taken, прочитан.Taken);
        Assert.Equal(2.25, прочитан.Seconds);
        Assert.Equal(EnvironmentSnapshot.CurrentSchema, прочитан.Schema);
        Assert.True(EnvironmentComparison.Compare(слепок, прочитан).IsEmpty);
    }

    [Fact]
    public void Сравнение_называет_добавленный_фильтр_убранный_кодек_merit_и_версию_и_ничего_больше()
    {
        // Как после установки набора кодеков: новый фильтр, убранный кодек VfW, фильтр с поднятым merit и новой версией.
        var до = EnvironmentSnapshot.Create(Снят, 0, Стенд());
        var после = Стенд();
        после.RemoveAll(e => e.Key == "vidc.xvid");
        после[1] = после[1] with
        {
            Values = new Dictionary<string, string?>(после[1].Values) { ["merit"] = "0xFF800001" },
            File = после[1].File! with { Version = "0.79.2.0" },
        };
        после.Add(Фильтр("{CCC}", "ffdshow Video Decoder", "0xFF800000"));

        var разница = EnvironmentComparison.Compare(до, EnvironmentSnapshot.Create(Снят, 0, после));

        Assert.Equal(["video-decoders/{CCC}"], разница.Added.Select(e => e.Key));
        Assert.Equal(["vidc.xvid"], разница.Removed.Select(e => e.Key));
        var изменение = Assert.Single(разница.Changed);
        Assert.Equal("video-decoders/{AAA}", изменение.Key);
        Assert.Equal(
            [
                new EnvironmentFieldChange("merit", "0x00800003", "0xFF800001"),
                new EnvironmentFieldChange("file.version", "1.2.3.4", "0.79.2.0"),
            ],
            изменение.Fields);
        Assert.Equal(до.Id, разница.BeforeId);
    }

    [Fact]
    public void Одинаковые_слепки_разницы_не_дают()
    {
        var разница = EnvironmentComparison.Compare(
            EnvironmentSnapshot.Create(Снят, 0, Стенд()),
            EnvironmentSnapshot.Create(Снят.AddHours(1), 3, Стенд().AsEnumerable().Reverse()));

        Assert.True(разница.IsEmpty);
    }

    [Fact]
    public void Порядок_записей_в_слепке_на_сравнение_не_влияет()
    {
        // Слепок, собранный мимо Create, — например, прочитанный из чужого файла, — сравнивается так же.
        var до = EnvironmentSnapshot.Create(Снят, 0, Стенд());
        var после = до with { Entries = до.Entries.Reverse().ToList() };

        Assert.True(EnvironmentComparison.Compare(до, после).IsEmpty);
    }

    [Fact]
    public void Появление_файла_у_записи_видно_по_полям_файла()
    {
        var записи = Стенд();
        var без = записи.Select(e => e.Key == "vidc.xvid" ? e with { File = null } : e);

        var разница = EnvironmentComparison.Compare(EnvironmentSnapshot.Create(Снят, 0, без), EnvironmentSnapshot.Create(Снят, 0, записи));

        var изменение = Assert.Single(разница.Changed);
        Assert.Contains(new EnvironmentFieldChange("file.exists", null, "true"), изменение.Fields);
        Assert.Contains(new EnvironmentFieldChange("file.version", null, "1.0"), изменение.Fields);
    }

    [Theory]
    [InlineData("K-Lite Codec Pack 17.8.0 Mega", true)]
    [InlineData("LAV Filters 0.79.2", true)]
    [InlineData("ffdshow [rev 4533] [2013-09-01] x64", true)]
    [InlineData("QuickTime 7", true)]
    [InlineData("Adobe After Effects 2024", false)]
    [InlineData("Photodex ProShow Producer", false)]
    public void Наборы_кодеков_узнаются_по_образцам_имён(string имя, bool кодеки)
    {
        Assert.Equal(кодеки, CodecProducts.Matches(имя));
    }
}
